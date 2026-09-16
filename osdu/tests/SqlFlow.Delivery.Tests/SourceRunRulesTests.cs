using System.Net.Sockets;
using SqlFlow.Core;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The rules a run of a source follows (docs/interfaces-design.md sections 6 and 8): the waves its interfaces run in,
/// when record failures stop an interface, and how a run names the interfaces it works on.
/// </summary>
public sealed class SourceRunRulesTests
{
    private static InterfaceDependency After(string name, string dependsOn) => new(name, dependsOn, "test");

    [Fact]
    public void Interfaces_run_in_waves_of_their_dependencies_in_document_order_within_a_wave()
    {
        // fields <- wells <- wellbores <- (logs, trajectories); documents waits for nothing.
        var names = new[] { "logs", "documents", "wellbores", "trajectories", "wells", "fields" };
        var waves = InterfaceOrder.Waves(names, [After("logs", "wellbores"), After("trajectories", "Wellbores"), After("wellbores", "wells"), After("wells", "fields")]);
        Assert.Equal(
            [["documents", "fields"], ["wells"], ["wellbores"], ["logs", "trajectories"]],
            waves.Select(w => w.ToArray()).ToArray());
    }

    [Fact]
    public void A_diamond_waits_for_both_sides_and_a_dependency_outside_the_run_is_not_waited_for()
    {
        var names = new[] { "top", "left", "right", "bottom" };
        var dependencies = new[] { After("left", "top"), After("right", "top"), After("bottom", "left"), After("bottom", "right") };
        Assert.Equal([["top"], ["left", "right"], ["bottom"]], InterfaceOrder.Waves(names, dependencies).Select(w => w.ToArray()).ToArray());

        // A run of some interfaces reads what the others delivered as it is: their order does not hold this run up.
        Assert.Equal([["left", "bottom"]], InterfaceOrder.Waves(["left", "bottom"], [After("left", "top")]).Select(w => w.ToArray()).ToArray());
        Assert.Equal([["left"], ["bottom"]], InterfaceOrder.Waves(["left", "bottom"], dependencies).Select(w => w.ToArray()).ToArray());
    }

    [Fact]
    public void Interfaces_waiting_for_each_other_are_named_around_the_cycle()
    {
        var names = new[] { "a", "b", "c", "d" };
        var dependencies = new[] { After("a", "d"), After("b", "a"), After("c", "b"), After("d", "c"), After("c", "a") };
        Assert.Equal(["a", "d", "c", "b", "a"], InterfaceOrder.Cycle(names, dependencies));
        var refused = Assert.Throws<DeliveryException>(() => InterfaceOrder.Waves(names, dependencies));
        Assert.Contains("a -> d -> c -> b -> a wait for each other", refused.Message, StringComparison.Ordinal);

        Assert.Null(InterfaceOrder.Cycle(names, [After("b", "a"), After("c", "b")]));

        // A long chain is walked without recursion.
        var chain = Enumerable.Range(0, 2000).Select(i => $"i{i}").ToList();
        var links = chain.Skip(1).Select((name, i) => After(name, chain[i])).ToList();
        Assert.Null(InterfaceOrder.Cycle(chain, links));
        Assert.Equal(2000, InterfaceOrder.Waves(chain, links).Count);
    }

