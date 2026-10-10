namespace SqlFlow.Delivery.Engine.Search;

/// <summary>
/// The clause that finds the records a window on a record timestamp holds, as a retrieval's watermark and a dimension's
/// incremental load read them. OSDU sets <c>modifyTime</c> only from a record's second version on: storage writes
/// <c>createTime</c> alone when it creates a record, and <c>modifyTime</c> on every later version and on a patch of its
/// metadata (storage-core <c>IngestionServiceImpl</c>, <c>PatchRecordsServiceImpl</c>); the indexer indexes
/// <c>modifyTime</c> only when storage holds one (indexer-core <c>IndexerServiceImpl</c>). A range on <c>modifyTime</c>
/// alone would never find a record that was created and not changed since, so a record's change time is the first of
/// several timestamps it holds, <c>modifyTime</c> and then <c>createTime</c>: each record is found by the time it last
/// changed, once, in the one window that time falls in.
/// </summary>
public static class RecordChanges
{
    /// <summary>When a record last changed, from its second version on.</summary>
    public const string ModifyTime = "modifyTime";

    /// <summary>When a record was created; the time its first version changed it.</summary>
    public const string CreateTime = "createTime";

    /// <summary>
    /// The clause finding the records whose <paramref name="field"/> falls in <c>[from, to)</c>, <paramref name="from"/> null
    /// for no lower bound. On <see cref="ModifyTime"/> a record holding none is read by its <see cref="CreateTime"/>; any other
    /// field is a plain range.
    /// </summary>
    public static string Within(string field, DateTime? from, DateTime to)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        return string.Equals(field, ModifyTime, StringComparison.Ordinal) ? Within([ModifyTime, CreateTime], from, to) : Within([field], from, to);
    }

    /// <summary>
    /// The clause finding the records whose change time falls in <c>[from, to)</c>, a record's change time being the first
    /// of <paramref name="fields"/> it holds: <c>(a:[from TO to} OR (b:[from TO to} AND NOT _exists_:a) OR ...)</c>.
    /// </summary>
    public static string Within(IReadOnlyList<string> fields, DateTime? from, DateTime to)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Count == 0 || fields.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("A record's change time is read from at least one field, each named.", nameof(fields));
        }

        var range = "[" + OsduSearch.LuceneTime(from) + " TO " + OsduSearch.LuceneTime(to) + "}";
        if (fields.Count == 1)
        {
            return fields[0] + ":" + range;
        }

        var parts = fields.Select((field, i) => i == 0
            ? field + ":" + range
            : "(" + field + ":" + range + string.Concat(fields.Take(i).Select(before => " AND NOT _exists_:" + before)) + ")");
        return "(" + string.Join(" OR ", parts) + ")";
    }
}
