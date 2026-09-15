namespace SqlFlow.Delivery.Source;

/// <summary>The dataset names of a flow's source as a mapping and the ledger speak of them.</summary>
public static class SourceDatasets
{
    /// <summary>The record table: the rows a mapping reads as <c>dataset.column</c>. No child dataset may take this name.</summary>
    public const string Record = "record";
}
