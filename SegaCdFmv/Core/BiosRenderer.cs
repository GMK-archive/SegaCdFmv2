using SkiaSharp;

namespace SegaCdFmv.Core;

/// <summary>All custom drawing: CRT screen + BIOS overlays, spinning disc, 7-segment LCD, CRAM viewer.</summary>
public static class BiosRenderer
{
    static readonly SKColor Panel = new(0x11, 0x11, 0x19), Neon = new(0x00, 0xFF, 0xCC);
    static readonly SKTypeface Mono = SKTypeface.FromFamilyName("monospace", SKFontStyle.Bold) ?? SKTypeface.Default;

    static void Text(SKCanvas c, string s, float x, float y, float size, SKColor col, SKTextAlign al = SKTextAlign.Left)
    {
        using var f = new SKFont(Mono, size) { Edging = SKFontEdging.Alias, Subpixel = false };
        using var p = new SKPaint { Color = col, IsAntialias = false };
        c.DrawText(s, x, y, al, f, p);
    }

    static void Box(SKCanvas c, SKRect r, SKColor fill, SKColor? border = null, float bw = 0)
    {
        using var p = new SKPaint { Color = fill, IsAntialias = false, Style = SKPaintStyle.Fill };
        c.DrawRect(r, p);
        if (border is SKColor bc && bw > 0)
        {
            using var s = new SKPaint { Color = bc, IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = bw };
            c.DrawRect(r, s);
        }
    }

    // ---------------------------------------------------------------- screen
    public static void DrawScreen(SKCanvas c, SKImageInfo info, FmvPlayer p, double t)
    {
        c.Clear(Panel);
        float m = 8f, aw = info.Width - 2 * m, ah = info.Height - 2 * m;
        float vw = aw, vh = aw * 0.75f;
        if (vh > ah) { vh = ah; vw = ah / 0.75f; }
        var d = SKRect.Create((info.Width - vw) / 2f, (info.Height - vh) / 2f, vw, vh);

        using (var bz = new SKPaint { Color = new SKColor(0x1B, 0x1B, 0x2A), IsAntialias = true })
            c.DrawRoundRect(SKRect.Inflate(d, 6, 6), 9, 9, bz);
        using (var ol = new SKPaint { Color = Neon.WithAlpha(90), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 })
            c.DrawRoundRect(SKRect.Inflate(d, 6, 6), 9, 9, ol);

        c.Save();
        c.ClipRect(d);
        if (!p.DrawFrame(c, d)) DrawIdle(c, d, t);
        c.Restore();

        float u = d.Width / 320f;

        if (p.Overlay.Checking)
        {
            Box(c, d, new SKColor(0, 0, 0, 110));
            var r = SKRect.Create(d.MidX - 100 * u, d.MidY - 18 * u, 200 * u, 36 * u);
            Box(c, r, new SKColor(0x00, 0x00, 0x90), SKColors.White, Math.Max(2f, 2 * u));
            if (((int)(t * 3)) % 2 == 0) Text(c, "CHECKING DISC...", d.MidX, r.MidY + 5 * u, 14 * u, SKColors.White, SKTextAlign.Center);
        }

        int pop = p.Overlay.Popup;
        if (pop != 0)
        {
            Box(c, d, new SKColor(0, 0, 0, 150));
            var r = SKRect.Create(d.MidX - 132 * u, d.MidY - 62 * u, 264 * u, 124 * u);
            Box(c, r, new SKColor(0x10, 0x20, 0xA0), SKColors.White, 3 * u);
            Box(c, SKRect.Inflate(r, -6 * u, -6 * u), SKColors.Transparent, new SKColor(0x7F, 0xA0, 0xFF), 1.5f * u);
            Box(c, SKRect.Create(r.Left + 8 * u, r.Top + 8 * u, r.Width - 16 * u, 17 * u), SKColors.White);
            Text(c, "MEGA-CD  BACKUP RAM", r.MidX, r.Top + 21 * u, 11 * u, new SKColor(0x10, 0x20, 0xA0), SKTextAlign.Center);
            string[] lines = pop == 1
                ? new[] { "BACKUP RAM IS FULL.", "64 KBIT: 0 BLOCKS FREE.", "DATA CANNOT BE SAVED." }
                : new[] { "BACKUP RAM BATTERY", "IS LOW OR DEAD.", "SAVED DATA MAY BE LOST." };
            for (int i = 0; i < lines.Length; i++)
                Text(c, lines[i], r.MidX, r.Top + (46 + i * 17) * u, 12 * u, SKColors.White, SKTextAlign.Center);
            if (((int)(t * 2)) % 2 == 0) Text(c, "TAP TO CONTINUE", r.MidX, r.Bottom - 12 * u, 9 * u, new SKColor(0xFF, 0xE0, 0x60), SKTextAlign.Center);
        }

        if (!string.IsNullOrEmpty(p.Hud)) Text(c, p.Hud, d.Left + 4 * u, d.Bottom - 4 * u, 8 * u, Neon.WithAlpha(210));
    }

    /// <summary>No disc: shows all 512 master-palette colors (the Genesis 9-bit test chart).</summary>
    static void DrawIdle(SKCanvas c, SKRect d, double t)
    {
        using (var sh = SKShader.CreateLinearGradient(new SKPoint(d.Left, d.Top), new SKPoint(d.Left, d.Bottom),
                   new[] { new SKColor(0, 0, 0x40), new SKColor(0, 0, 0x08) }, null, SKShaderTileMode.Clamp))
        using (var bg = new SKPaint { Shader = sh })
            c.DrawRect(d, bg);

        float cell = d.Width * 0.74f / 32f, gx = d.MidX - 16 * cell, gy = d.Top + d.Height * 0.27f;
        using var px = new SKPaint { IsAntialias = false };
        for (int i = 0; i < 512; i++)
        {
            px.Color = new SKColor(Palette.Lv[i & 7], Palette.Lv[(i >> 3) & 7], Palette.Lv[i >> 6]);
            c.DrawRect(gx + (i % 32) * cell, gy + (i / 32) * cell, cell, cell, px);
        }
        Text(c, "MEGA-CD", d.MidX, d.Top + d.Height * 0.19f, d.Height * 0.11f, SKColors.White, SKTextAlign.Center);
        if (((int)(t * 1.5)) % 2 == 0)
            Text(c, "INSERT DISC", d.MidX, d.Bottom - d.Height * 0.10f, d.Height * 0.07f, Neon, SKTextAlign.Center);
    }

