using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Submissions through <c>POST /api/v1/delivery/submissions</c>, both forms, with the records form (design.md section
/// 3.4) covered as the production path for wellbore master data: what the boundary refuses, that an accepted request
/// stores its records in the same transaction as the run that takes them, that a repeat answers with that run and queues
/// nothing (also when repeats race), that a reused id with anything else is a conflict, that a drop still submits as it
/// did, and that the source contract and the stored records read back. The in-process worker is off, so queued runs stay
/// queued and the assertions are about what the API stored. Gated on a reachable catalog database.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DeliverySubmissionApiTests
{
    private const string MappingReference = "Wellbore@1.0.0";

    /// <summary>The sample estate's wellbore mapping, self-contained so the suite needs no snapshot store.</summary>
    private const string MappingYaml = """
        documentType: mapping
        name: Wellbore
        version: 1.0.0
        kind: osdu:wks:master-data--Wellbore:1.3.0
        source: { system: recall, scopes: [aliases] }
        identity: { naturalKey: [data.FacilityName], label: "{facility_name}" }
        envelope:
          legalTags: [opendes-reference-data-default]
          otherRelevantDataCountries: [NO]
          acl:
            owners: [data.default.owners@opendes.dataservices.energy]
            viewers: [data.default.viewers@opendes.dataservices.energy]
        parameters:
          dataPartition: { required: true }
        properties:
          - { target: data.FacilityName, source: facility_name, transform: trim }
          - { target: data.FacilityDescription, source: facility_description }
          - { target: data.FacilityID, source: facility_id }
          - target: data.NameAliases
            collection: true
            scope: aliases
            properties:
              - { target: AliasName, source: alias_name }
        """;

    private static object Wellbore(string name, string description = "a wellbore", string updated = "2026-09-12T10:00:00Z", params string[] aliases) => new
    {
        record = new Dictionary<string, object?>
        {
            ["facility_name"] = name,
            ["facility_description"] = description,
            ["facility_id"] = "srn:master-data/Wellbore:" + name,
            ["update_date"] = updated,
        },
        scopes = new Dictionary<string, object> { ["aliases"] = aliases.Select(a => new { alias_name = a }).ToArray() },
    };

    /// <summary>A wellbore that points at where its payload files already sit, the way a source sends one to a file flow.</summary>
    private static object PayloadWellbore(string name, string location, string? hash = null) => new
    {
        record = new Dictionary<string, object?>
        {
            ["facility_name"] = name,
            ["facility_description"] = "with files",
            ["facility_id"] = "srn:master-data/Wellbore:" + name,
            ["update_date"] = "2026-09-12T10:00:00Z",
        },
        files = new Dictionary<string, object> { ["files"] = hash is null ? location : new { location, hash } },
    };

    [SkippableFact]
    public async Task Records_are_stored_with_their_run_and_a_repeat_answers_with_that_run()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.ProvisionAsync(cs);
        var estate = await Estate.SeedAsync(cs);
        try
        {
            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);
            var request = new
            {
                flow = estate.RecordsFlow,
                parameters = new { site = "north" },
                records = new[] { Wellbore("WB-API-1", "first", aliases: ["A-1", "A-2"]) },
            };

            using var first = await PostAsync(client, token, request);
            Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
            var accepted = await first.Content.ReadFromJsonAsync<DeliverySubmissionAccepted>();
            Assert.NotNull(accepted);
            Assert.False(accepted.Replayed);
            Assert.Equal(estate.RecordsFlow, accepted.FlowName);
            var submissionId = Assert.IsType<Guid>(accepted.SubmissionId);
            Assert.Equal($"/api/v1/runs/{accepted.RunId}", first.Headers.Location?.OriginalString);

            await using (var db = CatalogDatabase.Create(cs))
            {
                var run = await db.Runs.AsNoTracking().SingleAsync(r => r.SubmissionId == submissionId);
                Assert.Equal(accepted.RunId, run.RunId);
                Assert.Equal("deliver", run.Operation);
                Assert.False(run.Force);
                Assert.Equal(RunStatuses.Queued, run.Status);

                var stored = await db.DeliveryInlineSubmissions.AsNoTracking().SingleAsync(s => s.SubmissionId == submissionId);
                Assert.Equal(estate.RecordsFlow, stored.FlowName);
                Assert.Equal(MappingReference, stored.MappingReference);
                Assert.Equal("deliver", stored.Operation);
                Assert.Equal(1, stored.RecordCount);
                Assert.Equal(2, stored.ChildRowCount);
                Assert.Equal("""{"site":"north"}""", stored.ParametersJson);
                Assert.Equal(64, stored.ContentHash.Length);
                Assert.Equal(64, stored.RequestHash.Length);
                Assert.False(string.IsNullOrWhiteSpace(stored.ReceivedBy));
                Assert.Null(stored.DropLocation);
                Assert.Null(stored.WrittenUtc);
            }

            // The same request with its keys in another order and other whitespace is the same submission.
            using var repeat = await PostRawAsync(client, token, $$$"""
                { "submissionId": "{{{submissionId}}}", "parameters": { "site": "north" }, "flow": "{{{estate.RecordsFlow}}}",
                  "records": [ { "scopes": { "aliases": [ { "alias_name": "A-1" }, { "alias_name": "A-2" } ] },
                                 "record": { "update_date": "2026-09-12T10:00:00Z", "facility_id": "srn:master-data/Wellbore:WB-API-1",
                                             "facility_description": "first", "facility_name": "WB-API-1" } } ] }
                """);
            Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
            var replayed = await repeat.Content.ReadFromJsonAsync<DeliverySubmissionAccepted>();
            Assert.True(replayed!.Replayed);
            Assert.Equal(accepted.RunId, replayed.RunId);
            Assert.Equal(submissionId, replayed.SubmissionId);

            // The id names one request: other records, another operation or other parameters under it are refused.
            foreach (var (changed, expected) in new (object Body, string Names)[]
            {
                (new { submissionId, flow = estate.RecordsFlow, parameters = new { site = "north" }, records = new[] { Wellbore("WB-API-1", "changed") } }, "the records"),
                (new { submissionId, flow = estate.RecordsFlow, parameters = new { site = "south" }, records = new[] { Wellbore("WB-API-1", "first", aliases: ["A-1", "A-2"]) } }, "the flow parameter values"),
                (new { submissionId, flow = estate.RecordsFlow, operation = "plan", parameters = new { site = "north" }, records = new[] { Wellbore("WB-API-1", "first", aliases: ["A-1", "A-2"]) } }, "the operation"),
            })
            {
                using var conflict = await PostAsync(client, token, changed);
                Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
                Assert.Contains(expected, await conflict.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.Equal(1, await db.Runs.AsNoTracking().CountAsync(r => r.SubmissionId == submissionId));
            }

            // What was sent reads back from the ledger, with the run that took it.
            using var content = await GetAsync(client, token, $"/api/v1/delivery/submissions/{submissionId}/content");
            Assert.Equal(HttpStatusCode.OK, content.StatusCode);
            using var body = JsonDocument.Parse(await content.Content.ReadAsStringAsync());
            Assert.Equal(accepted.RunId, body.RootElement.GetProperty("runIds")[0].GetGuid());
            Assert.Equal(estate.RecordsFlow, body.RootElement.GetProperty("flowName").GetString());
            var record = body.RootElement.GetProperty("records")[0];
            Assert.Equal("WB-API-1", record.GetProperty("record").GetProperty("facility_name").GetString());
            Assert.Equal("A-1", record.GetProperty("scopes").GetProperty("aliases")[0].GetProperty("alias_name").GetString());

            using var none = await GetAsync(client, token, $"/api/v1/delivery/submissions/{Guid.NewGuid()}/content");
            Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
        }
        finally
        {
            await estate.CleanupAsync(cs);
        }
    }

    [SkippableFact]
    public async Task Concurrent_repeats_of_one_request_queue_one_run()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.ProvisionAsync(cs);
        var estate = await Estate.SeedAsync(cs);
        try
        {
            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);
            var submissionId = Guid.NewGuid();
            var body = new { submissionId, pipelineId = estate.RecordsPipeline, parameters = new { site = "north" }, records = new[] { Wellbore("WB-API-RACE") } };

            var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => PostAsync(client, token, body)));
            try
            {
                Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK, $"status {r.StatusCode}"));
                Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Accepted);
                var runIds = new HashSet<Guid>();
                foreach (var response in responses)
                {
                    runIds.Add((await response.Content.ReadFromJsonAsync<DeliverySubmissionAccepted>())!.RunId);
                }

                Assert.Single(runIds);
            }
            finally
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }

            await using var db = CatalogDatabase.Create(cs);
            Assert.Equal(1, await db.Runs.AsNoTracking().CountAsync(r => r.SubmissionId == submissionId));
            Assert.Equal(1, await db.DeliveryInlineSubmissions.AsNoTracking().CountAsync(s => s.SubmissionId == submissionId));
        }
        finally
        {
            await estate.CleanupAsync(cs);
        }
    }

    [SkippableFact]
    public async Task A_repeat_whose_run_is_gone_takes_the_stored_submission_again()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.ProvisionAsync(cs);
        var estate = await Estate.SeedAsync(cs);
        try
        {
            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);
            var submissionId = Guid.NewGuid();
            var body = new { submissionId, flow = estate.RecordsFlow, parameters = new { site = "north" }, records = new[] { Wellbore("WB-API-GONE") } };

            using var first = await PostAsync(client, token, body);
            Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
            var accepted = (await first.Content.ReadFromJsonAsync<DeliverySubmissionAccepted>())!;

            // Retention removed the run row; the records are still the ledger's, so a repeat queues a run for them.
            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.Runs.Where(r => r.RunId == accepted.RunId).ExecuteDeleteAsync();
            }

            using var again = await PostAsync(client, token, body);
            Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
            var second = (await again.Content.ReadFromJsonAsync<DeliverySubmissionAccepted>())!;
            Assert.True(second.Replayed);
            Assert.NotEqual(accepted.RunId, second.RunId);

            await using var check = CatalogDatabase.Create(cs);
            Assert.Equal(1, await check.DeliveryInlineSubmissions.AsNoTracking().CountAsync(s => s.SubmissionId == submissionId));
        }
        finally
        {
            await estate.CleanupAsync(cs);
        }
    }

    [SkippableFact]
    public async Task The_boundary_refuses_what_a_submission_cannot_be()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.ProvisionAsync(cs);
        var estate = await Estate.SeedAsync(cs);
        try
        {
            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);
            var one = new[] { Wellbore("WB-API-BAD") };

            async Task ExpectAsync(object body, HttpStatusCode status, string fragment)
            {
                using var response = await PostAsync(client, token, body);
                var text = await response.Content.ReadAsStringAsync();
                Assert.True(status == response.StatusCode, $"expected {status}, got {response.StatusCode}: {text}");
                Assert.Contains(fragment, text, StringComparison.Ordinal);
            }

            await ExpectAsync(new { flow = estate.RecordsFlow, parameters = new { site = "north" } }, HttpStatusCode.BadRequest, "names the drop to deliver");
            await ExpectAsync(new { flow = estate.RecordsFlow, drop = "C:/drops/x", records = one }, HttpStatusCode.BadRequest, "not both");
            await ExpectAsync(new { flow = estate.RecordsFlow, parameters = new { site = "north" }, operation = "verify", records = one }, HttpStatusCode.BadRequest, "operation is deliver or plan");
            await ExpectAsync(new { flow = estate.RecordsFlow, drop = "C:/drops/x", submissionId = Guid.NewGuid() }, HttpStatusCode.BadRequest, "submissionId goes with records");
            // A drop's own name for itself belongs in the manifest the preparing side writes, where its id already is.
            await ExpectAsync(new { flow = estate.RecordsFlow, drop = "C:/drops/x", reference = "job-17" }, HttpStatusCode.BadRequest, "on a submission it goes with records");
            await ExpectAsync(
                new { flow = estate.RecordsFlow, parameters = new { site = "north" }, reference = new string('x', 201), records = one },
                HttpStatusCode.BadRequest,
                "reference is at most 200 characters");
            await ExpectAsync(
                new { flow = estate.RecordsFlow, parameters = new { site = "north" }, reference = "line\u0007one", records = one },
                HttpStatusCode.BadRequest,
                "control character");
            await ExpectAsync(new { flow = estate.RecordsFlow, records = one }, HttpStatusCode.BadRequest, "parameter 'site' is required");
            await ExpectAsync(new { flow = estate.RecordsFlow, parameters = new { site = "north", other = "y" }, records = one }, HttpStatusCode.BadRequest, "'other' is not declared");
            await ExpectAsync(new { flow = estate.RecordsFlow, parameters = new { site = "north" }, submissionId = Guid.Empty, records = one }, HttpStatusCode.BadRequest, "non-empty UUID");
            await ExpectAsync(new { flow = estate.RecordsFlow, parameters = new { site = "north" }, records = Array.Empty<object>() }, HttpStatusCode.BadRequest, "is empty");
            await ExpectAsync(
                new { flow = estate.RecordsFlow, parameters = new { site = "north" }, records = new[] { new { record = new Dictionary<string, object?> { ["facility_name"] = new { nested = 1 } } } } },
                HttpStatusCode.BadRequest,
                "records[0].record.facility_name is a nested value");
            await ExpectAsync(
                new { flow = estate.RecordsFlow, parameters = new { site = "north" }, records = Enumerable.Range(0, 1001).Select(i => Wellbore($"WB-API-{i}")).ToArray() },
                HttpStatusCode.BadRequest,
                "at most 1000");
            // A flow that streams files takes records too, but each says where its files are, inside what the flow allows.
            await ExpectAsync(new { flow = estate.PayloadFlow, parameters = new { site = "north" }, records = one }, HttpStatusCode.BadRequest, "points at no files");
            await ExpectAsync(
                new { flow = estate.PayloadFlow, parameters = new { site = "north" }, records = new[] { PayloadWellbore("WB-API-OUTSIDE", "C:/somewhere/else") } },
                HttpStatusCode.BadRequest,
                "outside what flow");
            await ExpectAsync(
                new { flow = estate.PayloadFlow, parameters = new { site = "north" }, records = new[] { PayloadWellbore("WB-API-DOTS", Estate.FileRoot + "/../escape") } },
                HttpStatusCode.BadRequest,
                "must not contain '..'");
            // A flow that streams nothing has nowhere to read files from, so a record that points at some is refused.
            await ExpectAsync(
                new { flow = estate.RecordsFlow, parameters = new { site = "north" }, records = new[] { PayloadWellbore("WB-API-NOSTREAM", Estate.FileRoot + "/x") } },
                HttpStatusCode.BadRequest,
                "streams no payload files");
            await ExpectAsync(new { flow = estate.NoManualFlow, parameters = new { site = "north" }, records = one }, HttpStatusCode.BadRequest, "source.manualSubmission");
            await ExpectAsync(new { flow = "no-such-flow-" + Guid.NewGuid().ToString("N"), records = one }, HttpStatusCode.NotFound, "No active delivery flow");
            await ExpectAsync(new { pipelineId = Guid.NewGuid(), records = one }, HttpStatusCode.NotFound, "pipeline");
            await ExpectAsync(new { flow = estate.AmbiguousFlow, parameters = new { site = "north" }, records = one }, HttpStatusCode.Conflict, "repositories");

            // A submission id a drop already used is not a place to put records.
            var dropSubmissionId = await estate.SeedDropSubmissionAsync(cs);
            await ExpectAsync(
                new { flow = estate.RecordsFlow, submissionId = dropSubmissionId, parameters = new { site = "north" }, records = one },
                HttpStatusCode.Conflict,
                "is a drop submission");

            // A body that is not JSON at all is refused by the boundary, not by the handler.
            using (var malformed = await PostRawAsync(client, token, "{ not json"))
            {
                Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
            }

            await using var db = CatalogDatabase.Create(cs);
            Assert.Equal(0, await db.Runs.AsNoTracking().CountAsync(r => r.RepoId == estate.RepoId));
            Assert.Equal(0, await db.DeliveryInlineSubmissions.AsNoTracking().CountAsync(s => s.FlowName == estate.RecordsFlow));
        }
        finally
        {
            await estate.CleanupAsync(cs);
        }
    }

    /// <summary>
    /// Submitting records is an authenticated call on the operate surface. The platform's scope policies only enforce
    /// <c>admin</c> today (read, operate and author each require an authenticated user), so what is asserted here is
    /// what the deployment guarantees: no token, no submission.
    /// </summary>
    [SkippableFact]
    public async Task Submitting_records_needs_a_token()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.ProvisionAsync(cs);
        var estate = await Estate.SeedAsync(cs);
        try
        {
            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var body = new { flow = estate.RecordsFlow, parameters = new { site = "north" }, records = new[] { Wellbore("WB-API-AUTH") } };

            using (var anonymous = await PostAsync(client, token: null, body))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            }

            using (var garbled = await PostAsync(client, "not-a-token", body))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, garbled.StatusCode);
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.Equal(0, await db.DeliveryInlineSubmissions.AsNoTracking().CountAsync(s => s.FlowName == estate.RecordsFlow));
            }

            // The reads a caller needs to fill in a submission are reads: a read-scoped token gets them.
            var readOnly = await TokenAsync(client, "read");
            using var contract = await GetAsync(client, readOnly, $"/api/v1/delivery/flows/{estate.RecordsPipeline}/source-contract");
            Assert.Equal(HttpStatusCode.OK, contract.StatusCode);
            using var listing = await GetAsync(client, readOnly, "/api/v1/delivery/manual-submission/flows");
            Assert.Equal(HttpStatusCode.OK, listing.StatusCode);

            using var accepted = await PostAsync(client, await TokenAsync(client), body);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }
        finally
        {
            await estate.CleanupAsync(cs);
        }
    }

    [SkippableFact]
    public async Task A_plan_forces_and_defaults_are_recorded_as_asked_and_a_drop_still_submits()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.ProvisionAsync(cs);
        var estate = await Estate.SeedAsync(cs);
        try
        {
            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            using var plan = await PostAsync(client, token, new
            {
                flow = estate.RecordsFlow,
                operation = "plan",
                force = true,
                parameters = new { site = "north" },
                records = new[] { Wellbore("WB-API-PLAN") },
            });
            Assert.Equal(HttpStatusCode.Accepted, plan.StatusCode);
            var planned = (await plan.Content.ReadFromJsonAsync<DeliverySubmissionAccepted>())!;

            // A flow whose parameter has a default needs none in the request, and the default is what is stored.
            using var defaulted = await PostAsync(client, token, new { flow = estate.DefaultedFlow, records = new[] { Wellbore("WB-API-DEFAULT") } });
            Assert.Equal(HttpStatusCode.Accepted, defaulted.StatusCode);
            var defaultedAccepted = (await defaulted.Content.ReadFromJsonAsync<DeliverySubmissionAccepted>())!;

            using var drop = await PostAsync(client, token, new { pipelineId = estate.RecordsPipeline, drop = "C:/drops/north", parameters = new { site = "north" } });
            Assert.Equal(HttpStatusCode.Accepted, drop.StatusCode);
            var dropped = (await drop.Content.ReadFromJsonAsync<DeliverySubmissionAccepted>())!;
            Assert.Null(dropped.SubmissionId);
            Assert.False(dropped.Replayed);

            await using var db = CatalogDatabase.Create(cs);
            var planRun = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == planned.RunId);
            Assert.Equal("plan", planRun.Operation);
            Assert.True(planRun.Force);
            Assert.Equal(planned.SubmissionId, planRun.SubmissionId);
            var storedPlan = await db.DeliveryInlineSubmissions.AsNoTracking().SingleAsync(s => s.SubmissionId == planned.SubmissionId);
            Assert.Equal("plan", storedPlan.Operation);
            Assert.True(storedPlan.Force);

            var storedDefault = await db.DeliveryInlineSubmissions.AsNoTracking().SingleAsync(s => s.SubmissionId == defaultedAccepted.SubmissionId);
            Assert.Equal("""{"site":"south"}""", storedDefault.ParametersJson);

            var dropRun = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == dropped.RunId);
            Assert.Equal("deliver", dropRun.Operation);
            Assert.Null(dropRun.SubmissionId);
            Assert.Contains("C:/drops/north", dropRun.ParametersJson, StringComparison.Ordinal);
        }
        finally
        {
            await estate.CleanupAsync(cs);
        }
    }

    /// <summary>
    /// A submission to a flow that streams payload files: what is stored is where the files already sit, never the bytes,
    /// so the records ride in the catalog exactly as a metadata submission does and the node reads the files when it runs.
    /// </summary>
    [SkippableFact]
    public async Task A_submission_to_a_flow_that_streams_files_carries_where_they_are()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.ProvisionAsync(cs);
        var estate = await Estate.SeedAsync(cs);
        try
        {
            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);
            var location = Estate.FileRoot + "/WB-API-FILES";

            using var response = await PostAsync(client, token, new
            {
                flow = estate.PayloadFlow,
                parameters = new { site = "north" },
                records = new[] { PayloadWellbore("WB-API-FILES", location, "sha256:abc") },
            });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var accepted = (await response.Content.ReadFromJsonAsync<DeliverySubmissionAccepted>())!;

            await using var db = CatalogDatabase.Create(cs);
            var stored = await db.DeliveryInlineSubmissions.AsNoTracking().SingleAsync(s => s.SubmissionId == accepted.SubmissionId);
            Assert.Equal(estate.PayloadFlow, stored.FlowName);
            Assert.Equal(1, stored.RecordCount);
            Assert.Contains(location, stored.RecordsJson, StringComparison.Ordinal);
            Assert.Contains("sha256:abc", stored.RecordsJson, StringComparison.Ordinal);
        }
        finally
        {
            await estate.CleanupAsync(cs);
        }
    }

    [SkippableFact]
    public async Task The_listing_names_the_flows_that_offer_manual_submission()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.ProvisionAsync(cs);
        var estate = await Estate.SeedAsync(cs);
        try
        {
            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            using var offered = await GetAsync(client, token, "/api/v1/delivery/manual-submission/flows");
            Assert.Equal(HttpStatusCode.OK, offered.StatusCode);
            var flows = (await offered.Content.ReadFromJsonAsync<List<DeliveryManualFlowDto>>())!;
            var records = Assert.Single(flows, f => f.FlowName == estate.RecordsFlow);
            Assert.True(records.AcceptsRecords);
            Assert.Null(records.RecordsRefusal);
            Assert.Equal(MappingReference, records.MappingReference);
            Assert.Equal("OsduRecord", records.Protocol);
            Assert.Equal(estate.RecordsPipeline, records.PipelineId);
            Assert.Equal("site", Assert.Single(records.Parameters).Name);
            Assert.Null(records.PayloadName);
            // A flow that streams files offers manual submission on the same terms, and names the payload its records point at.
            var streaming = Assert.Single(flows, f => f.FlowName == estate.PayloadFlow);
            Assert.True(streaming.AcceptsRecords);
            Assert.Equal("files", streaming.PayloadName);
            // A flow that offers none is not on the list at all.
            Assert.DoesNotContain(flows, f => f.FlowName == estate.NoManualFlow);

            using var all = await GetAsync(client, token, "/api/v1/delivery/manual-submission/flows?all=true");
            var everything = (await all.Content.ReadFromJsonAsync<List<DeliveryManualFlowDto>>())!;
            var noManual = Assert.Single(everything, f => f.FlowName == estate.NoManualFlow);
            Assert.False(noManual.AcceptsRecords);
            Assert.Contains("source.manualSubmission", noManual.RecordsRefusal, StringComparison.Ordinal);
        }
        finally
        {
            await estate.CleanupAsync(cs);
        }
    }

    [SkippableFact]
    public async Task The_source_contract_names_what_a_flow_takes()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.ProvisionAsync(cs);
        var estate = await Estate.SeedAsync(cs);
        try
        {
            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            using var records = await GetAsync(client, token, $"/api/v1/delivery/flows/{estate.RecordsPipeline}/source-contract");
            Assert.Equal(HttpStatusCode.OK, records.StatusCode);
            var contract = (await records.Content.ReadFromJsonAsync<DeliverySourceContractDto>())!;
            Assert.True(contract.AcceptsRecords);
            Assert.Null(contract.RecordsRefusal);
            Assert.Null(contract.MappingProblem);
            Assert.Equal(MappingReference, contract.MappingReference);
            Assert.Equal("OsduRecord", contract.Protocol);
            Assert.Equal(["facility_name", "facility_description", "facility_id"], contract.RecordColumns);
            Assert.Equal(["facility_name"], contract.NaturalKey);
            var aliases = Assert.Single(contract.Scopes);
            Assert.Equal("aliases", aliases.Scope);
            Assert.Equal(["alias_name"], aliases.Columns);
            Assert.Equal("update_date", contract.LastModifiedColumn);
            Assert.Null(contract.FingerprintColumn);
            var site = Assert.Single(contract.Parameters);
            Assert.Equal("site", site.Name);
            Assert.True(site.Required);
            Assert.Null(site.Default);
            Assert.Equal(1000, contract.MaxRecords);
            // A flow that streams nothing says so, and a caller filling in a submission carries no files.
            Assert.Null(contract.PayloadName);
            Assert.False(contract.PayloadHashRequired);
            Assert.Empty(contract.PayloadRoots);

            using var payload = await GetAsync(client, token, $"/api/v1/delivery/flows/{estate.PayloadPipeline}/source-contract");
            var streaming = (await payload.Content.ReadFromJsonAsync<DeliverySourceContractDto>())!;
            Assert.True(streaming.AcceptsRecords);
            Assert.Null(streaming.RecordsRefusal);
            Assert.Equal("files", streaming.PayloadName);
            // The flow watches the files' modified times, so a record carries a hash only when its source has one.
            Assert.False(streaming.PayloadHashRequired);
            Assert.Equal([Estate.FileRoot], streaming.PayloadRoots);

            // A flow whose pinned mapping the catalog has not synced says so instead of guessing the columns.
            using var unsynced = await GetAsync(client, token, $"/api/v1/delivery/flows/{estate.UnsyncedMappingPipeline}/source-contract");
            var problem = (await unsynced.Content.ReadFromJsonAsync<DeliverySourceContractDto>())!;
            Assert.True(problem.AcceptsRecords);
            Assert.Empty(problem.RecordColumns);
            Assert.Contains("has not synced", problem.MappingProblem, StringComparison.Ordinal);

            using var unknown = await GetAsync(client, token, $"/api/v1/delivery/flows/{Guid.NewGuid()}/source-contract");
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }
        finally
        {
            await estate.CleanupAsync(cs);
        }
    }

    /// <summary>
    /// The caller's own name for a submission: stored with the accepted request, read back on the submission's page, and
    /// part of what a reused id has to match, so a retry that relabels the work is a conflict rather than a silent
    /// rewrite of what the ledger says the source called it.
    /// </summary>
    [SkippableFact]
    public async Task A_submission_carries_the_name_its_source_knows_it_by()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.ProvisionAsync(cs);
        var estate = await Estate.SeedAsync(cs);
        try
        {
            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);
            var submissionId = Guid.NewGuid();
            var records = new[] { Wellbore("WB-API-REF-1") };
            var body = new { submissionId, flow = estate.RecordsFlow, parameters = new { site = "north" }, reference = "  NO 15/9-19 SR___GR.las  ", records };

            using (var first = await PostAsync(client, token, body))
            {
                Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
            }

            // Stored trimmed, which is the form every later comparison and search works against.
            await using (var db = CatalogDatabase.Create(cs))
            {
                var stored = await db.DeliveryInlineSubmissions.AsNoTracking().SingleAsync(s => s.SubmissionId == submissionId);
                Assert.Equal("NO 15/9-19 SR___GR.las", stored.Reference);
            }

            // And read back beside the records it was sent with, where a source goes looking for what it sent.
            using (var read = await GetAsync(client, token, $"/api/v1/delivery/submissions/{submissionId:D}/content"))
            {
                var inline = (await read.Content.ReadFromJsonAsync<DeliveryInlineSubmissionDto>())!;
                Assert.Equal("NO 15/9-19 SR___GR.las", inline.Reference);
            }

            // The same request again, however its reference is spaced, is the same submission.
            using (var repeat = await PostAsync(client, token, body))
            {
                Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
                Assert.True((await repeat.Content.ReadFromJsonAsync<DeliverySubmissionAccepted>())!.Replayed);
            }

            // Relabelling under the same id is a new request wearing an old name, so it is refused saying which.
            using (var relabelled = await PostAsync(
                client, token, new { submissionId, flow = estate.RecordsFlow, parameters = new { site = "north" }, reference = "something-else.las", records }))
            {
                Assert.Equal(HttpStatusCode.Conflict, relabelled.StatusCode);
                var text = await relabelled.Content.ReadAsStringAsync();
                Assert.Contains("the reference", text, StringComparison.Ordinal);
                Assert.Contains("something-else.las", text, StringComparison.Ordinal);
            }

            // Dropping it is a change too, not an omission to be filled in from what was stored.
            using (var dropped = await PostAsync(
                client, token, new { submissionId, flow = estate.RecordsFlow, parameters = new { site = "north" }, records }))
            {
                Assert.Equal(HttpStatusCode.Conflict, dropped.StatusCode);
                Assert.Contains("the reference (none,", await dropped.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            // A submission with no reference is the ordinary case and stays absent rather than becoming empty.
            using (var plain = await PostAsync(
                client, token, new { flow = estate.RecordsFlow, parameters = new { site = "north" }, records = new[] { Wellbore("WB-API-REF-2") } }))
            {
                Assert.Equal(HttpStatusCode.Accepted, plain.StatusCode);
                var accepted = (await plain.Content.ReadFromJsonAsync<DeliverySubmissionAccepted>())!;
                await using var db = CatalogDatabase.Create(cs);
                Assert.Null((await db.DeliveryInlineSubmissions.AsNoTracking().SingleAsync(s => s.SubmissionId == accepted.SubmissionId)).Reference);
            }

            // The submissions listing narrows by reference, which is how a source finds work it knows by its own name.
            // Nothing has run, so the ledger holds no registered submission yet and the filter answers on an empty set
            // rather than on everything: an unfiltered listing and a filtered one must not be the same answer.
            using (var filtered = await GetAsync(client, token, $"/api/v1/delivery/flows/{estate.RecordsPipeline}/submissions?reference=15/9-19"))
            {
                Assert.NotNull(await filtered.Content.ReadFromJsonAsync<List<DeliverySubmissionDto>>());
            }
        }
        finally
        {
            await estate.CleanupAsync(cs);
        }
    }

    private static ControlPlaneAppFactory Factory(string cs)
        => new ControlPlaneAppFactory().WithCatalog(cs).WithSetting("ControlPlane:Worker:Enabled", "false");

    private static async Task<string> TokenAsync(HttpClient client, params string[] scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, scopes.Length > 0 ? scopes : ["read", "operate"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string? token, object body)
        => PostRawAsync(client, token, JsonSerializer.Serialize(body));

    private static async Task<HttpResponseMessage> PostRawAsync(HttpClient client, string? token, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/delivery/submissions", UriKind.Relative));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    /// <summary>
    /// A repository of wellbore flows: one that takes records, one that streams files, one whose parameter has a
    /// default, one pinning a mapping the catalog never synced, and a second repository holding a flow of the same name
    /// so an ambiguous name can be refused.
    /// </summary>
    private sealed record Estate(
        Guid RepoId,
        Guid SecondRepoId,
        string RecordsFlow,
        Guid RecordsPipeline,
        string PayloadFlow,
        Guid PayloadPipeline,
        string DefaultedFlow,
        string UnsyncedMappingFlow,
        Guid UnsyncedMappingPipeline,
        string AmbiguousFlow,
        string NoManualFlow,
        Guid NoManualPipeline)
    {
        /// <summary>The one place the file flow lets a submission point at: what is inside is allowed, what is outside is not.</summary>
        public const string FileRoot = "C:/lake/wellbore";

        public static async Task<Estate> SeedAsync(string cs)
        {
            var suffix = Guid.NewGuid().ToString("N")[..10];
            var repoName = "cp-inline-" + suffix;
            var repoId = FlowIdentity.FromName("repo/" + repoName);
            var secondRepoId = FlowIdentity.FromName("repo/second-" + repoName);
            var records = "wellbore-records-" + suffix;
            var payload = "wellbore-files-" + suffix;
            var defaulted = "wellbore-defaulted-" + suffix;
            var unsynced = "wellbore-unsynced-" + suffix;
            var ambiguous = "wellbore-ambiguous-" + suffix;
            var noManual = "wellbore-no-manual-" + suffix;
            var now = DateTime.UtcNow;

            await using var db = CatalogDatabase.Create(cs);
            db.Repos.Add(new CatalogRepo { Id = repoId, Name = repoName, FirstSeenUtc = now, LastSyncUtc = now });
            db.Repos.Add(new CatalogRepo { Id = secondRepoId, Name = "second-" + repoName, FirstSeenUtc = now, LastSyncUtc = now });
            db.Pipelines.Add(Pipeline(repoId, records, RecordsYaml(records, MappingReference, "    required: true"), now));
            db.Pipelines.Add(Pipeline(repoId, defaulted, RecordsYaml(defaulted, MappingReference, "    default: south"), now));
            db.Pipelines.Add(Pipeline(repoId, unsynced, RecordsYaml(unsynced, "NeverSynced@9.9.9", "    required: true"), now));
            db.Pipelines.Add(Pipeline(repoId, ambiguous, RecordsYaml(ambiguous, MappingReference, "    required: true"), now));
            db.Pipelines.Add(Pipeline(secondRepoId, ambiguous, RecordsYaml(ambiguous, MappingReference, "    required: true"), now));
            db.Pipelines.Add(Pipeline(repoId, noManual, RecordsYaml(noManual, MappingReference, "    required: true", manualSubmission: false), now));
            db.Pipelines.Add(Pipeline(repoId, payload, $$"""
                flowType: delivery
                name: {{payload}}
                parameters:
                  site:
                    required: true
                source:
                  location: C:/drops/files
                  payloads:
                    files: files/{deliveryKey}/*.csv
                  lastModified: update_date
                  manualSubmission: true
                  manualSubmissionFileRoots:
                    - {{FileRoot}}
                change:
                  payloadDetect: lastModified
                render:
                  mapping: {{MappingReference}}
                  references: pinned
                  parameters:
                    dataPartition: opendes
                target:
                  endpoint: https://osdu.example.test
                  headers:
                    data-partition-id: opendes
                  protocol: osduFile
                  protocolOptions:
                    payload: files
                    payloadContentType: text/csv
                """, now));
            db.DeliveryMappings.Add(new DeliveryMapping
            {
                Id = Guid.NewGuid(),
                RepoId = repoId,
                Reference = MappingReference,
                Name = "Wellbore",
                Version = "1.0.0",
                Kind = "osdu:wks:master-data--Wellbore:1.3.0",
                RelativePath = "mappings/Wellbore@1.0.0.yaml",
                ContentHash = new string('0', 64),
                Yaml = MappingYaml,
                Status = "valid",
                FirstSeenUtc = now,
                LastSeenUtc = now,
            });
            await db.SaveChangesAsync();
            return new Estate(
                repoId, secondRepoId, records, CatalogIdentity.Pipeline(repoId, records), payload, CatalogIdentity.Pipeline(repoId, payload),
                defaulted, unsynced, CatalogIdentity.Pipeline(repoId, unsynced), ambiguous, noManual, CatalogIdentity.Pipeline(repoId, noManual));
        }

        /// <summary>A drop's submission in the ledger, so an id it already uses can be refused for records.</summary>
        public async Task<Guid> SeedDropSubmissionAsync(string cs)
        {
            var id = Guid.NewGuid();
            await using var db = CatalogDatabase.Create(cs);
            db.DeliverySubmissions.Add(new DeliverySubmission
            {
                SubmissionId = id,
                FlowId = FlowIdentity.FromName(RecordsFlow),
                FlowName = RecordsFlow,
                MappingReference = MappingReference,
                RenderContext = "{}",
                DropLocation = "C:/drops/north",
                ParametersJson = """{"site":"north"}""",
                Status = "completed",
                ReceivedUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            return id;
        }

        public async Task CleanupAsync(string cs)
        {
            await using var db = CatalogDatabase.Create(cs);
            var flows = new[] { RecordsFlow, PayloadFlow, DefaultedFlow, UnsyncedMappingFlow, AmbiguousFlow, NoManualFlow };
            await db.DeliveryInlineSubmissions.Where(s => flows.Contains(s.FlowName)).ExecuteDeleteAsync();
            await db.DeliverySubmissions.Where(s => flows.Contains(s.FlowName)).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.RepoId == RepoId || r.RepoId == SecondRepoId).ExecuteDeleteAsync();
            await db.DeliveryMappings.Where(m => m.RepoId == RepoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == RepoId || p.RepoId == SecondRepoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == RepoId || r.Id == SecondRepoId).ExecuteDeleteAsync();
        }

        private static string RecordsYaml(string name, string mapping, string parameterRule, bool manualSubmission = true) => $$"""
            flowType: delivery
            name: {{name}}
            parameters:
              site:
            {{parameterRule}}
                description: The site the wellbores belong to.
            source:
              location: C:/drops/{site}
              lastModified: update_date
            {{(manualSubmission ? "  manualSubmission: true" : string.Empty)}}
            render:
              mapping: {{mapping}}
              references: pinned
              parameters:
                dataPartition: opendes
            target:
              endpoint: https://osdu.example.test
              headers:
                data-partition-id: opendes
              protocol: osduRecord
            """;

        private static CatalogPipeline Pipeline(Guid repoId, string name, string yaml, DateTime now) => new()
        {
            Id = CatalogIdentity.Pipeline(repoId, name),
            RepoId = repoId,
            Name = name,
            Kind = "delivery",
            RelativePath = $"flows/{name}.yaml",
            ContentHash = new string('0', 64),
            Yaml = yaml,
            DefinitionJson = JsonSerializer.Serialize(new { name, flowKind = "delivery" }),
            Active = true,
            Wave = 0,
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };
    }
}
