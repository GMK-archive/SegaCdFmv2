using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace SegaCdFmv.Core;

public enum Q
{
    // Video
    PostageStamp, Cinepak, AffineShimmer, CompositeRainbow, InterlaceComb,
    // Color
    Color64, Dither, ShadowHighlight, PaletteTear,
    // Framerate
    FpsLock, Starvation, WordRamStall, TvStandard,
    // Audio
    SampleCrunch, PcmGrit, GroundHum, LipSyncDrift, Capacitor,
    // Hardware
    LaserSkip, BackupRam
}

/// <summary>Lock-free live settings read by the video/audio threads.</summary>
public static class QuirkSettings
{
    static readonly int[] V = new int[Enum.GetValues<Q>().Length];
    static int _version;
    public static readonly int[] FpsOptions = { 10, 12, 15, 20 };

    static QuirkSettings() { V[(int)Q.FpsLock] = 2; }   // 15 FPS

    public static int Version => Volatile.Read(ref _version);
    public static int Get(Q q) => Volatile.Read(ref V[(int)q]);
    public static bool On(Q q) => Get(q) != 0;
    public static void Set(Q q, int v) { Volatile.Write(ref V[(int)q], v); Interlocked.Increment(ref _version); }
}

public abstract class ObservableBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); return true;
    }
    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class QuirkItem : ObservableBase
{
    bool _on; int _sel;
    public Q Id { get; }
    public string Title { get; }
    public string Blurb { get; }
    public string[]? Options { get; }
    public bool IsPicker => Options != null;
    public bool IsSwitch => Options == null;

    public QuirkItem(Q id, string title, string blurb, string[]? options = null, int defaultIndex = 0)
    {
        Id = id; Title = title; Blurb = blurb; Options = options; _sel = defaultIndex;
        if (options != null) QuirkSettings.Set(id, defaultIndex);
    }

    public bool IsOn
    {
        get => _on;
        set { if (SetField(ref _on, value)) QuirkSettings.Set(Id, value ? 1 : 0); }
    }

    public int SelectedIndex
    {
        get => _sel;
        set { if (value >= 0 && SetField(ref _sel, value)) QuirkSettings.Set(Id, value); }
    }
}

public sealed class QuirkGroup : ObservableBase
{
    bool _open;
    public string Title { get; }
    public Color Accent { get; }
    public Brush AccentBrush { get; }
    public QuirkItem[] Items { get; }
    public ICommand ToggleCommand { get; }
    public string Arrow => _open ? "▼" : "▶";

    public QuirkGroup(string title, Color accent, bool open, params QuirkItem[] items)
    {
        Title = title; Accent = accent; AccentBrush = new SolidColorBrush(accent);
        _open = open; Items = items;
        ToggleCommand = new Command(() => IsExpanded = !IsExpanded);
    }

    public bool IsExpanded
    {
        get => _open;
        set { if (SetField(ref _open, value)) Raise(nameof(Arrow)); }
    }
}

public static class QuirkCatalog
{
    public static List<QuirkGroup> Build() => new()
    {
        new QuirkGroup("VIDEO QUIRKS", Color.FromArgb("#00FFCC"), true,
            new QuirkItem(Q.PostageStamp, "Postage-stamp window", "ON: 256x128 boxed viewport. OFF: full 320x224"),
            new QuirkItem(Q.Cinepak, "Cinepak / TruVideo macroblocking", "4x4 + 8x8 blocks, codebook VQ mistakes"),
            new QuirkItem(Q.AffineShimmer, "ASIC affine scale & rotate shimmer", "Nearest-neighbour warp, scanline crawl"),
            new QuirkItem(Q.CompositeRainbow, "Composite rainbowing & dot crawl", "Chroma smear + luma/chroma crosstalk"),
            new QuirkItem(Q.InterlaceComb, "Interlace field comb / line doubling", "Alternating fields, comb on motion")),

        new QuirkGroup("COLOR QUIRKS", Color.FromArgb("#2E7BFF"), false,
            new QuirkItem(Q.Color64, "Genesis VDP 64-color limit (4x16)", "Per-tile palette line picked from the 512-color set"),
            new QuirkItem(Q.Dither, "Aggressive Bayer / checkerboard dither", "Fakes the missing gradients"),
            new QuirkItem(Q.ShadowHighlight, "Shadow / highlight crushing", "Darks to black, brights blown out"),
            new QuirkItem(Q.PaletteTear, "Palette DMA update tear", "Wrong CRAM on the bottom third")),

        new QuirkGroup("FRAMERATE QUIRKS", Color.FromArgb("#FFB000"), false,
            new QuirkItem(Q.FpsLock, "Authentic FPS lock", "Real Sega CD FMV rates",
                new[] { "10 FPS", "12 FPS", "15 FPS", "20 FPS" }, 2),
            new QuirkItem(Q.Starvation, "1x CD-ROM starvation (150 KB/s)", "Drops frames when detail spikes"),
            new QuirkItem(Q.WordRamStall, "Word RAM flip-flop stall", "Erratic pacing, micro-stutters"),
            new QuirkItem(Q.TvStandard, "TV standard", "Vblank grid used for frame pacing",
                new[] { "NTSC 60 Hz", "PAL 50 Hz" })),

        new QuirkGroup("AUDIO QUIRKS", Color.FromArgb("#FF3EA5"), false,
            new QuirkItem(Q.SampleCrunch, "Low sample-rate crunch", "Zero-order hold, no anti-alias filter",
                new[] { "Off", "11.025 kHz", "16 kHz" }),
            new QuirkItem(Q.PcmGrit, "8-bit PCM / ADPCM grit", "Sign-magnitude 8-bit + ADPCM step noise"),
            new QuirkItem(Q.GroundHum, "AC adapter ground-loop hum", "50/60 Hz follows TV standard"),
            new QuirkItem(Q.LipSyncDrift, "Red Book vs PCM lip-sync drift", "Audio slides 50-150 ms late"),
            new QuirkItem(Q.Capacitor, "Failing Model 1 capacitor", "Crackle, bass roll-off, dropouts")),

        new QuirkGroup("HARDWARE FAILURES", Color.FromArgb("#FF4545"), false,
            new QuirkItem(Q.LaserSkip, "KSS-240A laser tracking skip", "Freeze 0.5-1.5 s + gritty 100 ms loop"),
            new QuirkItem(Q.BackupRam, "Backup RAM full / dead battery", "BIOS pop-up. Tap the screen to dismiss")),
    };
}
