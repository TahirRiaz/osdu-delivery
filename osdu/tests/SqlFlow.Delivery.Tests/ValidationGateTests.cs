using System.Text.Json.Nodes;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Validation;
using Xunit;
using static SqlFlow.Delivery.Tests.ValidationFixtures;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The gate every document goes through before it is sent (<see cref="ValidationGate"/>): a verdict for every document a try
/// writes, against the template the document was rendered for; held or sent as the flow's <c>target.validation</c> says; a
/// release's acceptance honoured for the document it accepted alone; the storage check of <c>target.verifyReferences</c>
/// kept as it always held; what a route fills left unjudged; and one lookup of references per group.
/// </summary>
public sealed class ValidationGateTests
{
    private const string RenderedFor = "9f3c41d07a2b88e1";

    private static readonly TestClock Clock = new();

    private static FlowDefinition Flow(ValidationMode mode = ValidationMode.Report, UnverifiedAction unverified = UnverifiedAction.Send, DeliveryProtocol protocol = DeliveryProtocol.Storage)
    {
        var flow = new DeliveryDocumentLoader().ParseFlow("""
            flowType: delivery
            name: gated
            source:
              connection: ${env:OSDU_DATA_DB}
              record: { object: Db.ing.Thing, key: [code] }
              work: work
            render:
              mapping: Thing@1.0.0
            target:
              endpoint: https://osdu.example.com
              headers: { data-partition-id: dev }
              protocol: storage
            """.ReplaceLineEndings("\n"), "flow.yaml");
        return flow with
        {
            Target = flow.Target with
            {
                Protocol = protocol,
                Validation = new ValidationPolicy { Mode = mode, Unverified = unverified },
            },
        };
    }

    private static RecordState State(string key = "T-1", string? accepted = null, IReadOnlyList<RecordReference>? references = null, string? context = null) => new()
    {
        DeliveryKey = DeliveryKey.Derive("gated", [key]),
        FlowId = FlowId.Of("gated"),
        SourceKey = key,
        MappingName = "Thing",
        TargetId = $"dev:master-data--Thing:{key}",
        PendingDocumentRef = "0:0:10",
        PendingRenderContext = context ?? $$$"""{"mapping":"Thing@1.0.0","schema":"{{{RenderedFor}}}","parameters":{}}""",
        PendingMetadataHash = "hash-of-this-document",
        AcceptedMetadataHash = accepted,
        PendingMetadata = true,
        PendingReferences = references ?? [],
    };

    /// <summary>The schemas the gate is given: the fixture's for its kind, whatever version is asked; what was asked is kept.</summary>
    private sealed class Schemas
    {
        public List<(string Kind, string? Version)> Asked { get; } = [];

        public bool Saved { get; init; } = true;

        public Task<SchemaSnapshot?> Of(string kind, string? version, CancellationToken ct)
        {
            Asked.Add((kind, version));
            return Task.FromResult(Saved && kind == Kind ? Schema : null);
        }
    }

    /// <summary>A reference source answering from a set, counting its questions.</summary>
    private sealed class Source(params string[] holds)
    {
        public int Calls { get; private set; }

        public Task<IReadOnlySet<string>> Answer(IReadOnlyCollection<string> ids, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlySet<string>>(ids.Where(holds.Contains).ToHashSet(StringComparer.Ordinal));
        }
    }

    private static ValidationGate Gate(FlowDefinition flow, Schemas? schemas = null, ReferenceResolver? references = null)
        => new(flow, (schemas ?? new Schemas()).Of, references ?? ReferenceResolver.None, Clock);

    private static async Task<GateDecision> DecideAsync(
        ValidationGate gate, RecordState state, JsonObject document, bool writesMetadata = true, AssertionFindings? assertions = null)
        => Assert.Single(await gate.DecideAsync([(state, document, writesMetadata, assertions)]));

    private static JsonObject Invalid() => Record(r => DataOf(r)["Status"] = "Planned");

