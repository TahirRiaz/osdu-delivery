namespace SqlFlow.Delivery.Engine.Search;

/// <summary>
/// The clause that finds the records a window on a record timestamp holds, as a retrieval's watermark and a dimension's
/// update read them. OSDU sets <c>modifyTime</c> only from a record's second version on: storage writes <c>createTime</c>
/// alone when it creates a record, and <c>modifyTime</c> on every later version and on a patch of its metadata
/// (storage-core <c>IngestionServiceImpl</c>, <c>PatchRecordsServiceImpl</c>); the indexer indexes <c>modifyTime</c> only
/// when storage holds one (indexer-core <c>IndexerServiceImpl</c>). A range on <c>modifyTime</c> alone would never find a
/// record that was created and not changed since, so a window on it reads such a record by its <c>createTime</c>: each
/// record is found by the time it last changed, once, in the one window that time falls in.
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
        var range = "[" + OsduSearch.LuceneTime(from) + " TO " + OsduSearch.LuceneTime(to) + "}";
        return string.Equals(field, ModifyTime, StringComparison.Ordinal)
            ? $"({ModifyTime}:{range} OR ({CreateTime}:{range} AND NOT _exists_:{ModifyTime}))"
            : field + ":" + range;
    }
}
