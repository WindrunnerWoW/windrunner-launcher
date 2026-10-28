namespace WindrunnerLauncher.Core.Hearthstone.Art;

/// <summary>The few Python semantics the ported art scripts depend on.</summary>
internal static class Py
{
    /// <summary><c>round(x)</c>: half to even, as an int.</summary>
    public static int Round(double x) => (int)Math.Round(x, MidpointRounding.ToEven);

    /// <summary><c>statistics.median</c>: the middle value, or the mean of the two middle values.</summary>
    public static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0)
            throw new InvalidOperationException("no median for empty data");
        var n = sorted.Count;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2;
    }

    /// <summary><c>0.299 * r + 0.587 * g + 0.114 * b</c> (cardimg.lum).</summary>
    public static double Lum(int r, int g, int b) => 0.299 * r + 0.587 * g + 0.114 * b;

    public static double Clamp01(double v) => v < 0 ? 0.0 : v > 1 ? 1.0 : v;

    /// <summary>
    /// cardimg._runs: [(start, end_exclusive)] of true runs, merging runs separated by fewer than
    /// <paramref name="minGap"/> + 1 falses.
    /// </summary>
    public static List<(int A, int B)> Runs(IReadOnlyList<bool> flags, int minGap = 1)
    {
        var runs = new List<(int, int)>();
        int? start = null;
        var lastTrue = 0;
        for (var i = 0; i < flags.Count; i++)
        {
            if (flags[i])
            {
                start ??= i;
                lastTrue = i;
            }
            else if (start is not null && i - lastTrue > minGap)
            {
                runs.Add((start.Value, lastTrue + 1));
                start = null;
            }
        }

        if (start is not null)
            runs.Add((start.Value, lastTrue + 1));
        return runs;
    }
}
