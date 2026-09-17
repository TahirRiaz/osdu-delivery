using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The order a source's interfaces run in (docs/interfaces-design.md section 6): an interface waits for every interface
/// delivering a kind its mapping refers to, and for what after: names; the waves follow; a cycle is cut by after: and
/// then by OSDU group, and refused when neither decides.
/// </summary>
public sealed class InterfaceOrderTests
{
    private const string Well = "osdu:wks:master-data--Well:1.0.0";
    private const string Wellbore = "osdu:wks:master-data--Wellbore:1.3.0";
    private const string Trajectory = "osdu:wks:work-product-component--WellboreTrajectory:1.1.0";
    private const string Log = "osdu:wks:work-product-component--WellLog:1.4.0";
    private const string Unit = "osdu:wks:reference-data--UnitOfMeasure:1.0.0";
    private const string File = "osdu:wks:dataset--File.Generic:1.0.0";
    private const string Product = "osdu:wks:work-product--WorkProduct:1.0.0";

    private static InterfaceSchema Schema(string name, string kind, params (string Property, string Target)[] references)
        => new(name, kind, references.Select(r => new InterfaceReference(r.Property, [r.Target])).ToList());

    private static InterfaceDependency After(string name, string dependsOn) => new(name, dependsOn, $"interfaces.{name}.after names it");

    private static InterfaceOrderPlan Plan(IReadOnlyList<InterfaceDependency> declared, params InterfaceSchema[] schemas)
        => InterfaceOrder.Plan(schemas.Select(s => s.Interface).ToList(), declared, schemas);

    private static List<List<string>> Waves(InterfaceOrderPlan plan) => plan.Waves.Select(w => w.ToList()).ToList();

    [Fact]
    public void A_chain_of_references_runs_one_wave_after_another_and_says_why()
    {
        var plan = Plan(
            [],
            Schema("logs", Log, ("osdu.data.WellboreID", "master-data--Wellbore")),
            Schema("wellbores", Wellbore, ("osdu.data.WellID", "master-data--Well")),
            Schema("wells", Well));

        Assert.Equal([["wells"], ["wellbores"], ["logs"]], Waves(plan));
        Assert.Equal("wells then wellbores then logs", plan.Describe());
        var wait = Assert.Single(plan.WaitsFor("logs"));
        Assert.Equal(("wellbores", DependencyOrigin.Schema), (wait.DependsOn, wait.Origin));
        Assert.Equal($"osdu.data.WellboreID refers to master-data--Wellbore, which wellbores delivers ({Wellbore})", wait.Why);
        Assert.Empty(plan.NotWaitedFor);
        Assert.Equal((1, 3), (plan.WaveOf("WELLS"), plan.WaveOf("logs")));
    }

    [Fact]
    public void A_diamond_runs_the_shared_dependency_first_and_the_two_sides_together()
    {
        var plan = Plan(
            [],
            Schema("top", Product, ("osdu.data.Components[]", "work-product-component--WellLog"), ("osdu.data.Components[]", "work-product-component--WellboreTrajectory")),
            Schema("logs", Log, ("osdu.data.WellboreID", "master-data--Wellbore")),
            Schema("trajectories", Trajectory, ("osdu.data.WellboreID", "master-data--Wellbore")),
            Schema("wellbores", Wellbore));

        Assert.Equal([["wellbores"], ["logs", "trajectories"], ["top"]], Waves(plan));
        Assert.Equal(["logs", "trajectories"], plan.WaitsFor("top").Select(w => w.DependsOn));
    }

