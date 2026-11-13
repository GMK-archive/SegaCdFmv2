using SegaCdFmv.Core;
using Am = Android.Media;

namespace SegaCdFmv.Droid;

/// <summary>Streaming 16-bit mono AudioTrack. Small buffer = low latency, so the audio clock tracks the video clock.</summary>
public sealed class AndroidAudioSink : IAudioSink
{
    Am.AudioTrack? _track;
    public int BufferFrames { get; private set; }

    public void Start(int sampleRate)
    {
        Stop();
        int min = Am.AudioTrack.GetMinBufferSize(sampleRate, Am.ChannelOut.Mono, Am.Encoding.Pcm16bit);
        int bytes = Math.Max(min, 8192);                      // about 125 ms at 32.5 kHz
        var attrs = new Am.AudioAttributes.Builder()!
            .SetUsage(Am.AudioUsageKind.Media)!
            .SetContentType(Am.AudioContentType.Music)!
            .Build()!;
        var fmt = new Am.AudioFormat.Builder()!
            .SetEncoding(Am.Encoding.Pcm16bit)!
            .SetSampleRate(sampleRate)!
            .SetChannelMask(Am.ChannelOut.Mono)!
            .Build()!;
        _track = new Am.AudioTrack(attrs, fmt, bytes, Am.AudioTrackMode.Stream, 0);
        BufferFrames = bytes / 2;
        _track.Play();
    }

    public void Write(short[] data, int count) => _track?.Write(data, 0, count);

    public void Stop()
    {
        try { _track?.Stop(); _track?.Release(); } catch { /* already released */ }
        _track = null;
    }

    public void Dispose() => Stop();
}
