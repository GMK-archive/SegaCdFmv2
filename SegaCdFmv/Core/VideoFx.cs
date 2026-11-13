namespace SegaCdFmv.Core;

/// <summary>
/// Software video pipeline (one instance per video thread).
/// Stage A runs once per new frame:  compose -> Cinepak -> dither -> 512-color master palette
///                                   (or 64-color VDP limit) -> shadow/highlight -> palette tear.
/// Stage B runs on every vblank:     affine warp -> interlace comb -> composite (analog) output.
/// </summary>
public sealed class VideoFx
{
    const int W = Vdp.W, H = Vdp.H;
    readonly Vdp64 _vdp = new();
    readonly float[] _y = new float[W * H], _i = new float[W * H], _q = new float[W * H];
    readonly int[] _mr = new int[4], _mg = new int[4], _mb = new int[4], _dv = new int[4], _ml = new int[4];

    public ushort[] Cram => _vdp.Cram;

    static bool On(Q q) => QuirkSettings.On(q);
    static uint Hash(uint a, uint b, uint c)
    {
        uint h = a * 374761393u + b * 668265263u + c * 2246822519u;
        h = (h ^ (h >> 13)) * 1274126177u;
        return h ^ (h >> 16);
    }
    static int Luma(int r, int g, int b) => (r * 77 + g * 151 + b * 28) >> 8;
    static uint Px(int r, int g, int b) => Palette.Pack(r, g, b);

    // ------------------------------------------------------------------ stage A
    public void StageA(ushort[] master, FmvHeader hd, uint[] dst, bool stamp, int frame)
    {
        Compose(master, hd, stamp, dst);
        if (On(Q.Cinepak)) Cinepak(dst, frame);
        if (On(Q.Dither)) Dither(dst);
        if (On(Q.Color64)) _vdp.Apply(dst); else Palette.Snap(dst);   // the 512-color palette is always enforced
        if (On(Q.ShadowHighlight)) ShadowHighlight(dst);
        if (On(Q.PaletteTear)) Tear(dst, frame);
    }

    void Compose(ushort[] m, FmvHeader hd, bool stamp, uint[] dst)
    {
        var tab = Palette.Rgb15;
        if (!stamp) { for (int i = 0; i < dst.Length; i++) dst[i] = tab[m[i]]; return; }

        const int WW = 256, WH = 128, WX = (W - WW) / 2, WY = (H - WH) / 2;
        Array.Fill(dst, Palette.Rgb9[2 << 6]);                              // deep-blue overscan border
        for (int y = WY; y < WY + WH; y++) Array.Fill(dst, 0xFF000000u, y * W + WX, WW);
        int cw = hd.CW, ch = hd.CH;
        double s = Math.Min((double)WW / cw, (double)WH / ch);
        int dw = Math.Max(1, (int)(cw * s)), dh = Math.Max(1, (int)(ch * s));
        int ox = WX + (WW - dw) / 2, oy = WY + (WH - dh) / 2;
        for (int y = 0; y < dh; y++)
        {
            int sy = hd.CY + y * ch / dh;
            for (int x = 0; x < dw; x++) dst[(oy + y) * W + ox + x] = tab[m[sy * W + hd.CX + x * cw / dw]];
        }
    }

    // ------------------------------------------------------------ Cinepak model
    static int Qz(int v) => Math.Min(255, ((v + 16) >> 5) << 5);          // coarse codebook entry

    static void Fill(uint[] b, int x, int y, int w, int h, uint c)
    {
        for (int yy = 0; yy < h; yy++) Array.Fill(b, c, (y + yy) * W + x, w);
    }

