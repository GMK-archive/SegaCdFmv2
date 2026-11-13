namespace SegaCdFmv.Core;

public static class Vdp { public const int W = 320, H = 224; }

/// <summary>
/// The Genesis/Mega Drive 9-bit master palette (8 levels per channel = 512 colors).
/// Levels were measured from the supplied RGB_9bits_palette_color_test_chart.png:
/// 0, 36, 72, 109, 145, 182, 218, 255.
/// Pixel format everywhere in the engine: uint = 0xFF000000 | B&lt;&lt;16 | G&lt;&lt;8 | R (Rgba8888 little-endian).
/// </summary>
public static class Palette
{
    public static readonly byte[] Lv = { 0, 36, 72, 109, 145, 182, 218, 255 };
    public static readonly uint[] Rgb9 = new uint[512];       // idx = r3 | g3<<3 | b3<<6
    public static readonly uint[] Rgb15 = new uint[32768];    // idx = r5<<10 | g5<<5 | b5
    static readonly byte[] To3 = new byte[256];

    static Palette()
    {
        for (int v = 0; v < 256; v++)
        {
            int best = 0, bd = 999;
            for (int i = 0; i < 8; i++) { int d = Math.Abs(v - Lv[i]); if (d < bd) { bd = d; best = i; } }
            To3[v] = (byte)best;
        }
        for (int i = 0; i < 512; i++) Rgb9[i] = Pack(Lv[i & 7], Lv[(i >> 3) & 7], Lv[i >> 6]);
        for (int i = 0; i < 32768; i++)
            Rgb15[i] = Pack(E5((i >> 10) & 31), E5((i >> 5) & 31), E5(i & 31));
    }

    static int E5(int v) => (v << 3) | (v >> 2);
    public static uint Pack(int r, int g, int b) => 0xFF000000u | (uint)(b << 16) | (uint)(g << 8) | (uint)r;
    public static int Idx9(uint p) => To3[(int)(p & 255)] | (To3[(int)((p >> 8) & 255)] << 3) | (To3[(int)((p >> 16) & 255)] << 6);
    public static int Luma9(int idx) => (idx & 7) * 3 + ((idx >> 3) & 7) * 6 + (idx >> 6);

    /// <summary>Force every pixel onto the 512-color master palette.</summary>
    public static void Snap(uint[] px) { for (int i = 0; i < px.Length; i++) px[i] = Rgb9[Idx9(px[i])]; }
}

/// <summary>
/// Genesis VDP limit: 4 palette lines x 16 colors (64 on screen), each 8x8 tile uses exactly one line.
/// Lines are rebuilt every frame: tiles are clustered into 4 groups (k-means on tile mean color),
/// each group gets a 16-color median-cut palette from the 512-color master set.
/// </summary>
public sealed class Vdp64
{
    const int W = Vdp.W, H = Vdp.H, T = 8, TW = W / T, TH = H / T, NT = TW * TH;
    public readonly ushort[] Cram = new ushort[64];   // idx9 values, for the CRAM viewer

    readonly ushort[] _idx = new ushort[W * H];
    readonly int[] _mr = new int[NT], _mg = new int[NT], _mb = new int[NT];
    readonly int[] _key = new int[NT], _order = new int[NT], _sorted = new int[NT], _cl = new int[NT];
    readonly int[] _hist = new int[4 * 512];
    readonly int[][] _lut = { new int[512], new int[512], new int[512], new int[512] };
    readonly int[][] _err = { new int[512], new int[512], new int[512], new int[512] };
    readonly int[][] _pal = new int[4][];

    static int D(int a, int b)
    {
        int dr = (a & 7) - (b & 7), dg = ((a >> 3) & 7) - ((b >> 3) & 7), db = (a >> 6) - (b >> 6);
        return 3 * dr * dr + 4 * dg * dg + 2 * db * db;
    }
    static int Sq(int v) => v * v;