    // ------------------------------------------------------------------ disc
    public static void DrawDisc(SKCanvas c, SKImageInfo info, float angle)
    {
        c.Clear(SKColors.Transparent);
        float cx = info.Width / 2f, cy = info.Height / 2f, r = Math.Min(cx, cy) - 3;
        var cols = new[]
        {
            new SKColor(0xC0, 0xC4, 0xCC), new SKColor(0xB8, 0xD8, 0xFF), SKColors.White, new SKColor(0xFF, 0xC8, 0xF0),
            new SKColor(0xC0, 0xC4, 0xCC), new SKColor(0xC8, 0xFF, 0xE0), SKColors.White, new SKColor(0x90, 0x98, 0xA8),
            new SKColor(0xC0, 0xC4, 0xCC)
        };
        c.Save();
        c.Translate(cx, cy); c.RotateDegrees(angle);
        using (var sh = SKShader.CreateSweepGradient(SKPoint.Empty, cols, null))
        using (var p = new SKPaint { Shader = sh, IsAntialias = true })
            c.DrawCircle(0, 0, r, p);
        c.Restore();

        using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = new SKColor(0, 0, 0, 90) };
        c.DrawCircle(cx, cy, r * 0.62f, ring);
        using var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = Neon.WithAlpha(120) };
        c.DrawCircle(cx, cy, r, rim);
        using var hub = new SKPaint { IsAntialias = true, Color = Panel };
        c.DrawCircle(cx, cy, r * 0.20f, hub);
        using var hubRing = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = Neon };
        c.DrawCircle(cx, cy, r * 0.20f, hubRing);
    }

    // ------------------------------------------------------------------- LCD
    static readonly byte[] Seg = { 63, 6, 91, 79, 102, 109, 125, 7, 127, 111 };   // a=1 b=2 c=4 d=8 e=16 f=32 g=64

    static void Digit(SKCanvas c, int d, float x, float y, float w, float h, SKPaint on, SKPaint off)
    {
        float t = w * 0.22f, hh = h / 2f; byte m = Seg[d];
        void S(int bit, float rx, float ry, float rw, float rh) => c.DrawRect(rx, ry, rw, rh, (m & bit) != 0 ? on : off);
        S(1, x + t * 0.6f, y, w - t * 1.2f, t);
        S(2, x + w - t, y + t * 0.6f, t, hh - t * 0.9f);
        S(4, x + w - t, y + hh + t * 0.3f, t, hh - t * 0.9f);
        S(8, x + t * 0.6f, y + h - t, w - t * 1.2f, t);
        S(16, x, y + hh + t * 0.3f, t, hh - t * 0.9f);
        S(32, x, y + t * 0.6f, t, hh - t * 0.9f);
        S(64, x + t * 0.6f, y + hh - t / 2, w - t * 1.2f, t);
    }

    /// <summary>text: digits, ':' and ' ' only, e.g. "01 00:12:07" (track MM:SS:FF, 75 frames/s like a real CD clock).</summary>
    public static void DrawLcd(SKCanvas c, SKImageInfo info, string text)
    {
        c.Clear(new SKColor(0x04, 0x0C, 0x0B));
        float h = info.Height * 0.70f, w = h * 0.52f, gap = w * 0.30f, total = 0;
        foreach (char ch in text) total += ch == ':' ? w * 0.45f + gap : ch == ' ' ? w * 0.6f : w + gap;
        float y = (info.Height - h) / 2f, x = Math.Max(4, (info.Width - total) / 2f) + 0.12f * (y + h / 2);

        using var on = new SKPaint { Color = Neon, IsAntialias = true };
        using var off = new SKPaint { Color = Neon.WithAlpha(24), IsAntialias = true };
        c.Save();
        c.Skew(-0.12f, 0);
        foreach (char ch in text)
        {
            if (ch >= '0' && ch <= '9') { Digit(c, ch - '0', x, y, w, h, on, off); x += w + gap; }
            else if (ch == ':')
            {
                float t = w * 0.22f;
                c.DrawRect(x, y + h * 0.28f, t, t, on); c.DrawRect(x, y + h * 0.64f, t, t, on);
                x += w * 0.45f + gap;
            }
            else x += w * 0.6f;
        }
        c.Restore();
    }

    // ------------------------------------------------------------------ CRAM
    public static void DrawCram(SKCanvas c, SKImageInfo info, ushort[] cram, bool active)
    {
        c.Clear(new SKColor(0x04, 0x06, 0x0A));
        float cw = info.Width / 16f, ch = info.Height / 4f;
        using var p = new SKPaint { IsAntialias = false };
        for (int i = 0; i < 64; i++)
        {
            int v = cram[i];
            p.Color = active
                ? new SKColor(Palette.Lv[v & 7], Palette.Lv[(v >> 3) & 7], Palette.Lv[v >> 6])
                : new SKColor(0x20, 0x26, 0x30);
            c.DrawRect((i % 16) * cw + 0.5f, (i / 16) * ch + 0.5f, cw - 1f, ch - 1f, p);
        }
    }
}