    [Fact]
    public async Task Under_report_a_record_breaking_its_schema_is_sent_and_its_verdict_says_so()
    {
        var decision = await DecideAsync(Gate(Flow()), State(), Invalid());

        Assert.Null(decision.Hold);
        Assert.Equal(ValidationOutcome.Invalid, decision.Verdict.Outcome);
        Assert.Equal("data.Status", Assert.Single(decision.Verdict.Problems).At);
        Assert.False(decision.Verdict.Accepted);
    }

    [Fact]
    public async Task Under_enforce_a_record_breaking_its_schema_is_held_with_the_rules_it_breaks_and_a_valid_one_goes()
    {
        var gate = Gate(Flow(ValidationMode.Enforce));

        var held = await DecideAsync(gate, State(), Invalid());
        Assert.StartsWith($"validation: the record breaks the schema of {Kind}: data.Status enum: ", held.Hold, StringComparison.Ordinal);
        Assert.Contains("validation.mode is enforce, so it is held", held.Hold, StringComparison.Ordinal);

        var sent = await DecideAsync(gate, State(), ValidRecord());
        Assert.Null(sent.Hold);
        Assert.Equal(ValidationOutcome.Valid, sent.Verdict.Outcome);
    }

    [Fact]
    public async Task A_document_a_release_accepted_is_sent_whatever_its_verdict_and_only_that_document()
    {
        var gate = Gate(Flow(ValidationMode.Enforce));

        var accepted = await DecideAsync(gate, State(accepted: "hash-of-this-document"), Invalid());
        Assert.Null(accepted.Hold);
        Assert.True(accepted.Verdict.Accepted);
        Assert.Equal(ValidationOutcome.Invalid, accepted.Verdict.Outcome);
        Assert.EndsWith("; accepted by a release", accepted.Verdict.Summary(), StringComparison.Ordinal);

        // The acceptance of another document of the record does not reach this one.
        var other = await DecideAsync(gate, State(accepted: "hash-of-an-earlier-document"), Invalid());
        Assert.NotNull(other.Hold);
        Assert.False(other.Verdict.Accepted);
    }

