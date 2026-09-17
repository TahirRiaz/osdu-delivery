namespace SqlFlow.Delivery.Model;

/// <summary>What a column of a Reservoir Management DDMS table takes (osdu/specs/reservoir-management-ddms/INTEGRATION.md section 2.5).</summary>
public enum ReservoirManagementColumn
{
    /// <summary>A JSON number: the table's numeric columns.</summary>
    Number,

    /// <summary>A JSON string.</summary>
    Text,

    /// <summary>A JSON boolean.</summary>
    Boolean,

    /// <summary>A whole JSON number: a bigint column.</summary>
    WholeNumber,

    /// <summary>A string or a number: a column the brief gives no type for (a key of a reference table, a date, a table label).</summary>
    Scalar,
}

/// <summary>
/// One table of rows the Reservoir Management DDMS keeps for a header record, in its own database only
/// (osdu/specs/reservoir-management-ddms/INTEGRATION.md section 2.5). A row is posted alone (<c>POST /ddms/{segment}</c>)
/// and keyed by the integer the service's sequence gives it; the rows of a table below it name that key.
/// </summary>
/// <param name="Segment">The path segment of the table (<c>phi-k-synthesis-rt</c>).</param>
/// <param name="Header">The segment of the header collection the table's rows belong to.</param>
/// <param name="Parent">The segment of the table whose rows the rows belong to, or null for a table directly under the header.</param>
/// <param name="Columns">The columns a row may give, by name, with what each takes.</param>
/// <param name="Required">The columns a row must give (NOT NULL without a default in the service's database).</param>
/// <param name="ForecastBase">Whether a row names the forecast base of its forecast (<c>id_forecast_base</c>).</param>
public sealed record ReservoirManagementTable(
    string Segment,
    string Header,
    string? Parent,
    IReadOnlyDictionary<string, ReservoirManagementColumn> Columns,
    IReadOnlyList<string> Required,
    bool ForecastBase = false)
{
    /// <summary>The column the service keys a row by (<c>id_phi_k_synthesis_rt</c>).</summary>
    public string KeyColumn => ReservoirManagementTables.KeyColumn(Segment);

    /// <summary>The column naming the header record (<c>id_phi_k_synthesis</c>).</summary>
    public string HeaderColumn => ReservoirManagementTables.KeyColumn(Header);

    /// <summary>The column naming the row a row belongs to: the header's column, or the parent table's key column.</summary>
    public string ParentColumn => ReservoirManagementTables.KeyColumn(Parent ?? Header);

    /// <summary>The columns the route fills from the rows above a row, which the row itself does not give.</summary>
    public IReadOnlyList<string> RouteColumns
        => new[] { KeyColumn, HeaderColumn, ParentColumn, ReservoirManagementTables.ParentObjectColumn }
            .Concat(ForecastBase ? [ReservoirManagementTables.ForecastBaseColumn] : Array.Empty<string>())
            .Distinct(StringComparer.Ordinal)
            .ToList();
}

/// <summary>
/// One header collection of the Reservoir Management DDMS: the OSDU records it keeps a copy of, the only kind its list
/// call searches and takes into its database, and the tables of rows below it
/// (osdu/specs/reservoir-management-ddms/INTEGRATION.md sections 2.4 and 2.5).
/// </summary>
public sealed record ReservoirManagementHeader(string Segment, string EntityType, string Kind, IReadOnlyList<string> Tables);

/// <summary>The Reservoir Management DDMS's header collections and the tables below them, as its code and database define them.</summary>
public static class ReservoirManagementTables
{
    /// <summary>The column every row names its header record's parent in (a Reservoir, Segment or Sector id).</summary>
    public const string ParentObjectColumn = "parent_object_id";

    /// <summary>The column a forecast's rows name its forecast base in.</summary>
    public const string ForecastBaseColumn = "id_forecast_base";

    private const ReservoirManagementColumn N = ReservoirManagementColumn.Number;
    private const ReservoirManagementColumn T = ReservoirManagementColumn.Text;
    private const ReservoirManagementColumn B = ReservoirManagementColumn.Boolean;
    private const ReservoirManagementColumn I = ReservoirManagementColumn.WholeNumber;
    private const ReservoirManagementColumn A = ReservoirManagementColumn.Scalar;

