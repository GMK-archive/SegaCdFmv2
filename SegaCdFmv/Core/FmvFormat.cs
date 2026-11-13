using System.IO.Compression;
using System.Runtime.InteropServices;

namespace SegaCdFmv.Core;

/// <summary>
/// .scdfmv "disc image": 64-byte header, mono 16-bit PCM @ 32552 Hz (RF5C164 native rate),
/// deflated RGB555 master frames @ 20 fps (320x224), then a (offset,length) frame index.
/// Quirks are applied live at playback, so the master keeps more than 9 bits of color for dithering.
/// </summary>
public sealed class FmvHeader
{
    public const int Size = 64;
    public ushort Fps = 20;
    public uint FrameCount, AudioRate = 32552, AudioSamples;
    public ushort CX, CY, CW = Vdp.W, CH = Vdp.H;       // content rectangle inside the 320x224 frame
    public long AudioOffset, IndexOffset;

    public void Write(BinaryWriter w)
    {
        w.Write(new[] { (byte)'S', (byte)'C', (byte)'D', (byte)'F' });
        w.Write((ushort)1); w.Write((ushort)Vdp.W); w.Write((ushort)Vdp.H); w.Write(Fps);
        w.Write(FrameCount); w.Write(AudioRate); w.Write(AudioSamples);
        w.Write(CX); w.Write(CY); w.Write(CW); w.Write(CH);
        w.Write(AudioOffset); w.Write(IndexOffset);
        w.Write(new byte[16]);
    }

    public static FmvHeader Read(BinaryReader r)
    {
        if (r.ReadByte() != 'S' || r.ReadByte() != 'C' || r.ReadByte() != 'D' || r.ReadByte() != 'F')
            throw new InvalidDataException("NOT A MEGA-CD DISC IMAGE");
        r.ReadUInt16(); r.ReadUInt16(); r.ReadUInt16();
        return new FmvHeader
        {
            Fps = r.ReadUInt16(), FrameCount = r.ReadUInt32(), AudioRate = r.ReadUInt32(), AudioSamples = r.ReadUInt32(),
            CX = r.ReadUInt16(), CY = r.ReadUInt16(), CW = r.ReadUInt16(), CH = r.ReadUInt16(),
            AudioOffset = r.ReadInt64(), IndexOffset = r.ReadInt64()
        };
    }
}

public sealed class FmvWriter : IDisposable
{
    readonly FileStream _fs; readonly BinaryWriter _bw; readonly FmvHeader _h;
    readonly List<(long Off, int Len)> _idx = new();

    public FmvWriter(string path, FmvHeader h, short[] audio)
    {
        _h = h; _fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite); _bw = new BinaryWriter(_fs);
        _fs.Position = FmvHeader.Size;
        h.AudioOffset = _fs.Position; h.AudioSamples = (uint)audio.Length;
        _bw.Write(MemoryMarshal.AsBytes(audio.AsSpan()));
    }

    public void AddFrame(ushort[] px)
    {
        using var ms = new MemoryStream(48 * 1024);
        using (var ds = new DeflateStream(ms, CompressionLevel.Fastest, true)) ds.Write(MemoryMarshal.AsBytes(px.AsSpan()));
        long off = _fs.Position;
        _bw.Write(ms.GetBuffer(), 0, (int)ms.Length);
        _idx.Add((off, (int)ms.Length));
    }

    public void Finish()
    {
        _h.FrameCount = (uint)_idx.Count; _h.IndexOffset = _fs.Position;
        foreach (var (o, l) in _idx) { _bw.Write(o); _bw.Write(l); }
        _fs.Position = 0; _h.Write(_bw); _bw.Flush();
    }

    public void Dispose() => _bw.Dispose();
}

public sealed class FmvReader : IDisposable
{
    readonly FileStream _fs; readonly long[] _off; readonly int[] _len; byte[] _buf = new byte[64 * 1024];
    public FmvHeader H { get; }
    public short[] Audio { get; }

    public FmvReader(string path)
    {
        _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(_fs, System.Text.Encoding.UTF8, true);
        H = FmvHeader.Read(br);
        _fs.Position = H.AudioOffset;
        var a = new short[H.AudioSamples];
        _fs.ReadExactly(MemoryMarshal.AsBytes(a.AsSpan()));
        Audio = a;
        _fs.Position = H.IndexOffset;
        _off = new long[H.FrameCount]; _len = new int[H.FrameCount];
        for (int i = 0; i < _off.Length; i++) { _off[i] = br.ReadInt64(); _len[i] = br.ReadInt32(); }
    }

    public void ReadFrame(int i, ushort[] dst)
    {
        int n = _len[i];
        if (_buf.Length < n) _buf = new byte[n];
        _fs.Position = _off[i]; _fs.ReadExactly(_buf, 0, n);
        using var ms = new MemoryStream(_buf, 0, n, false);
        using var ds = new DeflateStream(ms, CompressionMode.Decompress);
        var span = MemoryMarshal.AsBytes(dst.AsSpan()); int got = 0;
        while (got < span.Length) { int r = ds.Read(span.Slice(got)); if (r <= 0) break; got += r; }
    }

    public void Dispose() => _fs.Dispose();
}

/// <summary>Streaming linear resampler (converter side only, quirks never touch this).</summary>
public sealed class LinearResampler
{
    readonly double _step;
    double _nextOutput;
    long _inputIndex;
    float _prev;
    bool _started;

    public LinearResampler(int srcRate, int dstRate)
    {
        if (srcRate <= 0 || dstRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(srcRate), "Sample rates must be positive.");

        _step = (double)srcRate / dstRate;
    }

    public void Push(float x, List<short> output)
    {
        if (!_started)
        {
            _started = true;
            _prev = x;
            Emit(x, output);
            _nextOutput = _step;
            return;
        }

        long currentIndex = _inputIndex + 1;
        while (_nextOutput <= currentIndex)
        {
            double fraction = _nextOutput - _inputIndex;
            float value = _prev + (x - _prev) * (float)fraction;
            Emit(value, output);
            _nextOutput += _step;
        }

        _inputIndex = currentIndex;
        _prev = x;
    }

    static void Emit(float value, List<short> output)
        => output.Add((short)Math.Clamp(value * 32767f, -32768f, 32767f));
}
