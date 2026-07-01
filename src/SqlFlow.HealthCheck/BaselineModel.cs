namespace SqlFlow.HealthCheck;

/// <summary>
/// The expectation model for series too short to train AutoML on: the robust median per weekday, falling back
/// to the overall median for weekdays not yet observed. "Plug it into any table and get results" includes the
/// table created last week: a brand-new entity still gets missing-data and gross-deviation detection from day
/// two, and graduates to the trained model as history accumulates. Medians, not means, so a bad day in a
/// five-day history does not poison the expectation.
/// </summary>
public sealed class BaselineModel
{
    /// <summary>The trainer name the report carries when the baseline scored the series.</summary>
    public const string TrainerName = "WeekdayMedianBaseline";

    private readonly Dictionary<DayOfWeek, double> _weekdayMedians;
    private readonly double _overallMedian;

    private BaselineModel(Dictionary<DayOfWeek, double> weekdayMedians, double overallMedian)
    {
        _weekdayMedians = weekdayMedians;
        _overallMedian = overallMedian;
    }

    /// <summary>Fits the baseline on the observed (non-imputed) points.</summary>
    public static BaselineModel Fit(IReadOnlyList<SeriesRow> series)
    {
        ArgumentNullException.ThrowIfNull(series);

        var observed = series.Where(r => r.IsNoData == 0).ToList();
        if (observed.Count == 0)
        {
            throw new InvalidOperationException("The baseline needs at least one observed point.");
        }

        var weekdayMedians = observed
            .GroupBy(r => r.Date.DayOfWeek)
            .ToDictionary(
                g => g.Key,
                g => RobustStatistics.Median(g.Select(r => (double)r.BaseValue).ToList()));
        var overall = RobustStatistics.Median(observed.Select(r => (double)r.BaseValue).ToList());

        return new BaselineModel(weekdayMedians, overall);
    }

    public double Predict(DateTime date)
        => _weekdayMedians.TryGetValue(date.DayOfWeek, out var median) ? median : _overallMedian;
}