    public void Cinepak(uint[] b, int f)
    {
        for (int my = 0; my < H / 8; my++)
            for (int mx = 0; mx < W / 8; mx++)
            {
                int x0 = mx * 8, y0 = my * 8;
                for (int q = 0; q < 4; q++)
                {
                    int bx = x0 + (q & 1) * 4, by = y0 + (q >> 1) * 4, sr = 0, sg = 0, sb = 0;
                    for (int yy = 0; yy < 4; yy++)
                        for (int xx = 0; xx < 4; xx++)
                        { uint p = b[(by + yy) * W + bx + xx]; sr += (int)(p & 255); sg += (int)((p >> 8) & 255); sb += (int)((p >> 16) & 255); }
                    _mr[q] = sr >> 4; _mg[q] = sg >> 4; _mb[q] = sb >> 4; _ml[q] = Luma(_mr[q], _mg[q], _mb[q]);
                    int dev = 0;
                    for (int yy = 0; yy < 4; yy++)
                        for (int xx = 0; xx < 4; xx++)
                        { uint p = b[(by + yy) * W + bx + xx]; dev += Math.Abs(Luma((int)(p & 255), (int)((p >> 8) & 255), (int)((p >> 16) & 255)) - _ml[q]); }
                    _dv[q] = dev;
                }

                int lmin = Math.Min(Math.Min(_ml[0], _ml[1]), Math.Min(_ml[2], _ml[3]));
                int lmax = Math.Max(Math.Max(_ml[0], _ml[1]), Math.Max(_ml[2], _ml[3]));
                bool flat = lmax - lmin < 14 && _dv[0] < 70 && _dv[1] < 70 && _dv[2] < 70 && _dv[3] < 70;
                if (flat)   // 8x8 macroblock: one codebook entry
                {
                    int r = (_mr[0] + _mr[1] + _mr[2] + _mr[3]) >> 2, g = (_mg[0] + _mg[1] + _mg[2] + _mg[3]) >> 2, bl = (_mb[0] + _mb[1] + _mb[2] + _mb[3]) >> 2;
                    Fill(b, x0, y0, 8, 8, Px(Qz(r), Qz(g), Qz(bl)));
                    continue;
                }
                for (int q = 0; q < 4; q++)
                {
                    int bx = x0 + (q & 1) * 4, by = y0 + (q >> 1) * 4;
                    if (_dv[q] < 160)   // V1: whole 4x4 is one vector
                    {
                        int r = _mr[q], g = _mg[q], bl = _mb[q];
                        uint hh = Hash((uint)mx, (uint)(my * 4 + q), (uint)f);
                        if ((hh & 127) < 2) { int t = r; r = g; g = bl; bl = t; }      // wrong codebook index
                        Fill(b, bx, by, 4, 4, Px(Qz(r), Qz(g), Qz(bl)));
                    }
                    else                // V4: four 2x2 vectors
                    {
                        for (int s = 0; s < 4; s++)
                        {
                            int sx = bx + (s & 1) * 2, sy = by + (s >> 1) * 2, r = 0, g = 0, bl = 0;
                            for (int yy = 0; yy < 2; yy++)
                                for (int xx = 0; xx < 2; xx++)
                                { uint p = b[(sy + yy) * W + sx + xx]; r += (int)(p & 255); g += (int)((p >> 8) & 255); bl += (int)((p >> 16) & 255); }
                            Fill(b, sx, sy, 2, 2, Px(Qz(r >> 2), Qz(g >> 2), Qz(bl >> 2)));
                        }
                    }
                }
            }
    }

    /// <summary>Estimated Cinepak stream cost of a frame in bytes (skip / V1 / V4 blocks vs. previous shown frame).</summary>
    public static int Cost(ushort[] cur, ushort[]? prev)
    {
        Span<int> l = stackalloc int[16];
        int bytes = 24, blocks = 0;
        for (int by = 0; by < H; by += 4)
            for (int bx = 0; bx < W; bx += 4)
            {
                int sum = 0, sad = 0, k = 0;
                for (int yy = 0; yy < 4; yy++)
                    for (int xx = 0; xx < 4; xx++, k++)
                    {
                        int i = (by + yy) * W + bx + xx; int v = cur[i];
                        int lum = (((v >> 10) & 31) * 77 + ((v >> 5) & 31) * 151 + (v & 31) * 28) >> 8;
                        l[k] = lum; sum += lum;
                        if (prev != null) { int pv = prev[i]; sad += Math.Abs(lum - ((((pv >> 10) & 31) * 77 + ((pv >> 5) & 31) * 151 + (pv & 31) * 28) >> 8)); }
                    }
                blocks++;
                if (prev != null && sad <= 20) continue;                       // skip block
                int mean = sum >> 4, dev = 0;
                for (int i = 0; i < 16; i++) dev += Math.Abs(l[i] - mean);
                bytes += dev <= 40 ? 2 : 6;                                    // V1 vs V4 (+codebook share)
            }
        return bytes + blocks / 8;
    }

