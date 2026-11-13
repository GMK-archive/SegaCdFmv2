namespace SegaCdFmv.Core;

/// <summary>
/// Ricoh RF5C164 PCM characteristics applied to a mono stream at the chip's native 32552 Hz:
/// zero-order-hold rate crunch, 8-bit sign-magnitude quantisation (0xFF is the loop marker, so +127 is
/// unreachable), IMA-style ADPCM step noise, failing-capacitor behaviour, mains hum and the
/// laser-skip 100 ms buffer loop. One instance per audio thread.
/// </summary>
public sealed class Rf5c164Dsp
{
    public const int ChipRate = 32552;   // 12.5 MHz / 384

    static readonly short[] Step =
    {
        7,8,9,10,11,12,13,14,16,17,19,21,23,25,28,31,34,37,41,45,50,55,60,66,73,80,88,97,107,118,130,143,157,173,190,209,
        230,253,279,307,337,371,408,449,494,544,598,658,724,796,876,963,1060,1166,1282,1411,1552,1707,1878,2066,2272,2499,
        2749,3024,3327,3660,4026,4428,4871,5358,5894,6484,7132,7845,8630,9493,10442,11487,12635,13899,15289,16818,18500,
        20350,22385,24623,27086,29794,32767
    };
    static readonly int[] IdxAdj = { -1, -1, -1, -1, 2, 4, 6, 8 };

    readonly Random _rng = new(0xC0DE);
    double _hold, _humPhase; float _held;
    int _pred, _idx;
    float _hpIn, _hpOut, _crackle, _gain = 1f;
    int _dropLeft, _dropIn = ChipRate * 3;
    readonly short[] _ring = new short[ChipRate / 10];   // last ~100 ms of output
    int _ringPos;
    short[] _snap = Array.Empty<short>(); int _snapPos;

    static bool On(Q q) => QuirkSettings.On(q);

    public void Process(float[] src, short[] dst, int n, bool pal)
    {
        int crunch = QuirkSettings.Get(Q.SampleCrunch);
        bool grit = On(Q.PcmGrit), cap = On(Q.Capacitor), hum = On(Q.GroundHum);
        double holdStep = (crunch == 1 ? 11025.0 : 16000.0) / ChipRate;
        double humInc = 2 * Math.PI * (pal ? 50 : 60) / ChipRate;

        for (int i = 0; i < n; i++)
        {
            float x = src[i];
            if (crunch != 0) { _hold += holdStep; if (_hold >= 1) { _hold -= 1; _held = x; } x = _held; }   // no anti-alias filter
            if (grit) x = Grit(x);
            if (cap) x = Cap(x);
            if (hum)
            {
                x += 0.014f * (float)(Math.Sin(_humPhase) + 0.35 * Math.Sin(2 * _humPhase) + 0.15 * Math.Sin(3 * _humPhase));
                _humPhase += humInc; if (_humPhase > Math.PI * 2) _humPhase -= Math.PI * 2;
            }
            short s = (short)(Math.Clamp(x, -1f, 1f) * 32767f);
            dst[i] = s; _ring[_ringPos] = s; _ringPos = (_ringPos + 1) % _ring.Length;
        }
    }

    float Grit(float x)
    {
        int m = Math.Min(126, (int)MathF.Round(MathF.Abs(x) * 126f));
        float y = MathF.Sign(x) * m / 126f;
        int s = (int)(y * 32767), diff = s - _pred, step = Step[_idx], code = 0;
        if (diff < 0) { code = 8; diff = -diff; }
        if (diff >= step) { code |= 4; diff -= step; }
        if (diff >= (step >> 1)) { code |= 2; diff -= step >> 1; }
        if (diff >= (step >> 2)) code |= 1;
        int dq = step >> 3;
        if ((code & 4) != 0) dq += step;
        if ((code & 2) != 0) dq += step >> 1;
        if ((code & 1) != 0) dq += step >> 2;
        _pred = Math.Clamp(_pred + ((code & 8) != 0 ? -dq : dq), -32767, 32767);
        _idx = Math.Clamp(_idx + IdxAdj[code & 7], 0, 88);
        float hiss = (float)(_rng.NextDouble() - _rng.NextDouble()) * 0.006f;   // TPDF hiss
        return _pred / 32768f + hiss;
    }

    float Cap(float x)
    {
        float hp = 0.955f * (_hpOut + x - _hpIn); _hpIn = x; _hpOut = hp;        // ~220 Hz bass roll-off
        float y = MathF.Tanh((0.2f * x + 0.8f * hp) * 2.2f) * 0.8f;               // soft clipping
        if (_rng.Next(ChipRate / 7) == 0) _crackle = (float)(_rng.NextDouble() * 0.5 + 0.1) * (_rng.Next(2) == 0 ? -1 : 1);
        y += _crackle; _crackle *= -0.55f; if (MathF.Abs(_crackle) < 0.001f) _crackle = 0;
        if (_dropLeft > 0) { _dropLeft--; _gain += (0.06f - _gain) * 0.02f; }
        else
        {
            _gain += (1f - _gain) * 0.002f;
            if (--_dropIn <= 0)
            {
                _dropLeft = (int)(ChipRate * (0.04 + _rng.NextDouble() * 0.2));
                _dropIn = (int)(ChipRate * (2 + _rng.NextDouble() * 5));
            }
        }
        return y * _gain;
    }

    public void BeginStutter()
    {
        var s = new short[_ring.Length];
        for (int i = 0; i < s.Length; i++) s[i] = _ring[(_ringPos + i) % _ring.Length];
        _snap = s; _snapPos = 0;
    }

    public void FillStutter(short[] dst, int n)
    {
        if (_snap.Length == 0) { Array.Clear(dst, 0, n); return; }
        for (int i = 0; i < n; i++)
        {
            int v = (_snap[_snapPos] >> 10) << 10;                                // 6-bit crush
            _snapPos = (_snapPos + 1) % _snap.Length;
            if (_rng.Next(400) == 0) v += _rng.Next(-9000, 9000);
            dst[i] = (short)Math.Clamp(v, -32768, 32767);
        }
    }
}