    [Fact]
    public void An_outage_stops_an_interface_and_a_delivery_in_between_ends_the_run_of_failures()
    {
        using var guard = new FailureGuard(new FlowFailWhen { OutageFailures = 3 });
        guard.Failed(FailureClass.Connection, settled: false, "HTTP 503");
        guard.Failed(FailureClass.Connection, settled: false, "HTTP 503");
        guard.Succeeded();
        guard.Failed(FailureClass.Connection, settled: false, "HTTP 503");
        guard.Failed(FailureClass.Data, settled: true, "HTTP 400");
        guard.Failed(FailureClass.Connection, settled: false, "HTTP 503");
        Assert.False(guard.Tripped);
        Assert.False(guard.Token.IsCancellationRequested);

        // Three connection failures with no delivery in between, whatever data failures come among them.
        guard.Failed(FailureClass.Connection, settled: true, "HTTP 502 from POST /records");
        Assert.True(guard.Tripped);
        Assert.True(guard.Token.IsCancellationRequested);
        Assert.Contains("an outage: 3 records in a row could not reach the service", guard.Reason, StringComparison.Ordinal);
        Assert.Contains("the last: HTTP 502 from POST /records", guard.Reason, StringComparison.Ordinal);

        // What tripped it stays the reason.
        guard.Failed(FailureClass.Permission, settled: true, "HTTP 403");
        Assert.Contains("could not reach the service", guard.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Refused_credentials_are_an_outage_and_the_rule_can_be_turned_off()
    {
        using var permission = new FailureGuard(new FlowFailWhen { OutageFailures = 2 });
        permission.Failed(FailureClass.Permission, settled: true, null);
        permission.Failed(FailureClass.Permission, settled: true, null);
        Assert.Contains("were refused by the service (401 or 403)", permission.Reason, StringComparison.Ordinal);

        using var off = new FailureGuard(new FlowFailWhen { OutageFailures = 0 });
        for (var i = 0; i < 1000; i++)
        {
            off.Failed(FailureClass.Connection, settled: false, null);
        }

        Assert.False(off.Tripped);

        // Data failures never count as an outage.
        using var data = new FailureGuard(new FlowFailWhen { OutageFailures = 2 });
        for (var i = 0; i < 100; i++)
        {
            data.Failed(FailureClass.Data, settled: true, null);
        }

        Assert.False(data.Tripped);
    }

    [Fact]
    public void Consecutive_failures_of_one_class_stop_an_interface_when_the_document_asks()
    {
        using var guard = new FailureGuard(new FlowFailWhen { ConsecutiveFailures = 3, OutageFailures = 0 });
        guard.Failed(FailureClass.Data, settled: true, "held");
        guard.Failed(FailureClass.Data, settled: true, "held");
        guard.Failed(FailureClass.Connection, settled: false, "503");
        Assert.False(guard.Tripped);
        guard.Failed(FailureClass.Data, settled: true, "HTTP 400: kind unknown");
        Assert.Contains("3 records in a row failed with data problems and none delivered in between (failWhen.consecutiveFailures 3)", guard.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_share_is_judged_once_enough_records_settled()
    {
        using var guard = new FailureGuard(new FlowFailWhen { FailedPercent = 50, MinRecords = 10, OutageFailures = 0 });
        for (var i = 0; i < 4; i++)
        {
            guard.Failed(FailureClass.Data, settled: true, "held");
        }

        // Retries are not settled: they count toward no share.
        for (var i = 0; i < 20; i++)
        {
            guard.Failed(FailureClass.Connection, settled: false, "503");
        }

        guard.Succeeded(5);
        Assert.False(guard.Tripped);
        guard.Failed(FailureClass.Data, settled: true, "held");
        Assert.True(guard.Tripped);
        Assert.Contains("5 of the 10 records the run delivered so far were held or failed (50%), at or above failWhen.failedPercent 50", guard.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_planning_that_holds_too_many_records_stops_the_interface_before_it_sends_anything()
    {
        using var below = new FailureGuard(new FlowFailWhen { FailedPercent = 20, MinRecords = 10 });
        below.Planned(held: 19, planned: 81);
        Assert.False(below.Tripped);

        using var small = new FailureGuard(new FlowFailWhen { FailedPercent = 20, MinRecords = 10 });
        small.Planned(held: 9, planned: 0);
        Assert.False(small.Tripped);

        using var above = new FailureGuard(new FlowFailWhen { FailedPercent = 20, MinRecords = 10 });
        above.Planned(held: 20, planned: 80);
        Assert.True(above.Token.IsCancellationRequested);
        Assert.Contains("20 of the 100 records the run planned were held before anything was sent (20%)", above.Reason, StringComparison.Ordinal);

        // Without a share rule, the planning never stops the interface.
        using var none = new FailureGuard(new FlowFailWhen());
        none.Planned(held: 1000, planned: 0);
        Assert.False(none.Tripped);
    }

    [Fact]
    public void A_failure_is_classed_by_what_it_wraps()
    {
        Assert.Equal(FailureClass.Permission, FailureGuard.Classify(new OsduStatusException(401, "unauthorised")));
        Assert.Equal(FailureClass.Permission, FailureGuard.Classify(new DeliveryException("wrapped", new OsduStatusException(403, "forbidden"))));
        Assert.Equal(FailureClass.Connection, FailureGuard.Classify(new OsduStatusException(503, "unavailable")));
        Assert.Equal(FailureClass.Connection, FailureGuard.Classify(new OsduStatusException(429, "throttled")));
        Assert.Equal(FailureClass.Connection, FailureGuard.Classify(new OsduStatusException(408, "timeout")));
        Assert.Equal(FailureClass.Data, FailureGuard.Classify(new OsduStatusException(400, "bad kind")));
        Assert.Equal(FailureClass.Connection, FailureGuard.Classify(new DeliveryException("HTTP transport failure", new HttpRequestException("refused", new SocketException()))));
        Assert.Equal(FailureClass.Connection, FailureGuard.Classify(new DeliveryException("timed out", new TaskCanceledException())));
        Assert.Equal(FailureClass.Connection, FailureGuard.Classify(new IOException("reset")));
        Assert.Equal(FailureClass.Data, FailureGuard.Classify(new RecordHeldException("the payload is too wide")));
        Assert.Equal(FailureClass.Data, FailureGuard.Classify(null));
    }

    [Fact]
    public void A_run_names_one_interface_or_selects_several_and_carries_them_through_its_payload()
    {
        var one = new DeliveryRunPayload { Interface = "wells", SubmissionId = Guid.NewGuid(), Slices = [1, 2] };
        var parsed = DeliveryRunPayload.Parse(one.ToJson());
        Assert.Equal("wells", parsed.Interface);
        parsed.Validate(DeliveryOperations.Intake);

        var several = DeliveryRunPayload.Parse("""{"interfaces":["wells","logs"],"force":true}""");
        Assert.Equal(["wells", "logs"], several.Interfaces);
        several.Validate(DeliveryOperations.Deliver);
        Assert.Equal("""{"force":true,"interfaces":["wells","logs"]}""", several.ToJson());
        Assert.False(several.IsEmpty);

        static string Refused(string json, string operation = DeliveryOperations.Deliver)
            => Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse(json).Validate(operation)).Message;

        Assert.Contains("payload names both an interface and interfaces", Refused("""{"interface":"wells","interfaces":["logs"]}"""), StringComparison.Ordinal);
        Assert.Contains("a run on a submission, on records or on slices works on one interface", Refused($$"""{"interfaces":["logs"],"submissionId":"{{Guid.NewGuid()}}"}""", DeliveryOperations.Drain), StringComparison.Ordinal);
        Assert.Contains("payload interfaces names an interface more than once", Refused("""{"interfaces":["logs","LOGS"]}"""), StringComparison.Ordinal);
        Assert.Contains("payload interface 'we lls' is not an interface name", Refused("""{"interface":"we lls"}"""), StringComparison.Ordinal);
        Assert.Contains("payload interfaces holds '9', which is not an interface name", Refused("""{"interfaces":["9"]}"""), StringComparison.Ordinal);
        Assert.Contains("payload interfaces must be a non-empty string", Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse("""{"interfaces":[1]}""")).Message, StringComparison.Ordinal);
        Assert.Contains("payload interfaces must be an array", Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse("""{"interfaces":"wells"}""")).Message, StringComparison.Ordinal);
    }
}
