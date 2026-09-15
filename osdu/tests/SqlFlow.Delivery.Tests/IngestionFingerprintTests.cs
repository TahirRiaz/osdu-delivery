using SqlFlow.Delivery.Source;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The version the ingestion tables give a record (docs/stage4-design.md section 2.5). It is what lets a run decide a
/// record is unchanged without rendering it, so it has to move when any row of the record moved, and stay put when
/// nothing did: the same rows read again, on another node or another day, are the same fingerprint.
/// </summary>
public class IngestionFingerprintTests
{
    private static readonly DateTime At = new(2026, 9, 1, 10, 15, 0, DateTimeKind.Utc);

    private static string Of(DateTime? record, params (string Name, int Rows, DateTime? Max)[] datasets)
        => IngestionFingerprint.Of(record, datasets.ToDictionary(d => d.Name, d => new DatasetVersion(d.Rows, d.Max), StringComparer.Ordinal));

    [Fact]
    public void The_same_rows_are_the_same_fingerprint_however_their_datasets_are_ordered()
    {
        var curves = ("curves", 3, (DateTime?)At);
        var tops = ("tops", 1, (DateTime?)At.AddMinutes(-5));

        Assert.Equal(Of(At, curves, tops), Of(At, curves, tops));
        // Datasets are folded in name order, so the order they were read in never reaches the fingerprint.
        Assert.Equal(Of(At, curves, tops), Of(At, tops, curves));
    }

    [Fact]
    public void A_record_whose_own_row_changed_is_a_different_fingerprint()
    {
        var unchanged = Of(At, ("curves", 3, At));
        Assert.NotEqual(unchanged, Of(At.AddTicks(1), ("curves", 3, At)));
        Assert.NotEqual(unchanged, Of(null, ("curves", 3, At)));
    }

    [Fact]
    public void A_child_row_added_removed_or_updated_moves_its_record_s_fingerprint()
    {
        var unchanged = Of(At, ("curves", 3, At));

        // A row added or removed: the count moves even when the newest update time does not.
        Assert.NotEqual(unchanged, Of(At, ("curves", 4, At)));
        Assert.NotEqual(unchanged, Of(At, ("curves", 2, At)));

        // A row updated in place: the count stands still and the newest update time moves.
        Assert.NotEqual(unchanged, Of(At, ("curves", 3, At.AddTicks(1))));

        // A dataset gained or lost entirely, and one with no rows at all.
        Assert.NotEqual(unchanged, Of(At, ("curves", 3, At), ("tops", 1, At)));
        Assert.NotEqual(unchanged, Of(At));
        Assert.NotEqual(Of(At, ("curves", 0, null)), Of(At));
    }

    [Fact]
    public void A_dataset_name_is_part_of_the_version_so_the_same_counts_under_another_name_differ()
        => Assert.NotEqual(Of(At, ("curves", 3, At)), Of(At, ("tops", 3, At)));

    [Fact]
    public void An_update_time_is_read_as_utc_however_the_row_carried_its_kind()
    {
        // A table's datetime2 arrives unspecified; the same moment local, unspecified or UTC is one version, because a
        // record must not look changed because of how a driver handed its column back.
        var utc = Of(At, ("curves", 1, At));
        Assert.Equal(utc, Of(DateTime.SpecifyKind(At, DateTimeKind.Unspecified), ("curves", 1, DateTime.SpecifyKind(At, DateTimeKind.Unspecified))));
        Assert.Equal(utc, Of(At.ToLocalTime(), ("curves", 1, At.ToLocalTime())));
    }
}