    /// <summary>
    /// The nine header collections, each with the kind its list call searches [app/core/constants.py:41-49]. The tank
    /// datum kind is spelled as the service's code spells it (<c>AcquiferInterpretation</c>); Kr and Phi-K syntheses share
    /// one kind.
    /// </summary>
    public static IReadOnlyList<ReservoirManagementHeader> Headers { get; } =
    [
        new("estimated-volumes", "work-product-component--ReservoirEstimatedVolumes", "osdu:wks:work-product-component--ReservoirEstimatedVolumes:1.0.0", ["estimated-volumes-det"]),
        new("pvt-properties", "master-data--FluidSystem", "osdu:wks:master-data--FluidSystem:1.0.0", []),
        new("geological-labels", "work-product-component--GeoLabelSet", "osdu:wks:work-product-component--GeoLabelSet:1.0.0", []),
        new("petro-properties", "work-product-component--ReservoirModelScenario", "osdu:wks:work-product-component--ReservoirModelScenario:1.0.0", []),
        new("tank-datum", "work-product-component--AcquiferInterpretation", "osdu:wks:work-product-component--AcquiferInterpretation:1.1.0", ["aquifer-datum"]),
        new("fluid-synthesis", "work-product-component--FluidSystemCharacterization", "osdu:wks:work-product-component--FluidSystemCharacterization:1.0.0", ["fluid-synthesis-tank-pvt", "fluid-synthesis-tank-blackoil"]),
        new("kr-synthesis", "work-product-component--PersistedCollection", "osdu:wks:work-product-component--PersistedCollection:1.2.0", ["kr-synthesis-rt"]),
        new("phi-k-synthesis", "work-product-component--PersistedCollection", "osdu:wks:work-product-component--PersistedCollection:1.2.0", ["phi-k-synthesis-rt"]),
        new("forecast", "work-product-component--ProductionValues", "osdu:wks:work-product-component--ProductionValues:1.0.0", ["forecast-fluid", "forecast-det"]),
    ];