    [Fact]
    public void A_reference_pointing_back_to_a_later_group_is_not_waited_for()
    {
        // A wellbore names its definitive trajectory, and the trajectory names its wellbore: master data runs first.
        var plan = Plan(
            [],
            Schema("wellbores", Wellbore, ("osdu.data.DefinitiveTrajectoryID", "work-product-component--WellboreTrajectory")),
            Schema("trajectories", Trajectory, ("osdu.data.WellboreID", "master-data--Wellbore")));

        Assert.Equal([["wellbores"], ["trajectories"]], Waves(plan));
        var cut = Assert.Single(plan.NotWaitedFor);
        Assert.Equal(("wellbores", "trajectories"), (cut.Interface, cut.DependsOn));
        Assert.Contains("osdu.data.DefinitiveTrajectoryID refers to work-product-component--WellboreTrajectory", cut.Why, StringComparison.Ordinal);
        Assert.Contains("not waited for, because master-data (wellbores) runs before work-product-component (trajectories)", cut.Why, StringComparison.Ordinal);
        Assert.Contains("the reference resolves once trajectories has delivered", cut.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void Interfaces_of_one_group_that_refer_to_each_other_are_refused_until_after_says_which_goes_first()
    {
        var sidetracks = Schema("sidetracks", Wellbore, ("osdu.data.KickOffWellbore", "master-data--Wellbore"));
        var wellbores = Schema("wellbores", Wellbore, ("osdu.data.KickOffWellbore", "master-data--Wellbore"));

        var refused = Assert.Throws<DeliveryException>(() => Plan([], sidetracks, wellbores));
        Assert.Contains("The interfaces sidetracks -> wellbores -> sidetracks wait for each other", refused.Message, StringComparison.Ordinal);
        Assert.Contains("sidetracks: osdu.data.KickOffWellbore refers to master-data--Wellbore, which wellbores delivers", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Name the interface that waits for the other with after:", refused.Message, StringComparison.Ordinal);

        var plan = Plan([After("sidetracks", "wellbores")], sidetracks, wellbores);
        Assert.Equal([["wellbores"], ["sidetracks"]], Waves(plan));
        var wait = Assert.Single(plan.WaitsFor("sidetracks"));
        Assert.Equal(DependencyOrigin.After, wait.Origin);
        var cut = Assert.Single(plan.NotWaitedFor);
        Assert.Equal(("wellbores", "sidetracks"), (cut.Interface, cut.DependsOn));
        Assert.EndsWith("not waited for, because after: runs sidetracks after wellbores", cut.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void After_decides_against_the_group_order()
    {
        // The document wants the wellbores after the trajectories, whatever their groups: the reference the other way goes.
        var plan = Plan(
            [After("wellbores", "trajectories")],
            Schema("wellbores", Wellbore),
            Schema("trajectories", Trajectory, ("osdu.data.WellboreID", "master-data--Wellbore")));

        Assert.Equal([["trajectories"], ["wellbores"]], Waves(plan));
        var cut = Assert.Single(plan.NotWaitedFor);
        Assert.Equal(("trajectories", "wellbores"), (cut.Interface, cut.DependsOn));
    }

    [Fact]
    public void A_group_reference_waits_for_every_interface_of_the_group_and_nothing_else_is_waited_for()
    {
        var plan = Plan(
            [],
            Schema("logs", Log, ("osdu.data.Datasets[]", "dataset"), ("osdu.data.WellboreID", "master-data--Wellbore"), ("osdu.data.Curves[].CurveUnit", "reference-data--UnitOfMeasure")),
            Schema("files", File),
            Schema("units", Unit));

        // No interface delivers wellbores: those records are OSDU's already, and nothing waits for them.
        Assert.Equal([["units", "files"], ["logs"]], Waves(plan));
        Assert.Equal(["files", "units"], plan.WaitsFor("logs").Select(w => w.DependsOn));
        Assert.Contains("osdu.data.Datasets[] refers to dataset, which files delivers", plan.WaitsFor("logs")[0].Why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wave_runs_its_interfaces_in_group_order_then_as_the_document_lists_them()
    {
        var plan = Plan([], Schema("products", Product), Schema("logs", Log), Schema("files", File), Schema("wells", Well), Schema("units", Unit), Schema("more-units", Unit));

        Assert.Equal([["units", "more-units", "wells", "files", "logs", "products"]], Waves(plan));
        Assert.True(InterfaceOrder.GroupRank("reference-data") < InterfaceOrder.GroupRank("master-data"));
        Assert.Equal(InterfaceOrder.GroupRank("custom-group"), InterfaceOrder.GroupRank("another-group"));
        Assert.True(InterfaceOrder.GroupRank("work-product") < InterfaceOrder.GroupRank("custom-group"));
    }

    [Fact]
    public void A_run_of_some_interfaces_waits_only_for_the_ones_it_runs()
    {
        var schemas = new[]
        {
            Schema("wellbores", Wellbore),
            Schema("logs", Log, ("osdu.data.WellboreID", "master-data--Wellbore")),
            Schema("archive", Wellbore),
        };

        var some = InterfaceOrder.Plan(["logs", "archive"], [After("logs", "wellbores")], schemas);
        Assert.Equal([["archive"], ["logs"]], Waves(some));
        Assert.Equal(["archive"], some.WaitsFor("LOGS").Select(w => w.DependsOn));

        var all = InterfaceOrder.Plan(["wellbores", "logs", "archive"], [After("LOGS", "Wellbores")], schemas);
        Assert.Equal([["wellbores", "archive"], ["logs"]], Waves(all));
        // The dependency after: declares is named as the document lists the interfaces, and not repeated by the schema's.
        Assert.Equal([("wellbores", DependencyOrigin.After), ("archive", DependencyOrigin.Schema)], all.WaitsFor("logs").Select(w => (w.DependsOn, w.Origin)));
    }

    [Fact]
    public void A_reference_listed_many_times_is_summarized()
    {
        var logs = Schema(
            "logs", Log,
            ("osdu.data.WellboreID", "master-data--Wellbore"), ("osdu.data.A", "master-data--Wellbore"), ("osdu.data.B", "master-data--Wellbore"),
            ("osdu.data.C", "master-data--Wellbore"), ("osdu.data.D", "master-data--Wellbore"));

        var wait = Assert.Single(InterfaceOrder.FromSchemas([logs, Schema("wellbores", Wellbore)]));
        Assert.StartsWith("osdu.data.WellboreID refers to master-data--Wellbore; osdu.data.A refers to master-data--Wellbore; osdu.data.B refers to master-data--Wellbore; and 2 more, which wellbores delivers", wait.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sample_mappings_refer_to_what_their_templates_say()
    {
        var loader = new DeliveryDocumentLoader();
        InterfaceSchema Describe(string name, string mapping, string kind)
        {
            var definition = loader.LoadMapping(Path.Combine(Samples.Mappings, mapping + ".yaml"));
            var template = OsduTemplate.From(Samples.SampleTemplate(kind));
            return InterfaceSchemas.Describe(name, definition, template);
        }

        var logs = Describe("logs", "WellLog@1.4.0", Samples.WellLogKind);
        var wellbores = Describe("wellbores", "Wellbore@1.0.0", Samples.WellboreKind);

        Assert.Equal((Samples.WellLogKind, "work-product-component--WellLog", "work-product-component"), (logs.Kind, logs.EntityType, logs.GroupType));
        Assert.Equal(["master-data--Wellbore"], logs.References.Single(r => r.Property == "osdu.data.WellboreID").Targets);
        Assert.DoesNotContain(logs.References, r => r.Property == "osdu.data.Name");
        Assert.All(wellbores.References, r => Assert.All(r.Targets, t => Assert.StartsWith("reference-data--", t, StringComparison.Ordinal)));
        Assert.Contains(wellbores.References, r => r.Property == "osdu.data.NameAliases[].AliasNameTypeID");

        var plan = InterfaceOrder.Plan(["logs", "wellbores"], [], [logs, wellbores]);
        Assert.Equal([["wellbores"], ["logs"]], Waves(plan));
    }
}
