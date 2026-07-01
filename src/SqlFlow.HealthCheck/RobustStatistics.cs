namespace SqlFlow.HealthCheck;

/// <summary>
/// Outlier-resistant location and scale estimates. The legacy engine measured anomalies against the mean and
/// standard deviation of the very sample the anomalies sit in, so one large spike inflated the threshold and
/// masked every smaller one. Median and MAD (median absolute deviation, scaled to be stddev-consistent for
/// normal data) ignore the contamination, which is what makes the detector dependable on real warehouse
/// series: the days you most need flagged are the days legacy silently raised the bar for.
/// </summary>
public static class RobustStatistics
{
    /// <summary>The consistency constant that scales MAD to the standard deviation of a normal distribution.</summary>
    public const double MadToSigma = 1.4826;

    public static double Median(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            throw new ArgumentException("A median needs at least one value.", nameof(values));
        }

        var sorted = values.ToArray();
        Array.Sort(sorted);
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>The robust scale of <paramref name="values"/>: scaled MAD, falling back to the mean absolute
    /// deviation when more than half the values sit exactly on the median (a constant-ish series whose MAD
    /// collapses to zero), never negative. A zero return means the series is genuinely constant; callers keep
    /// their absolute floors for that case.</summary>
    public static double Scale(IReadOnlyList<double> values)
    {
        var median = Median(values);
        var deviations = values.Select(v => Math.Abs(v - median)).ToList();
        var mad = Median(deviations);
        if (mad > 0)
        {
            return mad * MadToSigma;
        }

        // MAD collapses when the majority is identical; the mean absolute deviation still sees the minority.
        return deviations.Average() * 1.2533; // mean-absolute-deviation-to-sigma for normal data
    }

    /// <summary>How many robust standard deviations <paramref name="value"/> sits from the center of
    /// <paramref name="median"/>/<paramref name="scale"/>. A zero scale (a truly constant sample) returns 0
    /// for the center and infinity off it, which the caller's floors turn into a sane decision.</summary>
    public static double Z(double value, double median, double scale)
    {
        var deviation = Math.Abs(value - median);
        if (scale > 0)
        {
            return deviation / scale;
        }

        return deviation == 0 ? 0 : double.PositiveInfinity;
    }
}
