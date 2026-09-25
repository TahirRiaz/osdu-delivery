namespace SqlFlow.Delivery.Tests;

/// <summary>One fixture wellbore: the master-data row the wellbore fixture mapping renders, and its alternative names.</summary>
public sealed record SampleWellbore(string FacilityName, string? Description, string? FacilityId, DateTime UpdateDateUtc, IReadOnlyList<string> Aliases);

/// <summary>
/// The wellbores the fixture documents deliver (Fixtures/documents): test input for the storage route, several
/// interfaces in one source and the wellbore chain, which the sample estate does not have because Recall's wellbores
/// already exist on the platform. The fixture search answers for them beside the sample logs' own wellbores.
/// </summary>
public static class FixtureWellbores
{
    /// <summary>When the fixture rows were last changed.</summary>
    public static readonly DateTime UpdatedUtc = new(2026, 9, 1, 6, 30, 0, DateTimeKind.Utc);

    public static IReadOnlyList<SampleWellbore> Wellbores { get; } =
    [
        new SampleWellbore("OSDU-DEV-1-A", "Sample wellbore A", "srn:master-data/Wellbore:A", UpdatedUtc, ["WB-A", "15/9-A"]),
        new SampleWellbore("OSDU-DEV-1-B", "Sample wellbore B", "srn:master-data/Wellbore:B", UpdatedUtc, ["WB-B"]),
    ];

    /// <summary>The columns of the wellbore file the fixture pre flow reads.</summary>
    public static IReadOnlyList<string> WellboreColumns { get; } = ["facility_name", "facility_description", "facility_id", "update_date"];

    /// <summary>The columns of the wellbore alias file the fixture pre flow reads.</summary>
    public static IReadOnlyList<string> AliasColumns { get; } = ["facility_name", "alias_name"];
}
