using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A payload sent in parts (docs/interfaces-design.md section 5.5), as the ledger keeps it in the payload hash and
/// location it has always kept: the hash of the parts' hashes, the parts listed with their folders, and a redelivery of a
/// part left on the delivered hash until the payload lands again.
/// </summary>
public sealed class PayloadPartsTests
{
    private static CompositePayloadPart Files(string hash = "f1") => new(PayloadParts.Files, "files", "C:/data/files/rec-1/*", hash);

    private static CompositePayloadPart Bulk(string hash = "b1") => new(PayloadParts.Bulk, "bulk", "C:/data/bulk/rec-1/*.parquet", hash);

    [Fact]
    public void The_payload_hash_moves_with_any_part_and_only_with_a_part()
    {
        var hash = CompositePayload.HashOf([Files(), Bulk()]);
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(hash, CompositePayload.HashOf([Files(), Bulk()]));
        Assert.NotEqual(hash, CompositePayload.HashOf([Files("f2"), Bulk()]));
        Assert.NotEqual(hash, CompositePayload.HashOf([Files(), Bulk("b2")]));

        // The folder a part is read from is where it is, not what it holds.
        Assert.Equal(hash, CompositePayload.HashOf([Files() with { Location = "C:/moved/*" }, Bulk()]));

        // A route with no parts (a workflow that reads nothing) still has a payload hash, and it never moves.
        Assert.Equal(CompositePayload.HashOf([]), CompositePayload.HashOf([]));
    }

    [Fact]
    public void A_payload_in_parts_is_kept_as_json_and_read_back_whole()
    {
        var payload = new CompositePayload(
            [Files(), Bulk(), new CompositePayloadPart(PayloadParts.Files, "h5", null, CompositePayload.NoFiles)],
            new HashSet<string>(StringComparer.Ordinal) { PayloadParts.Bulk });
        var stored = payload.Encode();

        Assert.True(CompositePayload.IsComposite(stored));
        Assert.False(CompositePayload.IsComposite("C:/data/files/rec-1/*"));
        Assert.False(CompositePayload.IsComposite("abfss://landing@acct.dfs.core.windows.net/files/rec-1/*.las"));
        Assert.Null(CompositePayload.TooLong(stored));

        var read = CompositePayload.Decode(stored);
        Assert.Equal(payload.Parts, read.Parts);
        Assert.Equal([PayloadParts.Bulk], read.Forced);
        Assert.Null(read.Parts[2].Location);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"composite":"2","parts":[]}""")]
    [InlineData("""{"composite":"1"}""")]
    [InlineData("""{"composite":"1","parts":[{"role":"files","hash":"h"}]}""")]
    public void A_stored_payload_this_version_did_not_write_is_refused(string stored)
    {
        var refused = Assert.Throws<DeliveryException>(() => CompositePayload.Decode(stored));
        Assert.Contains("redeliver", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_payload_too_long_for_the_ledger_is_named()
    {
        var folder = "C:/" + new string('a', 1200);
        var payload = new CompositePayload([Files() with { Location = folder }, Bulk() with { Location = folder }], new HashSet<string>());
        var problem = CompositePayload.TooLong(payload.Encode());
        Assert.NotNull(problem);
        Assert.Contains("the ledger keeps 2000", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_redelivery_of_a_part_is_kept_on_the_delivered_hash_until_the_payload_lands()
    {
        Assert.Equal("redeliver:bulk,files", PayloadParts.RedeliverMarker([PayloadParts.Files, PayloadParts.Bulk, PayloadParts.Files]));
        Assert.Throws<ArgumentException>(() => PayloadParts.RedeliverMarker([]));
        Assert.Throws<ArgumentException>(() => PayloadParts.RedeliverMarker(["record"]));
        Assert.True(PayloadParts.RedeliverRoles("redeliver:workflow")!.SetEquals(["workflow"]));
        Assert.Null(PayloadParts.RedeliverRoles("a3f0"));
        Assert.Null(PayloadParts.RedeliverRoles(null));

        IReadOnlyList<string> roles = [PayloadParts.Files, PayloadParts.Bulk];
        var change = new FlowChange();

        // Never delivered, or a whole payload asked for: every part goes.
        Assert.True(PayloadParts.Forced(null, change, roles).SetEquals(roles));

        // A part named by a redelivery goes whatever its hash says; the others go when their hash moved.
        Assert.Equal([PayloadParts.Bulk], PayloadParts.Forced("redeliver:bulk", change, roles));
        Assert.Empty(PayloadParts.Forced("a3f0", change, roles));

        // A flow that always sends its payload sends every part.
        Assert.Equal(2, PayloadParts.Forced("a3f0", new FlowChange { PayloadDetect = ChangeDetection.Always }, roles).Count);
        Assert.Equal(2, PayloadParts.Forced("a3f0", new FlowChange { OnUnchanged = UnchangedAction.Deliver }, roles).Count);
    }

    [Fact]
    public void A_delivery_sends_a_part_when_it_is_forced_or_its_hash_moved()
    {
        var files = new WorkPayloadPart(PayloadParts.Files, "files", null, "f2");
        var bulk = new WorkPayloadPart(PayloadParts.Bulk, "bulk", null, "b1");
        var work = new DeliveryWork
        {
            Key = SqlFlow.Delivery.Identity.DeliveryKey.Derive("parts", ["1"]),
            TargetId = "dev:work-product-component--WellLog:1",
            Document = new System.Text.Json.Nodes.JsonObject(),
            DeliverMetadata = false,
            DeliverPayload = true,
            Parts = [files, bulk],
            TargetState = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PayloadParts.StateKey("files")] = "f1",
                [PayloadParts.StateKey("bulk")] = "b1",
            },
        };

        Assert.True(work.Sends(files));
        Assert.False(work.Sends(bulk));
        Assert.True((work with { ForcedParts = new HashSet<string> { PayloadParts.Bulk } }).Sends(bulk));
        Assert.False((work with { DeliverPayload = false }).Sends(files));
        Assert.True((work with { TargetState = new Dictionary<string, string>() }).Sends(bulk));
        Assert.True((work with { ForcedParts = new HashSet<string> { PayloadParts.Workflow } }).Forces(PayloadParts.Workflow));
        Assert.False(work.Forces(PayloadParts.Workflow));
    }
}