    // ------------------------------------------------------------------- color
    static readonly int[] Bayer = BuildBayer();
    static int[] BuildBayer()
    {
        int[] m = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };
        var o = new int[16];
        for (int k = 0; k < 16; k++) o[k] = (int)(((m[k] + 0.5) / 16 - 0.5) * 64);   // about +-0.9 palette step
        return o;
    }

    public void Dither(uint[] b)
    {
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int o = Bayer[((y & 3) << 2) | (x & 3)], i = y * W + x; uint p = b[i];
                b[i] = Px(Math.Clamp((int)(p & 255) + o, 0, 255), Math.Clamp((int)((p >> 8) & 255) + o, 0, 255), Math.Clamp((int)((p >> 16) & 255) + o, 0, 255));
            }
    }

    public void ShadowHighlight(uint[] b)
    {
        for (int i = 0; i < b.Length; i++)
        {
            uint p = b[i]; int r = (int)(p & 255), g = (int)((p >> 8) & 255), bl = (int)((p >> 16) & 255), l = Luma(r, g, bl);
            if (l < 64)      { if (l < 36) { r = g = bl = 0; } else { r >>= 1; g >>= 1; bl >>= 1; } }          // shadow: halve, crush
            else if (l > 190) { if (l > 225) { r = g = bl = 255; } else { r = (r >> 1) + 127; g = (g >> 1) + 127; bl = (bl >> 1) + 127; } }   // highlight: blow out
            else continue;
            b[i] = Palette.Rgb9[Palette.Idx9(Px(r, g, bl))];
        }
    }

    public void Tear(uint[] b, int f)
    {
        if (Hash((uint)f, 1, 9) % 10 >= 7) return;
        int y0 = H * 2 / 3 + (int)(Hash((uint)f, 7, 3) % 7) - 3;
        for (int i = y0 * W; i < b.Length; i++)
        {
            int c = Palette.Idx9(b[i]);                                        // stale CRAM: channels rotated
            b[i] = Palette.Rgb9[((c >> 3) & 7) | ((c >> 6) << 3) | ((c & 7) << 6)];
        }
    }

    // ------------------------------------------------------------------ stage B
    /// <summary>ASIC scaling/rotation, nearest neighbour in 16.16 fixed point with per-scanline wobble.</summary>
    public void Affine(uint[] src, uint[] dst, double t)
    {
        double zoom = 1.0 + 0.014 * Math.Sin(t * 1.7), rot = 0.012 * Math.Sin(t * 1.1 + 0.6);
        int c = (int)(Math.Cos(rot) / zoom * 65536), s = (int)(Math.Sin(rot) / zoom * 65536);
        const int cx = W / 2, cy = H / 2;
        for (int y = 0; y < H; y++)
        {
            int dy = y - cy, wob = (int)(Math.Sin(y * 0.23 + t * 8.0) * 1.4 * 65536);
            for (int x = 0; x < W; x++)
            {
                int dx = x - cx;
                int sx = ((c * dx - s * dy + wob) >> 16) + cx, sy = ((s * dx + c * dy) >> 16) + cy;
                dst[y * W + x] = ((uint)sx < W && (uint)sy < H) ? src[sy * W + sx] : 0xFF000000u;
            }
        }
    }

    static uint Dim(uint p) => 0xFF000000u | (((((p >> 16) & 255) * 200) >> 8) << 16) | (((((p >> 8) & 255) * 200) >> 8) << 8) | (((p & 255) * 200) >> 8);

    /// <summary>Interlace: this vblank draws only one field fresh; the other field is the previous frame (comb) or dimmed.</summary>
    public void Comb(uint[] cur, uint[] prev, uint[] dst, int field, bool fresh)
    {
        for (int y = 0; y < H; y++)
        {
            bool active = (y & 1) == field;
            var s = active ? cur : (fresh ? prev : cur);
            for (int x = 0; x < W; x++) { uint p = s[y * W + x]; dst[y * W + x] = active ? p : Dim(p); }
        }
    }

    static readonly float[] CosT = { 1, 0, -1, 0 }, SinT = { 0, 1, 0, -1 };
    static int Cl(float v) => v < 0 ? 0 : v > 255 ? 255 : (int)v;

    /// <summary>NTSC composite: chroma bandwidth smear + 4-pixel dot-crawl crosstalk on luma edges.</summary>
    public void Composite(uint[] src, uint[] dst, long field)
    {
        for (int k = 0; k < src.Length; k++)
        {
            uint p = src[k]; float r = p & 255, g = (p >> 8) & 255, b = (p >> 16) & 255;
            _y[k] = 0.299f * r + 0.587f * g + 0.114f * b;
            _i[k] = 0.596f * r - 0.274f * g - 0.322f * b;
            _q[k] = 0.211f * r - 0.523f * g + 0.312f * b;
        }
        int f = (int)(field & 1);
        for (int y = 0; y < H; y++)
        {
            int row = y * W;
            for (int x = 0; x < W; x++)
            {
                int k = row + x, k1 = x > 0 ? k - 1 : k, k2 = x > 1 ? k - 2 : k1;
                float yy = _y[k];
                float ci = (_i[k] + 2f * _i[k1] + _i[k2]) * 0.25f, cq = (_q[k] + 2f * _q[k1] + _q[k2]) * 0.25f;
                float amp = Math.Clamp((x > 0 ? yy - _y[k1] : 0f) * 0.30f, -50f, 50f);
                int ph = (x + 2 * ((y + f) & 1)) & 3;
                ci += amp * CosT[ph]; cq += amp * SinT[ph];
                dst[k] = Px(Cl(yy + 0.956f * ci + 0.621f * cq), Cl(yy - 0.272f * ci - 0.647f * cq), Cl(yy - 1.106f * ci + 1.703f * cq));
            }
        }
    }
}
