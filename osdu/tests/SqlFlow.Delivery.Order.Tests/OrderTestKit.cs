namespace SqlFlow.Delivery.Order.Tests;

/// <summary>Builders for the order's tests: schemas by their relationships, deliveries by what their mappings fill.</summary>
internal static class OrderTestKit
{
    public const string Well = "osdu:wks:master-data--Well:1.2.0";
    public const string Wellbore = "osdu:wks:master-data--Wellbore:1.3.0";
    public const string WellLog = "osdu:wks:work-product-component--WellLog:1.4.0";
    public const string Trajectory = "osdu:wks:work-product-component--WellboreTrajectory:1.3.0";
    public const string Unit = "osdu:wks:reference-data--UnitOfMeasure:1.0.0";
    public const string Quantity = "osdu:wks:reference-data--UnitQuantity:1.0.0";
    public const string Organisation = "osdu:wks:master-data--Organisation:1.2.0";
    public const string FileKind = "osdu:wks:dataset--File.Generic:1.0.0";

    public static SchemaNode Schema(string kind, params (string Property, string[] Targets)[] relationships)
        => new(kind, OsduEntity.OfKind(kind)!, [.. relationships.Select(r => new SchemaRelationship(r.Property, r.Targets))]);

    /// <summary>The schemas the tests share: OSDU's own shapes for the kinds they deliver, cut down to the relationships that matter.</summary>
    public static OsduSchemaGraph Graph(params SchemaNode[] extra)
        => OsduSchemaGraph.Of(
        [
            Schema(Quantity),
            Schema(Unit, ("osdu.data.UnitQuantityID", ["reference-data--UnitQuantity"])),
            Schema(Organisation),
            Schema(Well, ("osdu.data.VerticalMeasurements[].VerticalMeasurementUnitOfMeasureID", ["reference-data--UnitOfMeasure"]), ("osdu.data.CurrentOperatorID", ["master-data--Organisation"])),
            Schema(Wellbore,
                ("osdu.data.WellID", ["master-data--Well"]),
                ("osdu.data.DefinitiveTrajectoryID", ["work-product-component--WellboreTrajectory"]),
                ("osdu.data.ParentWellboreID", ["master-data--Wellbore"])),
            Schema(Trajectory, ("osdu.data.WellboreID", ["master-data--Wellbore"]), ("osdu.data.Datasets[]", ["dataset"])),
            Schema(WellLog,
                ("osdu.data.WellboreID", ["master-data--Wellbore"]),
                ("osdu.data.Curves[].CurveUnit", ["reference-data--UnitOfMeasure"]),
                ("osdu.data.CompanyID", ["master-data--Organisation"]),
                ("osdu.data.Datasets[]", ["dataset"])),
            Schema(FileKind),
            .. extra,
        ]);

    public static OrderDelivery Deliver(
        string flow, string kind, string[]? filled, string partition = "dev", string repository = "", string? @interface = null, string[]? searched = null)
        => new()
        {
            Partition = partition,
            Repository = repository,
            Flow = flow,
            Interface = @interface,
            Kind = kind,
            Filled = filled,
            Searched = searched ?? [],
        };

    public static OrderPlan Plan(params OrderDelivery[] deliveries) => OsduOrder.Plan("dev", deliveries, Graph());

    public static string Edges(OrderPlan plan) => string.Join(" ", plan.Edges.Select(e => $"{Short(e.From)}>{Short(e.To)}"));

    public static string Levels(OrderPlan plan) => string.Join(" | ", plan.Levels.Select(l => string.Join(",", l.Select(Short))));

    public static string Short(string entityType) => OsduEntity.EntityOf(entityType);
}