    /// <summary>The twelve tables of rows, less the forecast bases, which belong to no header record.</summary>
    public static IReadOnlyList<ReservoirManagementTable> Tables { get; } =
    [
        new(
            "estimated-volumes-det",
            "estimated-volumes",
            null,
            Columns(("probability", T), ("riagip", N), ("ridgip", N), ("rigcgip", N), ("rigip", N), ("rooip", N), ("rpiip", N), ("iagip", N), ("idgip", N), ("igcgip", N), ("igip", N), ("ooip", N), ("piip", N), ("stooip", N)),
            []),
        new(
            "aquifer-datum",
            "tank-datum",
            null,
            Columns(
                ("name", T), ("encroa_angle", N), ("aqui_comp", N), ("aqui_poro", N), ("io_rd_ratio", N), ("res_thick", N), ("res_radius", N), ("aqui_pem", N), ("aqui_vol", N),
                ("aqui_type", T), ("ref_aqui", T), ("ref_report", T), ("ecl_infl_tab", T), ("comment", T), ("ref_report_filename", T)),
            ["name"]),
        new(
            "fluid-synthesis-tank-pvt",
            "fluid-synthesis",
            null,
            Columns(
                ("depth", N), ("pressure", N), ("cond_dens", N), ("gas_dens", N), ("oil_dens", N), ("wat_dens", N), ("wat_compr", N), ("oil_compr", N), ("rock_compr", N),
                ("gas_visco", N), ("oil_visco", N), ("wat_visco", N), ("pb", N), ("temperature", N), ("bg", N), ("bo", N), ("bw", N), ("cgr_res", N), ("cgr_vapo", N), ("rs", N),
                ("z_factor", N), ("comment", T), ("description", T)),
            []),
        new(
            "fluid-synthesis-tank-blackoil",
            "fluid-synthesis",
            null,
            Columns(
                ("fluid_unit", A), ("flu_tab_type", A), ("surf_oil_den", N), ("pvdg_bg", N), ("pvdg_pgas", N), ("pvdg_vis_gas", N), ("pvtg_bg_prim", N), ("pvtg_rv", N), ("pvtg_bo", N),
                ("pvtg_rs", N), ("pvtg_pres", N), ("pvtg_vis_gas", N), ("pvto_pb", N), ("pvto_oil_vis", N)),
            ["fluid_unit", "flu_tab_type"]),
        new(
            "kr-synthesis-rt",
            "kr-synthesis",
            null,
            Columns(
                ("rt_tab_name", T), ("rt_name", T), ("rt_geol_desc", T), ("krg_max", N), ("krc_max", N), ("krw_max", N), ("sgr", N), ("sgr_hys", N), ("sor_hys", N), ("sorw", N),
                ("swi", N), ("swi_hys", N), ("gas_sweep", N), ("water_sweep", N), ("corey_ng", N), ("corey_no", N), ("corey_nw", N), ("rt_kr_table", B), ("has_kr_table", B)),
            ["rt_tab_name"]),
        new(
            "kr-synthesis-kr",
            "kr-synthesis",
            "kr-synthesis-rt",
            Columns(
                ("sat_tab_type", A), ("comment", T), ("sgnf_krg", N), ("sgnf_sg", N), ("sgof_krg", N), ("sgof_krog", N), ("sgof_sg", N), ("sgof_pcow", N), ("sof3_krog", N),
                ("sof3_krow", N), ("sof3_so", N), ("swfn_krw", N), ("swfn_sw", N), ("swfn_pcow", N), ("swof_krow", N), ("swof_sw", N), ("swof_krw", N), ("swof_pcow", N),
                ("sgfn_pcog", N)),
            ["sat_tab_type"]),
        new(
            "phi-k-synthesis-rt",
            "phi-k-synthesis",
            null,
            Columns(("rt_tab_name", T), ("rt_phi_k_tab", I), ("rt_name", T), ("rt_geol_desc", T), ("phi_k_model", T)),
            ["rt_tab_name", "rt_phi_k_tab"]),
        new(
            "phi-k-synthesis-phi-k",
            "phi-k-synthesis",
            "phi-k-synthesis-rt",
            Columns(("comment", T), ("rhos", N), ("kgas", N), ("kwat", N), ("phie", N), ("phit", N), ("depth", N), ("id_r_ori", A)),
            []),
        new(
            "forecast-fluid",
            "forecast",
            null,
            Columns(("id_fluid", A), ("qf_cutoff", N), ("qf_fore", N), ("qf_hist", N), ("qc_d_fluid", N), ("fluid_const", N), ("fluid_slope", N), ("fluid_method", T)),
            ["id_fluid"],
            ForecastBase: true),
        new(
            "forecast-det",
            "forecast",
            null,
            Columns(("dt", A), ("optime", N), ("optime_p", N), ("cgr", N), ("glr", N), ("gor", N), ("wct", N), ("wgr", N), ("comment", T)),
            ["dt"],
            ForecastBase: true),
        new(
            "forecast-det-fluid",
            "forecast",
            "forecast-det",
            Columns(("id_fluid", A), ("cum_fluid", N), ("qf_c", N), ("qf_p", N), ("fluid", N)),
            ["id_fluid"],
            ForecastBase: true),
    ];

    /// <summary>The column a row of <paramref name="segment"/>'s table is keyed by: <c>id_</c> and the segment with underscores.</summary>
    public static string KeyColumn(string segment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segment);
        return "id_" + segment.Replace('-', '_');
    }

    /// <summary>The header collection served under <paramref name="segment"/>, or null.</summary>
    public static ReservoirManagementHeader? Header(string segment)
        => Headers.FirstOrDefault(h => string.Equals(h.Segment, segment, StringComparison.Ordinal));

    /// <summary>The table served under <paramref name="segment"/>, or null.</summary>
    public static ReservoirManagementTable? Table(string segment)
        => Tables.FirstOrDefault(t => string.Equals(t.Segment, segment, StringComparison.Ordinal));

    /// <summary>The tables directly below <paramref name="segment"/>, a header collection or a table, in the order the service lists them.</summary>
    public static IReadOnlyList<ReservoirManagementTable> Below(string segment)
        => Header(segment) is { } header
            ? header.Tables.Select(t => Table(t)!).Where(t => t.Parent is null).ToList()
            : Tables.Where(t => string.Equals(t.Parent, segment, StringComparison.Ordinal)).ToList();

    private static Dictionary<string, ReservoirManagementColumn> Columns(params (string Name, ReservoirManagementColumn Type)[] columns)
        => columns.ToDictionary(c => c.Name, c => c.Type, StringComparer.Ordinal);
}
