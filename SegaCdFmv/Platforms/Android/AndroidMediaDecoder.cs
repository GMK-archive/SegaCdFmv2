using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SegaCdFmv.Core;
using Ag = Android.Graphics;
using Am = Android.Media;

namespace SegaCdFmv.Droid;

/// <summary>
/// "Disc ripper": MediaExtractor + MediaCodec for audio (mixed to mono, resampled to 32552 Hz),
/// MediaMetadataRetriever for video frames (20 fps, box-filtered into a 320x224 RGB555 master).
/// Aliases (Am / Ag) avoid the Android.Media.Stream vs System.IO.Stream and Path clashes.
/// </summary>
public sealed class AndroidMediaDecoder : IMediaDecoder
{
    const int MasterFps = 20;
    const double MaxSeconds = 180;   // longest clip ripped

    public Task<string> ConvertAsync(Stream source, string name,
        IProgress<(double Progress, string Message)> progress, CancellationToken ct)
        => Task.Run(() => Run(source, name, progress, ct), ct);

    static long ParseL(string? s) => long.TryParse(s, out var v) ? v : 0;

    static string Run(Stream source, string name, IProgress<(double Progress, string Message)> progress, CancellationToken ct)
    {
        string dir = Path.Combine(FileSystem.AppDataDirectory, "fmv");
        Directory.CreateDirectory(dir);
        string tmp = Path.Combine(FileSystem.CacheDirectory, $"disc_source_{Guid.NewGuid():N}.bin");

        try
        {
            progress.Report((0, "READING DISC..."));
            using (var fs = File.Create(tmp)) source.CopyToAsync(fs, 81920, ct).GetAwaiter().GetResult();

            ct.ThrowIfCancellationRequested();
            string key = new string(Path.GetFileNameWithoutExtension(name).Where(char.IsLetterOrDigit).Take(24).ToArray());
            if (key.Length == 0) key = "DISC";
            string hash;
            using (var input = File.OpenRead(tmp))
                hash = Convert.ToHexString(SHA256.HashData(input).AsSpan(0, 8));
            string outPath = Path.Combine(dir, $"{key}_{hash}.scdfmv");
            if (File.Exists(outPath)) { progress.Report((1, "DISC CACHED - SPINNING UP")); return outPath; }

            var audio = DecodeAudio(tmp, progress, ct);

            using var mmr = new Am.MediaMetadataRetriever();
            mmr.SetDataSource(tmp);
            double dur = ParseL(mmr.ExtractMetadata(Am.MetadataKey.Duration)) / 1000.0;
            int vw = (int)ParseL(mmr.ExtractMetadata(Am.MetadataKey.VideoWidth));
            int vh = (int)ParseL(mmr.ExtractMetadata(Am.MetadataKey.VideoHeight));
            int rot = (int)ParseL(mmr.ExtractMetadata(Am.MetadataKey.VideoRotation));
            if (vw <= 0 || vh <= 0) throw new InvalidDataException("NO VIDEO TRACK ON THIS DISC");
            if (rot % 180 != 0) (vw, vh) = (vh, vw);

            // fit the source inside a 4:3 display raster (320x224 px at the Genesis pixel aspect)
            double rel = ((double)vw / vh) / (4.0 / 3.0);
            int cw, ch;
            if (rel >= 1) { cw = Vdp.W; ch = (int)Math.Round(Vdp.H / rel); } else { ch = Vdp.H; cw = (int)Math.Round(Vdp.W * rel); }
            cw = Math.Max(16, cw & ~1); ch = Math.Max(16, ch & ~1);
            int cx = ((Vdp.W - cw) / 2) & ~1, cy = ((Vdp.H - ch) / 2) & ~1;

            int total = (int)(Math.Min(dur, MaxSeconds) * MasterFps);
            if (total < 1) throw new InvalidDataException("DISC IS EMPTY");

            var hdr = new FmvHeader { Fps = MasterFps, CX = (ushort)cx, CY = (ushort)cy, CW = (ushort)cw, CH = (ushort)ch };
            string part = outPath + ".part";
            try
            {
                using (var wr = new FmvWriter(part, hdr, audio))
                {
                    var frame = new ushort[Vdp.W * Vdp.H];          // letterbox stays black (0)
                    for (int i = 0; i < total; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        long us = (long)(i * 1_000_000.0 / MasterFps);
                        using (var bmp = mmr.GetScaledFrameAtTime(us, Am.Option.Closest, cw * 2, ch * 2))
                            if (bmp != null) Blit(bmp, frame, cx, cy, cw, ch);   // on a miss, the previous frame is repeated
                        wr.AddFrame(frame);
                        if ((i & 3) == 0) progress.Report((0.12 + 0.88 * i / total, $"RIPPING TRACK 01  {i * 100 / total}%"));
                    }
                    wr.Finish();
                }
                File.Move(part, outPath, true);
                progress.Report((1, "DISC READY"));
                return outPath;
            }
            finally
            {
                if (File.Exists(part)) File.Delete(part);
            }
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    static void Blit(Ag.Bitmap bmp, ushort[] dst, int cx, int cy, int cw, int ch)
    {
        int bw = bmp.Width, bh = bmp.Height;
        var px = new int[bw * bh];
        bmp.GetPixels(px, 0, bw, 0, 0, bw, bh);
        for (int y = 0; y < ch; y++)
        {
            int y0 = y * bh / ch, y1 = Math.Max(y0 + 1, (y + 1) * bh / ch);
            for (int x = 0; x < cw; x++)
            {
                int x0 = x * bw / cw, x1 = Math.Max(x0 + 1, (x + 1) * bw / cw), r = 0, g = 0, b = 0, n = 0;
                for (int yy = y0; yy < y1; yy++)
                    for (int xx = x0; xx < x1; xx++) { int p = px[yy * bw + xx]; r += (p >> 16) & 255; g += (p >> 8) & 255; b += p & 255; n++; }
                r /= n; g /= n; b /= n;
                dst[(cy + y) * Vdp.W + cx + x] = (ushort)(((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3));
            }
        }
    }

    static short[] DecodeAudio(string path, IProgress<(double Progress, string Message)> progress, CancellationToken ct)
    {
        var pcm = new List<short>();
        using var ex = new Am.MediaExtractor();
        ex.SetDataSource(path);

        int track = -1; Am.MediaFormat? fmt = null; string? mime = null;
        for (int i = 0; i < ex.TrackCount; i++)
        {
            var f = ex.GetTrackFormat(i);
            var m = f?.GetString(Am.MediaFormat.KeyMime);
            if (m != null && m.StartsWith("audio/")) { track = i; fmt = f; mime = m; break; }
        }
        if (track < 0 || fmt == null || mime == null) return Array.Empty<short>();   // silent disc

        ex.SelectTrack(track);
        int rate = fmt.GetInteger(Am.MediaFormat.KeySampleRate), chans = fmt.GetInteger(Am.MediaFormat.KeyChannelCount);
        var codec = Am.MediaCodec.CreateDecoderByType(mime)!;
        codec.Configure(fmt, null, null, Am.MediaCodecConfigFlags.None);
        codec.Start();

        var rs = new LinearResampler(rate, Rf5c164Dsp.ChipRate);
        var info = new Am.MediaCodec.BufferInfo();
        bool inDone = false, outDone = false;
        long maxOut = (long)(MaxSeconds * Rf5c164Dsp.ChipRate);
        try
        {
            while (!outDone && pcm.Count < maxOut)
            {
                ct.ThrowIfCancellationRequested();
                if (!inDone)
                {
                    int ii = codec.DequeueInputBuffer(10_000);
                    if (ii >= 0)
                    {
                        var ib = codec.GetInputBuffer(ii)!;
                        int n = ex.ReadSampleData(ib, 0);
                        if (n < 0) { codec.QueueInputBuffer(ii, 0, 0, 0, Am.MediaCodecBufferFlags.EndOfStream); inDone = true; }
                        else { codec.QueueInputBuffer(ii, 0, n, ex.SampleTime, 0); ex.Advance(); }
                    }
                }

                int oi = codec.DequeueOutputBuffer(info, 10_000);
                if (oi >= 0)
                {
                    if (info.Size > 0)
                    {
                        var ob = codec.GetOutputBuffer(oi)!;
                        ob.Position(info.Offset); ob.Limit(info.Offset + info.Size);
                        var bytes = new byte[info.Size];
                        ob.Get(bytes);
                        var s = MemoryMarshal.Cast<byte, short>(bytes.AsSpan());
                        int frames = s.Length / Math.Max(1, chans);
                        for (int k = 0; k < frames; k++)
                        {
                            int sum = 0;
                            for (int c = 0; c < chans; c++) sum += s[k * chans + c];
                            rs.Push(sum / (float)chans / 32768f, pcm);
                        }
                        progress.Report((Math.Min(0.11, 0.11 * pcm.Count / maxOut), "LOADING PCM..."));
                    }
                    codec.ReleaseOutputBuffer(oi, false);
                    if ((info.Flags & Am.MediaCodecBufferFlags.EndOfStream) != 0) outDone = true;
                }
                else if (oi == (int)Am.MediaCodecInfoState.OutputFormatChanged)
                {
                    var nf = codec.OutputFormat;
                    rate = nf.GetInteger(Am.MediaFormat.KeySampleRate); chans = nf.GetInteger(Am.MediaFormat.KeyChannelCount);
                    rs = new LinearResampler(rate, Rf5c164Dsp.ChipRate);
                }
            }
        }
        finally { codec.Stop(); codec.Release(); }
        return pcm.ToArray();
    }
}