    [Theory]
    [InlineData(UnverifiedAction.Send, false)]
    [InlineData(UnverifiedAction.Hold, true)]
    public async Task A_record_whose_template_is_not_saved_is_unverified_and_held_only_when_the_flow_says_so(UnverifiedAction unverified, bool holds)
    {
        var schemas = new Schemas { Saved = false };
        var decision = await DecideAsync(Gate(Flow(unverified: unverified), schemas), State(), ValidRecord());

        Assert.Equal(ValidationOutcome.Unverified, decision.Verdict.Outcome);
        var why = Assert.Single(decision.Verdict.Unverified).Message;
        Assert.Contains($"template {Kind} at version {RenderedFor}", why, StringComparison.Ordinal);
        Assert.Equal(holds, decision.Hold is not null);
        if (holds)
        {
            Assert.Contains("could not be fully checked", decision.Hold, StringComparison.Ordinal);
            Assert.Contains("validation.unverified is hold", decision.Hold, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_document_is_checked_against_the_template_version_it_was_rendered_for()
    {
        var schemas = new Schemas();
        await DecideAsync(Gate(Flow(), schemas), State(), ValidRecord());
        Assert.Equal((Kind, RenderedFor), Assert.Single(schemas.Asked));

        // A record whose render context names no version, or cannot be read, asks for the kind alone.
        var unnamed = new Schemas();
        await DecideAsync(Gate(Flow(), unnamed), State(context: "{\"mapping\":\"Thing@1.0.0\"}"), ValidRecord());
        await DecideAsync(Gate(Flow(), unnamed), State(context: "not json"), ValidRecord());
        Assert.All(unnamed.Asked, a => Assert.Null(a.Version));
    }

    [Fact]
    public async Task A_try_that_sends_the_payload_alone_checks_nothing_and_holds_nothing()
    {
        var schemas = new Schemas();
        var decision = await DecideAsync(Gate(Flow(ValidationMode.Enforce, UnverifiedAction.Hold), schemas), State(), Invalid(), writesMetadata: false);

        Assert.Null(decision.Hold);
        Assert.Equal(ValidationOutcome.NotValidated, decision.Verdict.Outcome);
        Assert.Contains("payload alone", decision.Verdict.Summary(), StringComparison.Ordinal);
        Assert.Empty(schemas.Asked);
    }

    [Fact]
    public async Task A_document_naming_no_kind_cannot_be_checked()
    {
        var decision = await DecideAsync(Gate(Flow(unverified: UnverifiedAction.Hold)), State(), Record(r => r.Remove("kind")));

        Assert.Equal(ValidationOutcome.Unverified, decision.Verdict.Outcome);
        Assert.Contains("names no kind", Assert.Single(decision.Verdict.Unverified).Message, StringComparison.Ordinal);
        Assert.NotNull(decision.Hold);
    }

    [Fact]
    public async Task Under_the_storage_check_a_record_whose_reference_storage_does_not_hold_is_held_whatever_the_mode()
    {
        var references = new ReferenceResolver(null, null, new Source().Answer, new Source("dev:master-data--Well:W-1").Answer);
        var dangling = State(references: [new RecordReference("dev:master-data--Wellbore:Gone", "data.WellboreID")]);

        var decision = await DecideAsync(Gate(Flow(ValidationMode.Report), references: references), dangling, ValidRecord());

        Assert.StartsWith("refers to dev:master-data--Wellbore:Gone (data.WellboreID), which neither the ledger nor OSDU's storage service holds", decision.Hold, StringComparison.Ordinal);

        // The verdict counts what storage said of the ids the document holds at its relationships.
        Assert.Equal(1, decision.Verdict.References.InOsdu);
        Assert.Equal(2, decision.Verdict.References.Missing);
    }

    [Fact]
    public async Task Under_the_storage_check_a_record_whose_references_all_exist_goes()
    {
        var holds = new[] { "dev:master-data--Well:W-1", "dev:reference-data--MeasurementType:KB", "dev:master-data--Field:F-1" };
        var references = new ReferenceResolver(null, null, new Source().Answer, new Source(holds).Answer);

        var decision = await DecideAsync(Gate(Flow(ValidationMode.Enforce), references: references), State(), ValidRecord());

        Assert.Null(decision.Hold);
        Assert.Equal(ValidationOutcome.Valid, decision.Verdict.Outcome);
        Assert.Equal(3, decision.Verdict.References.InOsdu);
    }

    [Theory]
    [InlineData(ValidationMode.Report, false)]
    [InlineData(ValidationMode.Enforce, true)]
    public async Task A_reference_to_reference_data_the_cache_captures_and_does_not_hold_is_a_problem_held_only_under_enforce(ValidationMode mode, bool holds)
    {
        var cache = new ReferenceSnapshot("v7", DateTimeOffset.UnixEpoch,
        [
            new ReferenceType("MeasurementType", "reference-data--MeasurementType", [ReferenceItem.FromText("dev:reference-data--MeasurementType:DF", new Dictionary<string, string>())]),
        ]);
        var references = new ReferenceResolver(cache, "version v7 of the cache of partition 'dev'", new Source().Answer, osdu: null);

        var decision = await DecideAsync(Gate(Flow(mode), references: references), State(), ValidRecord());

        Assert.Equal(ValidationOutcome.Invalid, decision.Verdict.Outcome);
        var problem = Assert.Single(decision.Verdict.Problems);
        Assert.Equal("reference", problem.Rule);
        Assert.Contains("version v7 of the cache of partition 'dev' holds no such reference-data--MeasurementType record", problem.Message, StringComparison.Ordinal);
        Assert.Equal(holds, decision.Hold is not null);
    }

    [Fact]
    public async Task References_that_cannot_be_looked_up_fail_the_group_rather_than_send_it_unchecked()
    {
        var references = new ReferenceResolver(null, null, (_, _) => throw new TimeoutException("the ledger did not answer"), null);
        var gate = Gate(Flow(), references: references);

        await Assert.ThrowsAsync<TimeoutException>(() => gate.DecideAsync([(State(), ValidRecord(), true, null)]));
    }

    [Fact]
    public async Task A_group_asks_each_reference_source_once_and_its_decisions_keep_the_group_s_order()
    {
        var ledger = new Source();
        var gate = Gate(Flow(ValidationMode.Enforce), references: new ReferenceResolver(null, null, ledger.Answer, null));

        var decisions = await gate.DecideAsync(
        [
            (State("T-1"), ValidRecord(), true, null),
            (State("T-2"), Invalid(), true, null),
            (State("T-3"), ValidRecord(), false, null),
            (State("T-4"), Record(r => DataOf(r)["WellID"] = "dev:master-data--Well:W-2:"), true, null),
        ]);

        Assert.Equal(1, ledger.Calls);
        Assert.Equal(
            [ValidationOutcome.Valid, ValidationOutcome.Invalid, ValidationOutcome.NotValidated, ValidationOutcome.Valid],
            decisions.Select(d => d.Verdict.Outcome));
        Assert.Equal([false, true, false, false], decisions.Select(d => d.Hold is not null));
    }

    [Theory]
    [InlineData(DeliveryProtocol.File, true)]
    [InlineData(DeliveryProtocol.Manifest, true)]
    [InlineData(DeliveryProtocol.FileAndDdms, true)]
    [InlineData(DeliveryProtocol.ManifestAndDdms, true)]
    [InlineData(DeliveryProtocol.Dataset, true)]
    [InlineData(DeliveryProtocol.Workflow, true)]
    [InlineData(DeliveryProtocol.Storage, false)]
    [InlineData(DeliveryProtocol.Ddms, false)]
    public async Task The_dataset_list_a_route_fills_when_it_sends_the_record_is_not_judged_as_rendered(DeliveryProtocol protocol, bool filled)
    {
        // As queued, before the route has registered the files: placeholders the File service's ids replace.
        var document = Record(r => DataOf(r)["Datasets"] = new JsonArray("<the File service's id>"));

        var decision = await DecideAsync(Gate(Flow(ValidationMode.Enforce, protocol: protocol)), State(), document);

        Assert.Equal(filled, decision.Verdict.Outcome == ValidationOutcome.Valid);
        Assert.Equal(filled, ValidationGate.RouteFilled(Flow(protocol: protocol), document).Contains("data.Datasets"));
    }

    [Fact]
    public void What_an_update_carries_forward_and_the_bulk_link_a_DDMS_manages_are_filled_by_the_route()
    {
        var flow = Flow(protocol: DeliveryProtocol.Ddms);
        flow = flow with { Target = flow.Target with { ProtocolOptions = flow.Target.ProtocolOptions with { PreserveDataKeys = ["Status"] } } };

        var filled = ValidationGate.RouteFilled(flow, ValidRecord());

        Assert.Contains("data.Status", filled);
        Assert.Contains("data.ExtensionProperties.wdms", filled);
        Assert.DoesNotContain("data.Datasets", filled);
    }
    private static AssertionFindings Findings(AssertionAction action, string value = "312", string label = "count-range")
    {
        var log = new AssertionLog("Thing@1.0.0");
        log.Judged();
        var assertion = new NodeAssertion
        {
            Label = label,
            Named = true,
            Location = "record.data.Count.$assert[0]",
            Condition = new ValueCondition { Operator = ValueOperator.Between },
            OnFail = action,
        };
        log.Fail("data.Count", "data.Count", assertion, action, value, $"is {value}, outside 0 to 250");
        return log.Findings();
    }

    [Fact]
    public async Task A_failure_whose_action_is_hold_holds_a_document_the_schema_accepts_and_names_the_assertion()
    {
        var decision = await DecideAsync(Gate(Flow()), State(), ValidRecord(), assertions: Findings(AssertionAction.Hold));

        Assert.StartsWith(
            "assertion: the record fails what its mapping Thing@1.0.0 asserts: data.Count fails \"count-range\" with '312'. Its mapping holds a record that fails them (onFail: hold), so it is held;",
            decision.Hold,
            StringComparison.Ordinal);
        Assert.Equal(ValidationOutcome.Valid, decision.Verdict.Outcome);
        Assert.Equal(1, decision.Verdict.Assertions!.Held);
        Assert.Contains("1 of 1 assertion judgement(s) of Thing@1.0.0 failed (1 holding)", decision.Verdict.Summary(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reported_or_omitted_failure_is_sent_with_its_verdict()
    {
        foreach (var action in new[] { AssertionAction.Report, AssertionAction.Omit })
        {
            var decision = await DecideAsync(Gate(Flow()), State(), ValidRecord(), assertions: Findings(action));
            Assert.Null(decision.Hold);
            Assert.Equal(1, decision.Verdict.Assertions!.Failed);
        }
    }

    [Fact]
    public async Task A_release_accepts_a_document_its_assertions_held()
    {
        var accepted = await DecideAsync(Gate(Flow()), State(accepted: "hash-of-this-document"), ValidRecord(), assertions: Findings(AssertionAction.Hold));
        Assert.Null(accepted.Hold);
        Assert.True(accepted.Verdict.Accepted);

        var other = await DecideAsync(Gate(Flow()), State(accepted: "hash-of-an-earlier-document"), ValidRecord(), assertions: Findings(AssertionAction.Hold));
        Assert.NotNull(other.Hold);
    }

    [Fact]
    public async Task A_record_held_for_its_assertions_and_its_schema_says_both()
    {
        var decision = await DecideAsync(Gate(Flow(ValidationMode.Enforce)), State(), Invalid(), assertions: Findings(AssertionAction.Hold));

        Assert.StartsWith("assertion: ", decision.Hold, StringComparison.Ordinal);
        Assert.Contains("It does not meet the schema of", decision.Hold, StringComparison.Ordinal);
        Assert.Contains("which validation.mode is enforce holds as well.", decision.Hold, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_try_that_sends_the_payload_alone_judges_no_assertion()
    {
        var decision = await DecideAsync(Gate(Flow()), State(), ValidRecord(), writesMetadata: false, assertions: Findings(AssertionAction.Hold));

        Assert.Null(decision.Hold);
        Assert.Equal(ValidationOutcome.NotValidated, decision.Verdict.Outcome);
        Assert.Null(decision.Verdict.Assertions);
    }

    [Fact]
    public async Task The_records_one_assertion_holds_are_one_issue_whatever_values_they_hold()
    {
        var first = (await DecideAsync(Gate(Flow()), State(), ValidRecord(), assertions: Findings(AssertionAction.Hold, "312"))).Hold!;
        var second = (await DecideAsync(Gate(Flow()), State(), ValidRecord(), assertions: Findings(AssertionAction.Hold, "N'A 999"))).Hold!;
        var other = (await DecideAsync(Gate(Flow()), State(), ValidRecord(), assertions: Findings(AssertionAction.Hold, "312", "count-positive"))).Hold!;

        Assert.Equal(ProblemSignature.Of(first), ProblemSignature.Of(second));
        Assert.NotEqual(ProblemSignature.Of(first), ProblemSignature.Of(other));
    }

    [Fact]
    public async Task A_verdict_carries_its_assertions_through_its_json_and_stays_within_its_bound()
    {
        var decision = await DecideAsync(Gate(Flow()), State(), ValidRecord(), assertions: Findings(AssertionAction.Report));
        var read = ValidationVerdict.FromJson(JsonNode.Parse(decision.Verdict.ToJson().ToJsonString()))!;
        Assert.Equal(decision.Verdict.Assertions!.Failures, read.Assertions!.Failures);
        Assert.Equal(1, read.Assertions.Reported);

        // A verdict whose failures carry long values is shortened, every count kept.
        var log = new AssertionLog("Thing@1.0.0");
        var assertion = new NodeAssertion { Label = "long", Location = "record.data.Name.$assert[0]", Condition = new ValueCondition { Operator = ValueOperator.Exists } };
        for (var i = 0; i < 50; i++)
        {
            log.Judged();
            log.Fail("data.Name", $"data.Name[{i}]", assertion, AssertionAction.Report, new string('x', 200), new string('y', 500));
        }

        var big = decision.Verdict with { Assertions = log.Findings() };
        var json = big.ToJson();
        Assert.True(json.ToJsonString().Length <= ValidationVerdict.MaxJsonChars);
        Assert.True(json["shortened"]!.GetValue<bool>());
        Assert.Equal(50, ValidationVerdict.FromJson(json)!.Assertions!.Failed);
    }
}
