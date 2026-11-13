namespace SegaCdFmv.Core;

/// <summary>Rips a video file into a .scdfmv disc image (platform specific).</summary>
public interface IMediaDecoder
{
    Task<string> ConvertAsync(Stream source, string name,
        IProgress<(double Progress, string Message)> progress, CancellationToken ct);
}

/// <summary>Blocking 16-bit mono PCM output (platform specific).</summary>
public interface IAudioSink : IDisposable
{
    int BufferFrames { get; }
    void Start(int sampleRate);
    void Write(short[] data, int count);   // blocks until the device has room
    void Stop();
}
