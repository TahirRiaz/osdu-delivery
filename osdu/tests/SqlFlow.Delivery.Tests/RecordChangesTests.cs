using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine.Search;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The window on a record timestamp (<see cref="RecordChanges"/>): OSDU writes <c>createTime</c> alone on a record's first
/// version and <c>modifyTime</c> from its second on, so a window on <c>modifyTime</c> finds each record by the time it last
/// changed, a record never modified by its <c>createTime</c>, and each in one window only.
/// </summary>
public sealed class RecordChangesTests : IDisposable
{
    private const string Kind = "osdu:wks:master-data--Wellbore:1.0.0";

    private readonly FakeDimensionPlatform _platform = new();

    public void Dispose() => _platform.Dispose();

    private static DateTime At(int hour) => new(2026, 9, 7, hour, 0, 0, DateTimeKind.Utc);

    private void Record(string name, int created, int? modified)
    {
        var record = _platform.Add("dev:master-data--Wellbore:" + name, Kind, new JsonObject { ["FacilityName"] = name });
        record["createTime"] = OsduSearch.LuceneTime(At(created));
        if (modified is { } hour)
        {
            record["modifyTime"] = OsduSearch.LuceneTime(At(hour));
        }
    }

    private IReadOnlyList<string> Found(string clause)
        => _platform.Find(Kind, clause).Select(id => id[(id.LastIndexOf(':') + 1)..]).ToList();

    [Fact]
    public void A_window_on_modifyTime_finds_each_record_by_when_it_last_changed_and_a_record_never_modified_by_its_creation()
    {
        Record("Old", created: 7, modified: null);
        Record("Created", created: 10, modified: null);
        Record("Modified", created: 8, modified: 10);
        Record("Later", created: 10, modified: 12);

        // Created in the window and never modified; modified in the window. One created in the window and modified after it
        // is not here: its last change is in the next window.
        Assert.Equal(["Created", "Modified"], Found(RecordChanges.Within(RecordChanges.ModifyTime, At(9), At(11))));
        Assert.Equal(["Later"], Found(RecordChanges.Within(RecordChanges.ModifyTime, At(11), At(13))));

        // An open start reads everything that last changed before the end, a record never modified included.
        Assert.Equal(["Old"], Found(RecordChanges.Within(RecordChanges.ModifyTime, null, At(9))));

        // Adjacent windows find every record once.
        var windows = new[] { (null as DateTime?, At(9)), (At(9), At(11)), (At(11), At(13)) }
            .SelectMany(w => Found(RecordChanges.Within(RecordChanges.ModifyTime, w.Item1, w.Item2)))
            .ToList();
        Assert.Equal(["Created", "Later", "Modified", "Old"], windows.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_window_on_another_field_is_a_plain_range()
    {
        Record("Created", created: 10, modified: null);
        Record("Modified", created: 8, modified: 10);

        Assert.Equal("createTime:[2026-09-07T09:00:00.000Z TO 2026-09-07T11:00:00.000Z}", RecordChanges.Within(RecordChanges.CreateTime, At(9), At(11)));
        Assert.Equal(["Created"], Found(RecordChanges.Within(RecordChanges.CreateTime, At(9), At(11))));
        Assert.Equal(
            "(modifyTime:[* TO 2026-09-07T11:00:00.000Z} OR (createTime:[* TO 2026-09-07T11:00:00.000Z} AND NOT _exists_:modifyTime))",
            RecordChanges.Within(RecordChanges.ModifyTime, null, At(11)));
    }
}
