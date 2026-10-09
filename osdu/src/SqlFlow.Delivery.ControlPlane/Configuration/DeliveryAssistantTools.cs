using SqlFlow.Assistant;
using SqlFlow.ControlPlane.Configuration;

namespace SqlFlow.Delivery.ControlPlane.Configuration;

/// <summary>
/// The MCP tools the control plane's chat assistant may call in OSDU Delivery: SQLFlow's tools that answer from
/// metadata, and the delivery module's readers. The assistant's tool source here is the product's own MCP server
/// (<c>osdu/hosts/osdu-delivery-mcp</c>), which offers the <c>delivery_*</c> tools and leaves out every tool of SQLFlow's
/// that reads rows, so SQLFlow's default list, which names those, is not this product's.
/// </summary>
/// <remarks>
/// A tool absent from the allowlist does not look restricted to the model, it looks absent, and the assistant then says
/// the product cannot do the thing. That is why the list is spelled out and why <see cref="Excluded"/> exists beside it:
/// <c>DeliveryAssistantToolsTests</c> reads the tool names out of the server's sources and fails when a delivery tool is
/// on neither list, or when either names a tool the server does not offer.
/// </remarks>
public static class DeliveryAssistantTools
{
    /// <summary>
    /// SQLFlow's tools the assistant may call: its read surface over the catalog, lineage, runs, schedules, search,
    /// insights and documentation, and the two lookups that answer how a table is keyed and joined from the catalog
    /// alone. None of them reaches a datasource.
    /// </summary>
    public static IReadOnlyList<string> Platform { get; } = [.. McpOptions.SlackDefaultTools, "get_table_key"];

    /// <summary>The delivery module's readers: the ledger, flows, the audit trail, documents, caches, tests and dimensions.</summary>
    public static IReadOnlyList<string> Delivery { get; } =
    [
        "delivery_find_records", "delivery_record",
        "delivery_flow", "delivery_flow_records", "delivery_submissions", "delivery_retrievals", "delivery_activities",
        "delivery_partitions", "delivery_config",
        "delivery_caches", "delivery_cache_versions", "delivery_cache_changes",
        "delivery_mappings", "delivery_check_mapping", "delivery_scaffold_mapping",
        "delivery_templates", "delivery_osdu_schemas", "delivery_osdu_schema_compare",
        "delivery_assertions", "delivery_assertion_runs",
        "delivery_dimensions",
    ];

    /// <summary>
    /// The delivery tools deliberately kept from the assistant, listed rather than merely absent so the omission is a
    /// decision on the record: every one starts work or changes what the ledger or OSDU holds, as SQLFlow's
    /// <c>trigger_run</c> does, and a chat answer never does that. They remain available to a person's own MCP client,
    /// under the <c>operate</c> policy, which every signed-in user passes (only admin checks a scope).
    /// </summary>
    public static IReadOnlyList<string> Excluded { get; } =
    [
        "delivery_probe_target",
        "delivery_verify_record",
        "delivery_sync_with_source",
        "delivery_release_records",
        "delivery_redeliver_record",
        "delivery_decide_cache_changes",
    ];

    /// <summary>What the assistant may call: <see cref="Platform"/> and <see cref="Delivery"/>.</summary>
    public static IReadOnlyList<string> Allowed { get; } = [.. Platform, .. Delivery];

    /// <summary>
    /// Makes <see cref="Allowed"/> the assistant's tools, unless the deployment configured a list of its own
    /// (<c>ControlPlane:Assistant:Mcp:AllowedTools</c>), which is kept as it is.
    /// </summary>
    public static void Apply(ControlPlaneOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Assistant.Mcp.ApplySurfaceDefault(Allowed);
    }
}
