using System.Numerics;

namespace MediaServer.Api.Library;

/// <summary>A stretch that sounds the same in two recordings: where it starts in each (in frames) and how long it is.</summary>
public sealed record FingerprintMatch(int StartA, int StartB, int Length, int Matched);

/// <summary>
/// A small audio fingerprint, in the style of the classic Philips/Haitsma-Kalker scheme: 33 bands between 300 Hz and 2 kHz, and one bit per
/// band pair saying whether the energy difference between neighbouring bands rose or fell since the previous frame. The same music or
/// jingle gives (nearly) the same bits whatever is said over it, which is what makes a show's opening titles findable by comparing two episodes.
/// </summary>
public static class AudioFingerprint
{
    public const int SampleRate = 8000;
    public const int FrameSize = 2048;
    public const int Hop = 512;
    public const double FrameSeconds = (double)Hop / SampleRate;
    private const int Bands = 33;
    private const double LowHz = 300, HighHz = 2000;
    /// <summary>Frames quieter than this (mean square of samples in -1..1) are treated as silence and never match anything.</summary>
    private const double SilenceMeanSquare = 2e-7;

    private static readonly double[] Window = Enumerable.Range(0, FrameSize).Select(i => 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FrameSize - 1))).ToArray();
    private static readonly int[] Edges = BandEdges();

    private static int[] BandEdges()
    {
        var binHz = (double)SampleRate / FrameSize;
        var edges = new int[Bands + 1];
        for (var b = 0; b <= Bands; b++) edges[b] = (int)Math.Round(LowHz * Math.Pow(HighHz / LowHz, (double)b / Bands) / binHz);
        for (var b = 1; b <= Bands; b++) if (edges[b] <= edges[b - 1]) edges[b] = edges[b - 1] + 1;
        return edges;
    }

    /// <summary>One 32-bit print per frame. A print of 0 means "silence": it is never counted as a match.</summary>
    public static uint[] Compute(ReadOnlySpan<short> pcm)
    {
        if (pcm.Length < FrameSize + Hop) return [];
        var frames = (pcm.Length - FrameSize) / Hop + 1;
        var prints = new uint[Math.Max(0, frames - 1)];
        var buf = new Complex[FrameSize];
        var prev = new double[Bands];
        var cur = new double[Bands];
        for (var f = 0; f < frames; f++)
        {
            double ms = 0;
            for (var i = 0; i < FrameSize; i++)
            {
                var s = pcm[f * Hop + i] / 32768.0;
                ms += s * s;
                buf[i] = new Complex(s * Window[i], 0);
            }
            Fft(buf);
            for (var b = 0; b < Bands; b++)
            {
                double e = 0;
                for (var k = Edges[b]; k < Edges[b + 1]; k++) e += buf[k].Real * buf[k].Real + buf[k].Imaginary * buf[k].Imaginary;
                cur[b] = e;
            }
            if (f > 0)
            {
                uint bits = 0;
                if (ms / FrameSize >= SilenceMeanSquare)
                    for (var b = 0; b < 32; b++)
                        if ((cur[b] - cur[b + 1]) - (prev[b] - prev[b + 1]) > 0) bits |= 1u << b;
                prints[f - 1] = bits;
            }
            (prev, cur) = (cur, prev);
        }
        return prints;
    }

    /// <summary>
    /// The longest stretch that matches between the two prints, allowing brief mismatches (speech over the music, a cut). Every shift of one
    /// recording against the other is tried, so it doesn't matter where in each episode the opening starts.
    /// </summary>
    /// <param name="minFrames">Shortest stretch worth reporting.</param>
    /// <param name="maxBits">Most differing bits (of 32) for two frames to count as the same.</param>
    /// <param name="gapFrames">Longest run of non-matching frames bridged inside a stretch.</param>
    /// <param name="minDensity">Share of frames inside the stretch that must match.</param>
    public static FingerprintMatch? BestMatch(uint[] a, uint[] b, int minFrames, int maxBits = 11, int gapFrames = 12, double minDensity = 0.6)
    {
        FingerprintMatch? best = null;
        foreach (var lag in CandidateLags(a, b))
        {
            var start = Math.Max(0, lag);
            var end = Math.Min(a.Length, b.Length + lag);
            if (end - start < minFrames) continue;
            int runStart = -1, last = -1, matches = 0;
            for (var i = start; i < end; i++)                    // a[i] is compared with b[i - lag]
            {
                if (Close(a[i], b, i - lag, maxBits))
                {
                    if (runStart < 0) { runStart = i; matches = 0; }
                    last = i; matches++;
                }
                else if (runStart >= 0 && i - last > gapFrames) { Consider(); runStart = -1; }
            }
            if (runStart >= 0) Consider();

            bool Same(int i) => Close(a[i], b, i - lag, maxBits);
            int Count(int from, int n) { var c = 0; for (var i = from; i < from + n; i++) if (Same(i)) c++; return c; }

            void Consider()
            {
                // A stray coincidence next to the real stretch would drag its edges outwards: start and end where the matching is solid.
                const int Solid = 12, Needed = 9;
                var s = runStart; var e = last;
                while (s + Solid <= e + 1 && Count(s, Solid) < Needed) s++;
                while (e - Solid + 1 >= s && Count(e - Solid + 1, Solid) < Needed) e--;
                var len = e - s + 1;
                if (len < minFrames) return;
                var m = Count(s, len);
                if (m >= minDensity * len && (best is null || m > best.Matched)) best = new FingerprintMatch(s, s - lag, len, m);
            }
        }
        return best;
    }

    private const int TopLags = 12;
    private const int StopBucket = 400;       // a byte value shared by this many frames says nothing

    /// <summary>
    /// Which shifts of one recording against the other are worth checking frame by frame. Every frame votes, through each of its four bytes, for the
    /// shifts that would line it up with frames of the other recording that share that byte; a real match piles up votes on one shift (give or take
    /// a frame), chance doesn't. This turns an all-pairs comparison of ~10 000 x 10 000 frames into a few million votes.
    /// </summary>
    internal static List<int> CandidateLags(uint[] a, uint[] b)
    {
        if (a.Length == 0 || b.Length == 0) return [];
        var buckets = new List<int>?[4][];
        for (var p = 0; p < 4; p++) buckets[p] = new List<int>?[256];
        for (var j = 0; j < b.Length; j++)
        {
            if (b[j] == 0) continue;
            for (var p = 0; p < 4; p++) (buckets[p][(byte)(b[j] >> (8 * p))] ??= []).Add(j);
        }
        var votes = new int[a.Length + b.Length + 2];                    // index = lag + b.Length (lag runs from -(b.Length-1) to a.Length-1)
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] == 0) continue;
            for (var p = 0; p < 4; p++)
                if (buckets[p][(byte)(a[i] >> (8 * p))] is { Count: <= StopBucket } list)
                    foreach (var j in list) votes[i - j + b.Length]++;
        }
        var smooth = new int[votes.Length];
        for (var k = 1; k < votes.Length - 1; k++) smooth[k] = votes[k - 1] + votes[k] + votes[k + 1];
        var picked = new List<int>();
        for (var n = 0; n < TopLags; n++)
        {
            var bestK = -1;
            for (var k = 1; k < smooth.Length - 1; k++) if (smooth[k] > 0 && (bestK < 0 || smooth[k] > smooth[bestK])) bestK = k;
            if (bestK < 0) break;
            picked.Add(bestK - b.Length);
            for (var k = Math.Max(1, bestK - 4); k <= Math.Min(smooth.Length - 2, bestK + 4); k++) smooth[k] = 0;   // the same peak again
        }
        return picked;
    }

    /// <summary>
    /// Whether a frame matches b[j]. The two recordings' frame grids are not aligned (the opening starts at an arbitrary instant), so the
    /// neighbouring frames of b count too: that absorbs up to half a hop of misalignment, which otherwise flips a third of the bits.
    /// </summary>
    private static bool Close(uint x, uint[] b, int j, int maxBits)
    {
        if (x == 0) return false;
        for (var k = Math.Max(0, j - 1); k <= j + 1 && k < b.Length; k++)
        {
            var y = b[k];
            if (y != 0 && BitOperations.PopCount(x ^ y) <= maxBits) return true;
        }
        return false;
    }

    /// <summary>In-place radix-2 FFT (the length must be a power of two).</summary>
    private static void Fft(Complex[] x)
    {
        var n = x.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (x[i], x[j]) = (x[j], x[i]);
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2 * Math.PI / len;
            var wl = new Complex(Math.Cos(ang), Math.Sin(ang));
            for (var i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (var k = 0; k < len / 2; k++)
                {
                    var u = x[i + k]; var v = x[i + k + len / 2] * w;
                    x[i + k] = u + v; x[i + k + len / 2] = u - v;
                    w *= wl;
                }
            }
        }
    }
}
