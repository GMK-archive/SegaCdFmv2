using System.Diagnostics;
using SegaCdFmv.Core;
using SkiaSharp.Views.Maui;

namespace SegaCdFmv;

public partial class MainPage : ContentPage
{
    readonly IMediaDecoder _decoder;
    readonly FmvPlayer _player;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    CancellationTokenSource? _cts;
    bool _busy;
    float _angle;
    double _spin;

    public List<QuirkGroup> Groups { get; } = QuirkCatalog.Build();

    public MainPage(IMediaDecoder decoder, IAudioSink sink)
    {
        InitializeComponent();
        _decoder = decoder;
        _player = new FmvPlayer(sink);
        BindingContext = this;

        SizeChanged += (_, _) => ScreenView.HeightRequest = Math.Max(140, (Width - 16) * 0.75 + 18);

        var timer = Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(25);
        timer.Tick += (_, _) => OnTick();
        timer.Start();
    }

    // ------------------------------------------------------------------ UI tick
    void OnTick()
    {
        double target = _player.IsPlaying && !_player.Overlay.Checking ? 540 : 0;   // deg/s, with inertia
        _spin += (target - _spin) * 0.10;
        _angle = (_angle + (float)(_spin * 0.025)) % 360f;

        if (!_busy)
        {
            double d = _player.Duration;
            Bar.Progress = d > 0 ? Math.Clamp(_player.Position / d, 0, 1) : 0;
            StatusLabel.Text = !_player.IsLoaded ? "NO DISC"
                : _player.Overlay.Checking ? "CHECKING DISC..."
                : _player.IsPlaying ? "PLAYING  TRACK 01" : "PAUSED  TRACK 01";
            PlayButton.Text = _player.IsPlaying ? "PAUSE" : "PLAY";
        }

        ScreenView.InvalidateSurface();
        DiscView.InvalidateSurface();
        LcdView.InvalidateSurface();
        CramView.InvalidateSurface();
    }

    string LcdText()
    {
        double p = _player.Position;
        int mm = (int)(p / 60), ss = (int)(p % 60), ff = (int)((p - Math.Floor(p)) * 75);   // 75 CD frames / s
        return $"01 {mm:00}:{ss:00}:{ff:00}";
    }

    // -------------------------------------------------------------- paint hooks
    void OnScreenPaint(object? s, SKPaintSurfaceEventArgs e)
        => BiosRenderer.DrawScreen(e.Surface.Canvas, e.Info, _player, _clock.Elapsed.TotalSeconds);
    void OnDiscPaint(object? s, SKPaintSurfaceEventArgs e) => BiosRenderer.DrawDisc(e.Surface.Canvas, e.Info, _angle);
    void OnLcdPaint(object? s, SKPaintSurfaceEventArgs e) => BiosRenderer.DrawLcd(e.Surface.Canvas, e.Info, LcdText());
    void OnCramPaint(object? s, SKPaintSurfaceEventArgs e)
        => BiosRenderer.DrawCram(e.Surface.Canvas, e.Info, _player.Cram, QuirkSettings.On(Q.Color64));

    void OnScreenTap(object? s, TappedEventArgs e) => _player.Overlay.Popup = 0;   // dismiss BIOS pop-up

    // ----------------------------------------------------------------- transport
    async void OnOpen(object? s, EventArgs e)
    {
        if (_busy) { _cts?.Cancel(); return; }
        try
        {
            var file = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Insert disc (pick a video)",
                FileTypes = FilePickerFileType.Videos
            });
            if (file == null) return;

            _player.Stop();
            _cts = new CancellationTokenSource();
            _busy = true; OpenButton.Text = "CANCEL"; PlayButton.IsEnabled = StopButton.IsEnabled = false;

            var progress = new Progress<(double Progress, string Message)>(x => { Bar.Progress = x.Progress; StatusLabel.Text = x.Message; });
            await using var stream = await file.OpenReadAsync();
            string path = await _decoder.ConvertAsync(stream, file.FileName, progress, _cts.Token);

            _player.Load(path);
            _player.Play();
        }
        catch (OperationCanceledException) { StatusLabel.Text = "READ CANCELLED"; }
        catch (Exception ex) { await DisplayAlertAsync("DISC READ ERROR", ex.Message, "OK"); }
        finally
        {
            _busy = false; OpenButton.Text = "OPEN"; PlayButton.IsEnabled = StopButton.IsEnabled = true;
        }
    }

    void OnPlayPause(object? s, EventArgs e) { if (_player.IsPlaying) _player.Pause(); else _player.Play(); }
    void OnStop(object? s, EventArgs e) => _player.Stop();

    protected override void OnDisappearing() { base.OnDisappearing(); _player.Pause(); }
}
