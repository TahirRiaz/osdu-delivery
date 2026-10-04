using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What a flow says the gate does with a verdict (<c>target.validation</c>, docs/validation-plan.md): read from a flow
/// document, and from a source document for every interface with each interface's own keys laid over it; refused when it
/// names a value that is not one; and which verdicts hold a record under each setting.
/// </summary>
public sealed class ValidationPolicyTests
{
    private readonly DeliveryDocumentLoader _loader = new();

    private static string Flow(string validation) => ($$"""
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
        {{validation}}
        """).ReplaceLineEndings("\n");

    private static string Source(string validation, string wellsValidation = "", string logsValidation = "") => ($$"""
        flowType: delivery
        name: petrel
        source:
          connection: ${env:PETREL_DB}
          work: ../.work
        render:
          parameters:
            dataPartition: dev
        target:
          endpoint: https://osdu.example.com
          headers:
            data-partition-id: dev
        {{validation}}
        interfaces:
          wells:
            record: { object: Petrel.ing.Well, key: [uwi] }
            mapping: Well@1.2.0
        {{wellsValidation}}
          logs:
            record: { object: Petrel.ing.WellLog, key: [uwi, log_id] }
            mapping: WellLog@1.4.0
        {{logsValidation}}
        """).ReplaceLineEndings("\n");

    [Fact]
    public void A_flow_that_says_nothing_reports_and_sends_what_it_could_not_check()
    {
        var policy = _loader.ParseFlow(Flow(string.Empty), "flow.yaml").Target.Validation;

        Assert.Equal(ValidationPolicy.Default, policy);
        Assert.Equal(ValidationMode.Report, policy.Mode);
        Assert.Equal(UnverifiedAction.Send, policy.Unverified);
    }

    [Theory]
    [InlineData("  validation: { mode: enforce }", ValidationMode.Enforce, UnverifiedAction.Send)]
    [InlineData("  validation: { mode: report, unverified: hold }", ValidationMode.Report, UnverifiedAction.Hold)]
    [InlineData("  validation: { mode: Enforce, unverified: Hold }", ValidationMode.Enforce, UnverifiedAction.Hold)]
    [InlineData("  validation: {}", ValidationMode.Report, UnverifiedAction.Send)]
    public void A_flow_says_what_the_gate_does_with_a_verdict(string block, ValidationMode mode, UnverifiedAction unverified)
    {
        var policy = _loader.ParseFlow(Flow(block), "flow.yaml").Target.Validation;

        Assert.Equal(mode, policy.Mode);
        Assert.Equal(unverified, policy.Unverified);
    }

    [Theory]
    [InlineData("  validation: { mode: strict }", "'target.validation.mode' value 'strict' is not one of report, enforce")]
    [InlineData("  validation: { unverified: drop }", "'target.validation.unverified' value 'drop' is not one of send, hold")]
    [InlineData("  validation: { mode: enforce, onFail: hold }", "onFail")]
    public void A_setting_that_is_not_one_is_refused_naming_the_key(string block, string expected)
    {
        var refused = Assert.ThrowsAny<SqlFlowException>(() => _loader.ParseFlow(Flow(block), "flow.yaml"));
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_source_s_setting_applies_to_every_interface_and_an_interface_lays_its_own_keys_over_it()
    {
        var source = _loader.ParseSource(
            Source(
                "  validation: { mode: enforce }",
                logsValidation: "    validation: { mode: report, unverified: hold }"),
            "petrel.yaml");

        var wells = source.Interface("wells").Target.Validation;
        Assert.Equal(new ValidationPolicy { Mode = ValidationMode.Enforce, Unverified = UnverifiedAction.Send }, wells);

        var logs = source.Interface("logs").Target.Validation;
        Assert.Equal(new ValidationPolicy { Mode = ValidationMode.Report, Unverified = UnverifiedAction.Hold }, logs);
    }

    [Fact]
    public void An_interface_may_set_what_its_source_leaves_out_and_a_key_it_leaves_out_is_the_source_s()
    {
        var source = _loader.ParseSource(
            Source("  validation: { unverified: hold }", wellsValidation: "    validation: { mode: enforce }"),
            "petrel.yaml");

        Assert.Equal(new ValidationPolicy { Mode = ValidationMode.Enforce, Unverified = UnverifiedAction.Hold }, source.Interface("wells").Target.Validation);
        Assert.Equal(new ValidationPolicy { Mode = ValidationMode.Report, Unverified = UnverifiedAction.Hold }, source.Interface("logs").Target.Validation);
    }

    [Fact]
    public void A_bad_value_an_interface_takes_from_its_source_names_the_source_s_key_and_the_interface()
    {
        var refused = Assert.ThrowsAny<SqlFlowException>(() => _loader.ParseSource(Source("  validation: { mode: lenient }"), "petrel.yaml"));
        Assert.Contains("'target.validation.mode of interface 'wells'' value 'lenient'", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ValidationMode.Report, UnverifiedAction.Send, ValidationOutcome.Invalid, false)]
    [InlineData(ValidationMode.Report, UnverifiedAction.Send, ValidationOutcome.Unverified, false)]
    [InlineData(ValidationMode.Enforce, UnverifiedAction.Send, ValidationOutcome.Invalid, true)]
    [InlineData(ValidationMode.Enforce, UnverifiedAction.Send, ValidationOutcome.Unverified, false)]
    [InlineData(ValidationMode.Report, UnverifiedAction.Hold, ValidationOutcome.Invalid, false)]
    [InlineData(ValidationMode.Report, UnverifiedAction.Hold, ValidationOutcome.Unverified, true)]
    [InlineData(ValidationMode.Enforce, UnverifiedAction.Hold, ValidationOutcome.Valid, false)]
    [InlineData(ValidationMode.Enforce, UnverifiedAction.Hold, ValidationOutcome.NotValidated, false)]
    public void Which_verdicts_hold_a_record_under_each_setting(ValidationMode mode, UnverifiedAction unverified, ValidationOutcome outcome, bool holds)
        => Assert.Equal(holds, new ValidationPolicy { Mode = mode, Unverified = unverified }.Holds(outcome));

    [Fact]
    public void A_hold_names_the_setting_that_made_it_as_the_flow_writes_it()
    {
        Assert.Equal("validation.mode is enforce", ValidationPolicy.Setting(ValidationOutcome.Invalid));
        Assert.Equal("validation.unverified is hold", ValidationPolicy.Setting(ValidationOutcome.Unverified));
    }
}
