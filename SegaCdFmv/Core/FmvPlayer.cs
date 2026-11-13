using System.Diagnostics;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace SegaCdFmv.Core;

/// <summary>
/// Playback engine. Video thread: vblank-paced (60 or 50 Hz) scheduler that decodes master frames and runs
/// VideoFx. Audio thread: pulls from the clock, runs Rf5c164Dsp and feeds a blocking audio sink.
/// </summary>
public sealed class FmvPlayer : IDisposable
{
    public sealed class OverlayState { public volatile bool Checking; public volatile int Popup; }
    public readonly OverlayState Overlay = new();

    const double BusRate = 150 * 1024;   // 1x CD-ROM
    static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest, SKMipmapMode.None);
    static readonly SKImageInfo Info = new(Vdp.W, Vdp.H, SKColorType.Rgba8888, SKAlphaType.Opaque);

    sealed class MediaClock
    {
        readonly Stopwatch _sw = new(); double _base; bool _run;
        public bool Running { get { lock (this) return _run; } }
        public double Time { get { lock (this) return _base + (_run ? _sw.Elapsed.TotalSeconds : 0); } }
        public void Start() { lock (this) { if (_run) return; _sw.Restart(); _run = true; } }
        public void Hold() { lock (this) { if (!_run) return; _base += _sw.Elapsed.TotalSeconds; _sw.Reset(); _run = false; } }
        public void Seek(double t) { lock (this) { _base = t; if (_run) _sw.Restart(); } }
    }

    readonly IAudioSink _sink;
    readonly MediaClock _clock = new();
    readonly VideoFx _fx = new();
    readonly object _imgLock = new();
    FmvReader? _rd; Thread? _vt, _at; SKImage? _img;
    volatile bool _run, _want, _reset;

    public FmvPlayer(IAudioSink sink) { _sink = sink; }

    public string Hud { get; private set; } = "";
    public ushort[] Cram => _fx.Cram;
    public bool IsLoaded => _rd != null;
    public bool IsPlaying => _want;
    public double Position => _clock.Time;
    public double Duration => _rd == null ? 0 : _rd.H.FrameCount / (double)_rd.H.Fps;

    static bool On(Q q) => QuirkSettings.On(q);

    // ------------------------------------------------------------------ control
    public void Load(string path)
    {
        Shutdown();
        _rd = new FmvReader(path);
        _want = false; _clock.Hold(); _clock.Seek(0); _reset = true;
        _sink.Start(Rf5c164Dsp.ChipRate);
        _run = true;
        _vt = new Thread(VideoLoop) { IsBackground = true, Name = "VDP", Priority = ThreadPriority.AboveNormal };
        _at = new Thread(AudioLoop) { IsBackground = true, Name = "RF5C164", Priority = ThreadPriority.Highest };
        _vt.Start(); _at.Start();
    }

    public void Play() { if (_rd == null) return; _want = true; if (!Overlay.Checking) _clock.Start(); }
    public void Pause() { _want = false; _clock.Hold(); }
    public void Stop() { Pause(); _clock.Seek(0); _reset = true; }

    void Shutdown()
    {
        _run = false;
        _sink.Stop();
        _vt?.Join(1500); _at?.Join(1500); _vt = _at = null;
        if (_rd != null) { _rd.Dispose(); _rd = null; }
        lock (_imgLock) { _img?.Dispose(); _img = null; }
        Overlay.Checking = false; Overlay.Popup = 0; Hud = "";
    }

    public void Dispose() => Shutdown();

    /// <summary>Draws the latest frame with nearest-neighbour sampling (no smoothing, no mipmaps).</summary>
    public bool DrawFrame(SKCanvas c, SKRect dest)
    {
        lock (_imgLock)
        {
            if (_img == null) return false;
            c.DrawImage(_img, new SKRect(0, 0, Vdp.W, Vdp.H), dest, Nearest, null);
            return true;
        }
    }

    void Publish(uint[] px)
    {
        var img = SKImage.FromPixelCopy(Info, MemoryMarshal.AsBytes(px.AsSpan()));
        SKImage? old;
        lock (_imgLock) { old = _img; _img = img; }
        old?.Dispose();
    }

    // -------------------------------------------------------------------- video
    void VideoLoop()
    {
        var rd = _rd!; var h = rd.H; int N = Vdp.W * Vdp.H;
        var cand = new ushort[N]; var master = new ushort[N];
        var digital = new uint[N]; var prev = new uint[N]; var tmpA = new uint[N]; var tmpB = new uint[N];
        var rng = new Random(0x5E6A);
        var sw = Stopwatch.StartNew();
        double next = 0, credit = BusRate * 0.35, nextSkip = 0, skipUntil = 0, popNext = 0, popHide = 0, hudAt = 0;
        long vb = 0, tickLast = -1, stallUntil = 0, stallTick = -1, bus = 0;
        int shown = -1, field = 0, since = 99, verSeen = -1, drops = 0, popKind = 0;
        bool popActive = false;
        double dur = h.FrameCount / (double)h.Fps;

        while (_run)
        {
            bool pal = On(Q.TvStandard);
            double vbl = 1.0 / (pal ? 50.0 : 60.0);
            next += vbl;
            double now = sw.Elapsed.TotalSeconds;
            if (next < now - 0.1) next = now;
            while ((now = sw.Elapsed.TotalSeconds) < next) { if (next - now > 0.002) Thread.Sleep(1); else Thread.SpinWait(80); }
            vb++; field ^= 1; since++;
            credit = Math.Min(credit + BusRate * vbl, BusRate * 0.35);
            if (_reset) { _reset = false; tickLast = -1; shown = -1; verSeen = -1; since = 99; }

            // ---- hardware failures -------------------------------------------------
            if (On(Q.LaserSkip))
            {
                if (nextSkip == 0) nextSkip = now + 4 + rng.NextDouble() * 5;
                if (!Overlay.Checking && _want && now >= nextSkip)
                {
                    skipUntil = now + 0.5 + rng.NextDouble();            // 0.5 - 1.5 s
                    Overlay.Checking = true; _clock.Hold();
                }
                else if (Overlay.Checking && now >= skipUntil)
                {
                    Overlay.Checking = false; if (_want) _clock.Start();
                    nextSkip = now + 5 + rng.NextDouble() * 8;
                }
            }
            else { nextSkip = 0; if (Overlay.Checking) { Overlay.Checking = false; if (_want) _clock.Start(); } }

            if (On(Q.BackupRam))
            {
                if (popNext == 0) popNext = now + 0.5;
                if (Overlay.Popup == 0 && !popActive && now >= popNext) { popKind ^= 1; Overlay.Popup = 1 + popKind; popHide = now + 6; popActive = true; }
                else if (popActive && (Overlay.Popup == 0 || now >= popHide)) { Overlay.Popup = 0; popActive = false; popNext = now + 25; }
            }
            else { Overlay.Popup = 0; popActive = false; popNext = 0; }

            // ---- frame scheduling --------------------------------------------------
            double t = _clock.Time;
            if (_want && t >= dur) { _want = false; _clock.Hold(); _clock.Seek(0); _reset = true; continue; }

            int fps = QuirkSettings.FpsOptions[Math.Clamp(QuirkSettings.Get(Q.FpsLock), 0, 3)];
            long tick = (long)Math.Floor(t * fps + 1e-6);
            bool fresh = false;

            if (tick != tickLast)
            {
                if (On(Q.WordRamStall))
                {
                    if (stallTick != tick) { stallTick = tick; if (rng.NextDouble() < 0.14) stallUntil = vb + rng.Next(1, 5); }
                }
                else stallUntil = 0;

                if (vb >= stallUntil)
                {
                    tickLast = tick;
                    int mi = (int)Math.Min(h.FrameCount - 1, Math.Floor(tick / (double)fps * h.Fps + 1e-6));
                    if (mi != shown)
                    {
                        rd.ReadFrame(mi, cand);
                        int cost = VideoFx.Cost(cand, shown >= 0 ? master : null);
                        bool starve = On(Q.Starvation);
                        if (!starve || shown < 0 || credit >= cost)
                        {
                            if (starve) credit -= cost;
                            bus += cost; (cand, master) = (master, cand); shown = mi; fresh = true;
                        }
                        else drops++;                                    // not enough CD bandwidth: frame lost
                    }
                }
            }

            // ---- stage A (per new frame or settings change) -------------------------
            bool dirty = false;
            if (shown >= 0 && (fresh || verSeen != QuirkSettings.Version))
            {
                verSeen = QuirkSettings.Version;
                if (fresh) { Array.Copy(digital, prev, N); since = 0; }
                _fx.StageA(master, h, digital, On(Q.PostageStamp), shown);
                dirty = true;
            }

            // ---- stage B (analog / per-vblank) --------------------------------------
            bool affine = On(Q.AffineShimmer), comb = On(Q.InterlaceComb), comp = On(Q.CompositeRainbow);
            if (shown >= 0 && (dirty || affine || comb || comp))
            {
                uint[] cur = digital;
                if (affine) { _fx.Affine(cur, tmpA, t); cur = tmpA; }
                if (comb) { _fx.Comb(cur, prev, tmpB, field, since == 0); cur = tmpB; }
                if (comp) { var d = ReferenceEquals(cur, tmpA) ? tmpB : tmpA; _fx.Composite(cur, d, vb); cur = d; }
                Publish(cur);
            }

            if (now >= hudAt)
            {
                Hud = $"{fps}FPS {(pal ? "PAL" : "NTSC")}  BUS {bus * 2 / 1024.0:0}K/S  DROP {drops}";
                bus = 0; hudAt = now + 0.5;
            }
        }
    }

    // -------------------------------------------------------------------- audio
    static float Smp(short[] a, int i) => (uint)i < (uint)a.Length ? a[i] : 0;

    void AudioLoop()
    {
        const int N = 512;
        var audio = _rd!.Audio; var dsp = new Rf5c164Dsp();
        var f = new float[N]; var o = new short[N]; var sil = new short[N];
        double rate = Rf5c164Dsp.ChipRate, latency = _sink.BufferFrames / rate;
        double cursor = 0, drift = 0, driftTarget = 0;
        bool resync = true, stutter = false, driftOn = false;
        var rng = new Random(77);

        // fill the device buffer with silence so audio and clock share the same latency
        for (int k = 0; k < _sink.BufferFrames && _run; k += N) _sink.Write(sil, N);

        while (_run)
        {
            if (Overlay.Checking)                                    // laser skip: loop the last 100 ms
            {
                if (!stutter) { dsp.BeginStutter(); stutter = true; }
                dsp.FillStutter(o, N); _sink.Write(o, N); resync = true; continue;
            }
            stutter = false;
            if (!_clock.Running) { _sink.Write(sil, N); resync = true; continue; }

            bool dOn = On(Q.LipSyncDrift);
            if (dOn && !driftOn) driftTarget = 0.05 + rng.NextDouble() * 0.10;   // 50 - 150 ms
            if (!dOn) driftTarget = 0;
            driftOn = dOn;
            double stepMax = 0.006 * N / rate;                       // 6 ms of drift per second
            drift += Math.Clamp(driftTarget - drift, -stepMax, stepMax);

            double target = (_clock.Time + latency - drift) * rate;
            if (resync || Math.Abs(target - cursor) > 0.25 * rate) { cursor = target; resync = false; }
            double err = (target - cursor) / rate, speed = 1.0 + Math.Clamp(err, -0.02, 0.02);

            for (int i = 0; i < N; i++)
            {
                double p = cursor + i * speed; int i0 = (int)Math.Floor(p); float fr = (float)(p - i0);
                float a = Smp(audio, i0), b = Smp(audio, i0 + 1);
                f[i] = (a + (b - a) * fr) / 32768f;
            }
            cursor += N * speed;
            dsp.Process(f, o, N, On(Q.TvStandard));
            _sink.Write(o, N);
        }
    }
}