    public void Apply(uint[] px)
    {
        for (int i = 0; i < px.Length; i++) _idx[i] = (ushort)Palette.Idx9(px[i]);

        for (int t = 0; t < NT; t++)
        {
            int tx = (t % TW) * T, ty = (t / TW) * T, r = 0, g = 0, b = 0;
            for (int y = 0; y < T; y++)
            {
                int o = (ty + y) * W + tx;
                for (int x = 0; x < T; x++) { int c = _idx[o + x]; r += c & 7; g += (c >> 3) & 7; b += c >> 6; }
            }
            _mr[t] = r; _mg[t] = g; _mb[t] = b; _key[t] = r * 3 + g * 6 + b; _order[t] = t;
        }

        // deterministic init: centers at luma quartiles (keeps palettes stable frame to frame)
        Array.Copy(_key, _sorted, NT);
        Array.Sort(_sorted, _order);
        Span<int> cr = stackalloc int[4], cg = stackalloc int[4], cb = stackalloc int[4];
        Span<int> sr = stackalloc int[4], sg = stackalloc int[4], sb = stackalloc int[4], sn = stackalloc int[4];
        for (int k = 0; k < 4; k++) { int t = _order[NT * (2 * k + 1) / 8]; cr[k] = _mr[t]; cg[k] = _mg[t]; cb[k] = _mb[t]; }

        for (int it = 0; it < 4; it++)
        {
            sr.Clear(); sg.Clear(); sb.Clear(); sn.Clear();
            for (int t = 0; t < NT; t++)
            {
                int best = 0, bd = int.MaxValue;
                for (int k = 0; k < 4; k++)
                {
                    int d = 3 * Sq(_mr[t] - cr[k]) + 4 * Sq(_mg[t] - cg[k]) + 2 * Sq(_mb[t] - cb[k]);
                    if (d < bd) { bd = d; best = k; }
                }
                _cl[t] = best; sr[best] += _mr[t]; sg[best] += _mg[t]; sb[best] += _mb[t]; sn[best]++;
            }
            for (int k = 0; k < 4; k++) if (sn[k] > 0) { cr[k] = sr[k] / sn[k]; cg[k] = sg[k] / sn[k]; cb[k] = sb[k] / sn[k]; }
        }

        Array.Clear(_hist);
        for (int t = 0; t < NT; t++)
        {
            int hb = _cl[t] * 512, tx = (t % TW) * T, ty = (t / TW) * T;
            for (int y = 0; y < T; y++) { int o = (ty + y) * W + tx; for (int x = 0; x < T; x++) _hist[hb + _idx[o + x]]++; }
        }

        for (int c = 0; c < 4; c++)
        {
            var pal = MedianCut(_hist, c * 512, 16);
            _pal[c] = pal;
            for (int col = 0; col < 512; col++)
            {
                int bi = pal[0], bd = int.MaxValue;
                foreach (int p in pal) { int d = D(col, p); if (d < bd) { bd = d; bi = p; } }
                _lut[c][col] = bi; _err[c][col] = bd;
            }
        }

        for (int t = 0; t < NT; t++)
        {
            int tx = (t % TW) * T, ty = (t / TW) * T, bc = 0; long be = long.MaxValue;
            for (int c = 0; c < 4; c++)
            {
                long e = 0; var er = _err[c];
                for (int y = 0; y < T; y++) { int o = (ty + y) * W + tx; for (int x = 0; x < T; x++) e += er[_idx[o + x]]; }
                if (e < be) { be = e; bc = c; }
            }
            var lut = _lut[bc];
            for (int y = 0; y < T; y++)
            {
                int o = (ty + y) * W + tx;
                for (int x = 0; x < T; x++) px[o + x] = Palette.Rgb9[lut[_idx[o + x]]];
            }
        }

        for (int c = 0; c < 4; c++)
        {
            var pal = (int[])_pal[c].Clone();
            Array.Sort(pal, (a, b) => Palette.Luma9(a).CompareTo(Palette.Luma9(b)));
            for (int k = 0; k < 16; k++) Cram[c * 16 + k] = (ushort)pal[Math.Min(k, pal.Length - 1)];
        }
    }

    static int Ch(int c, int ch) => ch == 0 ? c & 7 : ch == 1 ? (c >> 3) & 7 : c >> 6;

    static int[] MedianCut(int[] hist, int off, int n)
    {
        var all = new List<int>();
        for (int i = 0; i < 512; i++) if (hist[off + i] > 0) all.Add(i);
        if (all.Count == 0) return new[] { 0 };
        var boxes = new List<List<int>> { all };
        while (boxes.Count < n)
        {
            int bi = -1; long best = 0;
            for (int i = 0; i < boxes.Count; i++)
            {
                if (boxes[i].Count < 2) continue;
                long s = 0; foreach (int c in boxes[i]) s += hist[off + c];
                if (s > best) { best = s; bi = i; }
            }
            if (bi < 0) break;
            var b = boxes[bi];
            int rmin = 7, rmax = 0, gmin = 7, gmax = 0, bmin = 7, bmax = 0;
            foreach (int c in b)
            {
                int r = c & 7, g = (c >> 3) & 7, bl = c >> 6;
                rmin = Math.Min(rmin, r); rmax = Math.Max(rmax, r);
                gmin = Math.Min(gmin, g); gmax = Math.Max(gmax, g);
                bmin = Math.Min(bmin, bl); bmax = Math.Max(bmax, bl);
            }
            int ch = (gmax - gmin >= rmax - rmin && gmax - gmin >= bmax - bmin) ? 1 : (rmax - rmin >= bmax - bmin ? 0 : 2);
            b.Sort((x, y) => Ch(x, ch).CompareTo(Ch(y, ch)));
            long half = best / 2, acc = 0; int cut = 0;
            for (; cut < b.Count - 1; cut++) { acc += hist[off + b[cut]]; if (acc >= half) break; }
            cut = Math.Min(cut, b.Count - 2);
            var hi = b.GetRange(cut + 1, b.Count - cut - 1);
            b.RemoveRange(cut + 1, b.Count - cut - 1);
            boxes.Add(hi);
        }
        var res = new int[boxes.Count];
        for (int i = 0; i < boxes.Count; i++)
        {
            long sr = 0, sg = 0, sb = 0, sn = 0;
            foreach (int c in boxes[i]) { long w = hist[off + c]; sr += (c & 7) * w; sg += ((c >> 3) & 7) * w; sb += (c >> 6) * w; sn += w; }
            res[i] = (int)((sr + sn / 2) / sn) | ((int)((sg + sn / 2) / sn) << 3) | ((int)((sb + sn / 2) / sn) << 6);
        }
        return res;
    }
}
