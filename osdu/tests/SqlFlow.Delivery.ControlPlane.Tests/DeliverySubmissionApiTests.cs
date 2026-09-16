using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.ControlPlane.Api;
using SqlFlow.Delivery.ControlPlane.Background;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Records sent through <c>POST /api/v1/delivery/submissions</c> (docs/stage4-design.md section 4): what the boundary
/// refuses, that an accepted request lands its records as files for the flow's pre-ingestion flows and queues the chain
/// of pre, ing and OSDU runs that delivers them with the ledger rows describing it, that a repeat answers with that
/// chain and queues nothing, that a reused id with anything else is a conflict, and that a submission whose request died
/// is finished by the resume sweep. The in-process worker is off unless a test runs a flow, so the assertions are about
/// what the API stored. Gated on a reachable catalog database.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DeliverySubmissionApiTests
{
    /// <summary>A wellbore as a source system sends it: the columns the sample mapping reads, and its alternative names.</summary>
    private static object Wellbore(string name, string description = "Sent by the source system", params string[] aliases) => new
    {
        record = new Dictionary<string, object?>
        {
            ["facility_name"] = name,
            ["facility_description"] = description,
            ["facility_id"] = "srn:master-data/Wellbore:" + name,
            ["update_date"] = "2026-09-12T10:00:00Z",
        },
        datasets = new Dictionary<string, object> { ["aliases"] = aliases.Select(a => new { alias_name = a }).ToArray() },
    };

    [SkippableFact]
    public async Task Records_land_for_the_pre_flows_and_the_chain_is_queued_with_them()
    {
        var cs = CatalogTestDb.Require();
        var estate = await Estate.SeedAsync(cs);
        try
        {
            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);
            var submissionId = Guid.NewGuid();

            using var response = await PostAsync(client, token, new
            {
                submissionId,
                flow = SampleEstate.WellboreFlowName,
                reference = "job-17",
                records = new[] { Wellbore("WB-API-1", aliases: ["A-1", "A-2"]) },
            });

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var accepted = await response.Content.ReadFromJsonAsync<DeliverySubmissionAccepted>();
            Assert.NotNull(accepted);
            Assert.False(accepted.Replayed);
            Assert.Equal(submissionId, accepted.SubmissionId);
            Assert.NotNull(accepted.GroupId);
            Assert.Equal($"/api/v1/runs/groups/{accepted.GroupId}", response.Headers.Location?.OriginalString);

            await using var osdu = SampleEstate.Context(cs);
            var stored = await osdu.DeliveryInlineSubmissions.AsNoTracking().SingleAsync(s => s.SubmissionId == submissionId);
            Assert.Equal(InlineStatuses.Queued, stored.Status);
            Assert.Equal(accepted.GroupId, stored.GroupId);
            Assert.Equal(accepted.RunId, stored.OsduRunId);
            Assert.Equal("job-17", stored.Reference);
            Assert.Equal(1, stored.RecordCount);
            Assert.Equal(2, stored.ChildRowCount);
            Assert.NotNull(stored.LandedUtc);
            Assert.Null(stored.Error);

            // One landing per dataset, each written where its pre flow reads, and each taken by that flow's member run.
            var landings = await osdu.DeliverySubmissionLandings.AsNoTracking()
                .Where(l => l.SubmissionId == submissionId)
                .OrderBy(l => l.Dataset)
                .ToListAsync();
            Assert.Equal(["aliases", "record"], landings.Select(l => l.Dataset));
            Assert.Equal([SampleEstate.WellboreAliasesPreFlow, SampleEstate.WellborePreFlow], landings.Select(l => l.PreFlowName));
            foreach (var landing in landings)
            {
                Assert.Equal($"{submissionId:N}_{landing.Dataset}.csv", landing.FileName);
                Assert.True(File.Exists(landing.Location), landing.Location + " was not written");
                Assert.NotNull(landing.WrittenUtc);
                Assert.NotNull(landing.PreRunId);
                Assert.Equal(64, landing.ContentHash.Length);
                Assert.True(landing.Bytes > 0);
            }

            var recordFile = await File.ReadAllLinesAsync(landings[1].Location);
            Assert.Contains("facility_name", recordFile[0], StringComparison.Ordinal);
            Assert.Contains("WB-API-1", recordFile[1], StringComparison.Ordinal);
            Assert.Equal(2, await File.ReadAllLinesAsync(landings[0].Location).ContinueWith(t => t.Result.Length - 1, TaskScheduler.Default));

            // The chain the group queued: both pre flows, both ing flows and the OSDU flow, in wave order, each with
            // the parameters its part of the work needs.
            await using var db = CatalogDatabase.Create(cs);
            var members = await db.Runs.AsNoTracking()
                .Where(r => r.GroupId == accepted.GroupId)
                .OrderBy(r => r.GroupWave).ThenBy(r => r.FlowName)
                .Select(r => new { r.RunId, r.FlowName, r.GroupWave, r.FullLoad, r.FilePattern, r.Operation, r.Payload })
                .ToListAsync();
            Assert.Equal(5, members.Count);
            Assert.Equal(
                [SampleEstate.WellboreAliasesPreFlow, SampleEstate.WellborePreFlow, SampleEstate.WellboreAliasesIngFlow, SampleEstate.WellboreIngFlow, SampleEstate.WellboreFlowName],
                members.Select(m => m.FlowName));
            foreach (var pre in members.Where(m => m.FlowName.EndsWith("-pre", StringComparison.Ordinal)))
            {
                Assert.True(pre.FullLoad);
                Assert.EndsWith(".csv", pre.FilePattern!, StringComparison.Ordinal);
                Assert.Contains(submissionId.ToString("N"), pre.FilePattern!, StringComparison.Ordinal);
            }

            var osduMember = members[^1];
            Assert.Equal(accepted.RunId, osduMember.RunId);
            Assert.Equal(DeliveryOperations.Deliver, osduMember.Operation);
            Assert.Equal(submissionId, DeliveryRunPayload.Parse(osduMember.Payload).SubmissionId);

            // The submission is in the flow's activity trail, with who sent it.
            var activity = await osdu.DeliveryActivities.AsNoTracking().SingleAsync(a => a.SubmissionId == submissionId && a.Kind == "submit");
            Assert.Equal("completed", activity.Outcome);
            Assert.Contains("1 record(s) accepted", activity.Summary!, StringComparison.Ordinal);
        }
        finally
        {
            await estate.CleanupAsync(cs);
        }
    }

    [SkippableFact]
    public async Task A_repeat_answers_with_the_chain_that_was_queued_and_queues_nothing()
    {
        var cs = CatalogTestDb.Require();
        var estate = await Estate.SeedAsync(cs);
        try
        {
            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);
            var submissionId = Guid.NewGuid();
            var body = new { submissionId, flow = SampleEstate.WellboreFlowName, records = new[] { Wellbore("WB-API-REPEAT") } };

            using var first = await PostAsync(client, token, body);
            Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
            var accepted = (await first.Content.ReadFromJsonAsync<DeliverySubmissionAccepted>())!;

            // The same records with their keys in another order and other whitespace are the same submission.
            using var repeat = await PostRawAsync(client, token, $$$"""
                { "submissionId": "{{{submissionId}}}", "flow": "{{{SampleEstate.WellboreFlowName}}}",
                  "records": [ { "datasets": { "aliases": [] },
                                 "record": { "update_date": "2026-09-12T10:00:00Z", "facility_id": "srn:master-data/Wellbore:WB-API-REPEAT",
                                             "facility_description": "Sent by the source system", "facility_name": "WB-API-REPEAT" } } ] }
                """);

            Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
            var replayed = (await repeat.Content.ReadFromJsonAsync<DeliverySubmissionAccepted>())!;
            Assert.True(replayed.Replayed);
            Assert.Equal(accepted.GroupId, replayed.GroupId);
            Assert.Equal(accepted.RunId, replayed.RunId);

            await using var db = CatalogDatabase.Create(cs);
            Assert.Equal(5, await db.Runs.AsNoTracking().CountAsync(r => r.GroupId == accepted.GroupId));
            Assert.Equal(1, await db.RunGroups.AsNoTracking().CountAsync(g => g.RepoId == estate.RepoId));
        }
        finally
        {
            await estate.CleanupAsync(cs);
        }
    }

    [SkippableFact]
    public async Task A_reused_id_with_anything_different_is_a_conflict()
    {
        var cs = CatalogTestDb.Require();
        var estate = await Estate.SeedAsync(cs);
        try
        {
            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);
            var submissionId = Guid.NewGuid();
            var records = new[] { Wellbore("WB-API-CONFLICT") };

            using (var first = await PostAsync(client, token, new { submissionId, flow = SampleEstate.WellboreFlowName, records }))
            {
                Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
            }

            foreach (var (changed, names) in new (object Body, string Names)[]
            {
                (new { submissionId, flow = SampleEstate.WellboreFlowName, records = new[] { Wellbore("WB-API-CONFLICT", "changed") } }, "the records"),
                (new { submissionId, flow = SampleEstate.WellboreFlowName, operation = "plan", records }, "the operation"),
                (new { submissionId, flow = SampleEstate.WellboreFlowName, reference = "something-else", records }, "the reference"),
            })
            {
                using var conflict = await PostAsync(client, token, changed);
                Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
                Assert.Contains(names, await conflict.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            // An id a plan of the ingestion tables already used is not a place to put records either.
            var planned = await Estate.SeedPlannedSubmissionAsync(cs);
            using var taken = await PostAsync(client, token, new { submissionId = planned, flow = SampleEstate.WellboreFlowName, records });
            Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
            Assert.Contains("under an id of their own", await taken.Content.ReadAsStringAsync(), StringComparison.Ordinal);
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

            await ExpectAsync(new { flow = SampleEstate.WellboreFlowName }, HttpStatusCode.BadRequest, "carries the records to deliver");
            await ExpectAsync(new { flow = SampleEstate.WellboreFlowName, operation = "verify", records = one }, HttpStatusCode.BadRequest, "operation is deliver or plan");
            await ExpectAsync(new { flow = SampleEstate.WellboreFlowName, submissionId = Guid.Empty, records = one }, HttpStatusCode.BadRequest, "non-empty UUID");
            await ExpectAsync(new { flow = SampleEstate.WellboreFlowName, records = Array.Empty<object>() }, HttpStatusCode.BadRequest, "is empty");
            await ExpectAsync(
                new { flow = SampleEstate.WellboreFlowName, reference = new string('x', 201), records = one },
                HttpStatusCode.BadRequest,
                "reference is at most 200 characters");
            // Every record names every key column of the record table, or the plan could never find its row.
            await ExpectAsync(
                new { flow = SampleEstate.WellboreFlowName, records = new[] { new { record = new Dictionary<string, object?> { ["facility_description"] = "no key" } } } },
                HttpStatusCode.BadRequest,
                "records[0].record.facility_name is empty");
            await ExpectAsync(
                new { flow = SampleEstate.WellboreFlowName, records = new[] { new { record = new Dictionary<string, object?> { ["facility_name"] = new { nested = 1 } } } } },
                HttpStatusCode.BadRequest,
                "is a nested value");
            // A flow that declares no source.submissions has nowhere to land records, so it takes none.
            await ExpectAsync(new { flow = estate.NoSubmissionsFlow, records = one }, HttpStatusCode.BadRequest, "source.submissions");
            await ExpectAsync(new { flow = "no-such-flow-" + Guid.NewGuid().ToString("N"), records = one }, HttpStatusCode.NotFound, "No active delivery flow");
            await ExpectAsync(new { pipelineId = Guid.NewGuid(), records = one }, HttpStatusCode.NotFound, "pipeline");

            using (var malformed = await PostRawAsync(client, token, "{ not json"))
            {
                Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
            }

            await using var db = CatalogDatabase.Create(cs);
            Assert.Equal(0, await db.Runs.AsNoTracking().CountAsync(r => r.RepoId == estate.RepoId));
            await using var osdu = SampleEstate.Context(cs);
            Assert.Equal(0, await osdu.DeliveryInlineSubmissions.AsNoTracking().CountAsync(s => s.FlowName == SampleEstate.WellboreFlowName));
        }
        finally
        {
            await estate.CleanupAsync(cs);
        }
    }

    [SkippableFact]
    public async Task The_submission_reads_back_with_its_records_and_the_files_it_landed()
    {
        var cs = CatalogTestDb.Require();
        var estate = await Estate.SeedAsync(cs);
        try
        {
            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);
            var submissionId = Guid.NewGuid();

            using (var accepted = await PostAsync(client, token, new
            {
                submissionId,
                flow = SampleEstate.WellboreFlowName,
                records = new[] { Wellbore("WB-API-READBACK", aliases: ["RB-1"]) },
            }))
            {
                Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            }

            using var read = await GetAsync(client, token, $"/api/v1/delivery/submissions/{submissionId:D}/content");
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            var inline = (await read.Content.ReadFromJsonAsync<DeliveryInlineSubmissionDto>())!;

            Assert.Equal(SampleEstate.WellboreFlowName, inline.FlowName);
            Assert.Equal(SampleEstate.WellboreMapping, inline.MappingReference);
            Assert.Equal(InlineStatuses.Queued, inline.Status);
            Assert.NotNull(inline.GroupId);
            Assert.NotNull(inline.OsduRunId);
            Assert.Equal(2, inline.Landings.Count);
            Assert.All(inline.Landings, landing => Assert.NotNull(landing.WrittenUtc));
            Assert.Contains(inline.RunIds, id => id == inline.OsduRunId);
            var record = inline.Records[0];
            Assert.Equal("WB-API-READBACK", record.GetProperty("record").GetProperty("facility_name").GetString());
            Assert.Equal("RB-1", record.GetProperty("datasets").GetProperty("aliases")[0].GetProperty("alias_name").GetString());

            using var unknown = await GetAsync(client, token, $"/api/v1/delivery/submissions/{Guid.NewGuid():D}/content");
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }
        finally
        {
            await estate.CleanupAsync(cs);
        }
    }

    [SkippableFact]
    public async Task Submitting_records_needs_a_token()
    {
        var cs = CatalogTestDb.Require();
        var estate = await Estate.SeedAsync(cs);
        try
        {
            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var body = new { flow = SampleEstate.WellboreFlowName, records = new[] { Wellbore("WB-API-AUTH") } };

            using (var anonymous = await PostAsync(client, token: null, body))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            }

            using (var garbled = await PostAsync(client, "not-a-token", body))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, garbled.StatusCode);
            }

            await using (var osdu = SampleEstate.Context(cs))
            {
                Assert.Equal(0, await osdu.DeliveryInlineSubmissions.AsNoTracking().CountAsync(s => s.FlowName == SampleEstate.WellboreFlowName));
            }

            // The reads a caller needs to fill in a submission are reads: a read-scoped token gets them.
            var readOnly = await TokenAsync(client, "read");
            using var contract = await GetAsync(client, readOnly, $"/api/v1/delivery/flows/{estate.WellborePipeline}/source-contract");
            Assert.Equal(HttpStatusCode.OK, contract.StatusCode);
            var source = (await contract.Content.ReadFromJsonAsync<DeliverySourceContractDto>())!;
            Assert.True(source.AcceptsRecords);
            Assert.Equal(["facility_name"], source.Key);
            Assert.Equal("OsduSample.ing.Wellbore", source.SourceObject);
            Assert.Equal("update_date", source.LastModifiedColumn);
            Assert.Empty(source.Payloads);

            using var listing = await GetAsync(client, readOnly, "/api/v1/delivery/manual-submission/flows");
            Assert.Equal(HttpStatusCode.OK, listing.StatusCode);
            var flows = (await listing.Content.ReadFromJsonAsync<List<DeliveryManualFlowDto>>())!;
            var wellbore = Assert.Single(flows, f => f.FlowName == SampleEstate.WellboreFlowName);
            Assert.True(wellbore.AcceptsRecords);
            Assert.DoesNotContain(flows, f => f.FlowName == estate.NoSubmissionsFlow);

            using var accepted = await PostAsync(client, await TokenAsync(client), body);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }
        finally
        {
            await estate.CleanupAsync(cs);
        }
    }

    /// <summary>
    /// A request that stored its records and then died: the sweep lands the files and queues the chain, so a submission
    /// the ledger accepted is always delivered, whatever happened to the request that accepted it.
    /// </summary>
    [SkippableFact]
    public async Task A_submission_whose_request_died_is_finished_by_the_resume_sweep()
    {
        var cs = CatalogTestDb.Require();
        var estate = await Estate.SeedAsync(cs);
        try
        {
            await using var factory = Factory(cs)
                .WithSetting("Osdu:Submissions:Enabled", "true")
                .WithSetting("Osdu:Submissions:PollSeconds", "1")
                .WithSetting("Osdu:Submissions:GraceSeconds", "1");
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);
            var submissionId = Guid.NewGuid();

            using (var accepted = await PostAsync(client, token, new
            {
                submissionId,
                flow = SampleEstate.WellboreFlowName,
                records = new[] { Wellbore("WB-API-RESUME") },
            }))
            {
                Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            }

            // Wind the submission back to where a died request would have left it: accepted, no chain, no files.
            await using (var osdu = SampleEstate.Context(cs))
            {
                var stored = await osdu.DeliveryInlineSubmissions.SingleAsync(s => s.SubmissionId == submissionId);
                var groupId = stored.GroupId;
                stored.Status = InlineStatuses.Accepted;
                stored.GroupId = null;
                stored.OsduRunId = null;
                stored.LandedUtc = null;
                stored.ReceivedUtc = DateTime.UtcNow.AddMinutes(-5);
                foreach (var landing in await osdu.DeliverySubmissionLandings.Where(l => l.SubmissionId == submissionId).ToListAsync())
                {
                    File.Delete(landing.Location);
                    landing.WrittenUtc = null;
                    landing.PreRunId = null;
                }

                await osdu.SaveChangesAsync();
                await using var db = CatalogDatabase.Create(cs);
                await db.Runs.Where(r => r.GroupId == groupId).ExecuteDeleteAsync();
                await db.RunGroups.Where(g => g.GroupId == groupId).ExecuteDeleteAsync();
            }

            var sweep = factory.Services.GetServices<IHostedService>().OfType<SubmissionLandingService>().Single();
            await sweep.StartAsync(CancellationToken.None);
            try
            {
                DeliveryInlineSubmission? finished = null;
                for (var attempt = 0; attempt < 40 && finished is null; attempt++)
                {
                    await Task.Delay(250);
                    await using var osdu = SampleEstate.Context(cs);
                    finished = await osdu.DeliveryInlineSubmissions.AsNoTracking()
                        .SingleOrDefaultAsync(s => s.SubmissionId == submissionId && s.GroupId != null);
                }

                Assert.True(finished is not null, "the resume sweep never queued the submission's chain");
                Assert.Equal(InlineStatuses.Queued, finished!.Status);

                await using var check = SampleEstate.Context(cs);
                var landings = await check.DeliverySubmissionLandings.AsNoTracking().Where(l => l.SubmissionId == submissionId).ToListAsync();
                Assert.All(landings, landing => Assert.True(File.Exists(landing.Location), landing.Location + " was not landed again"));
                Assert.All(landings, landing => Assert.NotNull(landing.WrittenUtc));
            }
            finally
            {
                await sweep.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            await estate.CleanupAsync(cs);
        }
    }

    /// <summary>
    /// The platform's run path end to end over a flow of the module's kind: a triggered <c>plan</c> run of the sample
    /// wellbore flow, executed by the in-process node, rendering the records the flow's ingestion tables hold. It needs
    /// no OSDU target, which is what makes it a safe end-to-end proof that the module's kind really executes.
    /// </summary>
    [SkippableFact]
    public async Task A_plan_run_of_the_sample_flow_executes_end_to_end()
    {
        var cs = CatalogTestDb.Require();
        var estate = await Estate.SeedAsync(cs);
        var tables = new MemoryIngestionTables();
        tables.Add(new MemoryRecord
        {
            Row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["facility_name"] = "OSDU-DEV-1-A",
                ["facility_description"] = "Sample wellbore A",
                ["facility_id"] = "srn:master-data/Wellbore:A",
                ["update_date"] = new DateTime(2026, 9, 1, 6, 30, 0, DateTimeKind.Utc),
            },
            UpdatedUtc = new DateTime(2026, 9, 1, 6, 30, 0, DateTimeKind.Utc),
            FileName = "wellbore_20260901.csv",
            RowNumber = 1,
        }).AddChild("aliases", new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["facility_name"] = "OSDU-DEV-1-A",
            ["alias_name"] = "WB-A",
        });

        try
        {
            await SampleEstate.SaveTemplatesAsync(cs);
            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false")
                .WithSetting("Osdu:Submissions:Enabled", "false")
                .WithServices(services => services.AddSingleton<IIngestionSourceFactory>(tables));
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            Guid runId;
            using (var response = await client.SendAsync(Authorized(
                HttpMethod.Post, "/api/v1/runs", token,
                new RunTriggerRequest(estate.RepoId, SampleEstate.WellboreFlowName, Operation: DeliveryOperations.Plan))))
            {
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                var accepted = await response.Content.ReadFromJsonAsync<RunTriggerAccepted>();
                Assert.NotNull(accepted);
                runId = accepted.RunId;
            }

            string? status = null;
            string? error = null;
            for (var attempt = 0; attempt < 160 && status is not ("succeeded" or "failed" or "cancelled"); attempt++)
            {
                await Task.Delay(250);
                using var detail = await client.SendAsync(Authorized(HttpMethod.Get, $"/api/v1/runs/{runId:D}", token));
                if (detail.StatusCode != HttpStatusCode.OK)
                {
                    continue;
                }

                using var document = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
                status = document.RootElement.GetProperty("status").GetString();
                error = document.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            }

            var log = string.Join(Environment.NewLine, factory.Logs.TakeLast(40));
            Assert.True(status == "succeeded", $"the plan run ended '{status}': {error}{Environment.NewLine}{log}");
            Assert.Equal(1, tables.Opens);

            // The run's own trace says what it planned, and the record it planned is in the ledger.
            using var trace = await client.SendAsync(Authorized(HttpMethod.Get, $"/api/v1/runs/{runId:D}/trace?pageSize=200", token));
            Assert.Equal(HttpStatusCode.OK, trace.StatusCode);
            using var timeline = JsonDocument.Parse(await trace.Content.ReadAsStringAsync());
            var messages = timeline.RootElement.GetProperty("items").EnumerateArray()
                .Select(e => e.GetProperty("message").GetString() ?? string.Empty)
                .ToList();
            Assert.NotEmpty(messages);
            Assert.True(
                messages.Exists(m => m.Contains("record", StringComparison.OrdinalIgnoreCase)),
                "the plan's trace never mentioned a record: " + string.Join(" | ", messages));
        }
        finally
        {
            await estate.CleanupAsync(cs);
        }
    }

    private static ControlPlaneAppFactory Factory(string cs)
        => new ControlPlaneAppFactory()
            .WithCatalog(cs)
            .WithModules(new DeliveryControlPlaneModule())
            .WithSetting("ControlPlane:Worker:Enabled", "false")
            .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false")
            .WithSetting("Osdu:Submissions:Enabled", "false");

    private static HttpRequestMessage Authorized(HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        return request;
    }

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
    /// The sample estate in the catalog: the wellbore chain (two pre flows, two ing flows and the OSDU flow, with the
    /// lineage edges between them), the mapping the OSDU flow pins, and one delivery flow that declares no submissions,
    /// so a flow that takes no records can be refused. The documents are the estate's own, copied to a temp repository
    /// the suite writes its landing files into.
    /// </summary>
    private sealed record Estate(Guid RepoId, string Root, Guid WellborePipeline, string NoSubmissionsFlow)
    {
        public static async Task<Estate> SeedAsync(string cs)
        {
            await CatalogDatabase.MigrateAsync(cs);
            await SampleEstate.MigrateModuleAsync(cs);

            var suffix = Guid.NewGuid().ToString("N")[..10];
            var repoName = "cp-submission-" + suffix;
            var repoId = FlowIdentity.FromName("repo/" + repoName);
            var root = SampleEstate.CopyTo(Path.Combine(Path.GetTempPath(), "sqlflow_cp_sub_" + suffix));
            var noSubmissions = "wellbore-no-submissions-" + suffix;
            var now = DateTime.UtcNow;

            await using var db = CatalogDatabase.Create(cs);
            db.Repos.Add(new CatalogRepo { Id = repoId, Name = repoName, RemoteUrl = "https://example/" + repoName + ".git", RootPath = root, FirstSeenUtc = now, LastSyncUtc = now });

            var waves = new (string Flow, string Kind, int Wave)[]
            {
                (SampleEstate.WellborePreFlow, "pre", 0),
                (SampleEstate.WellboreAliasesPreFlow, "pre", 0),
                (SampleEstate.WellboreIngFlow, "ing", 1),
                (SampleEstate.WellboreAliasesIngFlow, "ing", 1),
                (SampleEstate.WellboreFlowName, "delivery", 2),
            };
            foreach (var (flow, kind, wave) in waves)
            {
                db.Pipelines.Add(Pipeline(repoId, flow, kind, wave, SampleEstate.FlowYaml(root, flow), SampleEstate.FlowPath(flow), now));
            }

            // A delivery flow of the same repository that declares no source.submissions: the sample well log flow with
            // that block removed, so what is refused is the declaration and nothing else about the flow.
            var welllog = SampleEstate.FlowYaml(root, SampleEstate.FlowName).ReplaceLineEndings("\n");
            var submissions = welllog.IndexOf("\n  submissions:", StringComparison.Ordinal);
            var afterSubmissions = welllog.IndexOf("\nrender:", StringComparison.Ordinal);
            var withoutSubmissions = (welllog[..submissions] + welllog[afterSubmissions..])
                .Replace("name: " + SampleEstate.FlowName, "name: " + noSubmissions, StringComparison.Ordinal);
            db.Pipelines.Add(Pipeline(repoId, noSubmissions, "delivery", 2, withoutSubmissions, "flows/" + noSubmissions + ".yaml", now));

            foreach (var (from, to) in new[]
            {
                (SampleEstate.WellborePreFlow, SampleEstate.WellboreIngFlow),
                (SampleEstate.WellboreAliasesPreFlow, SampleEstate.WellboreAliasesIngFlow),
                (SampleEstate.WellboreIngFlow, SampleEstate.WellboreFlowName),
                (SampleEstate.WellboreAliasesIngFlow, SampleEstate.WellboreFlowName),
            })
            {
                db.FlowDependencies.Add(new CatalogFlowDependency
                {
                    RepoId = repoId,
                    FromFlow = from,
                    ToFlow = to,
                    FromPipelineId = CatalogIdentity.Pipeline(repoId, from),
                    ToPipelineId = CatalogIdentity.Pipeline(repoId, to),
                    ViaObjects = "OsduSample.ing.Wellbore",
                });
            }

            await db.SaveChangesAsync();

            // The mapping the OSDU flow pins, as the repository sync reconciles it into the module's database.
            await using var osdu = SampleEstate.Context(cs);
            osdu.DeliveryMappings.Add(new DeliveryMapping
            {
                Id = FlowIdentity.FromName($"delivery-mapping/{repoId:N}/{SampleEstate.WellboreMapping}"),
                RepoId = repoId,
                Reference = SampleEstate.WellboreMapping,
                Name = "Wellbore",
                Version = "1.0.0",
                Kind = SampleEstate.WellboreTemplateKind,
                TemplateVersion = SampleEstate.WellboreTemplateVersion,
                RelativePath = "mappings/Wellbore@1.0.0.yaml",
                ContentHash = new string('0', 64),
                Yaml = await File.ReadAllTextAsync(Path.Combine(root, "mappings", "Wellbore@1.0.0.yaml")),
                SummaryJson = "{}",
                Status = "valid",
                FirstSeenUtc = now,
                LastSeenUtc = now,
            });
            await osdu.SaveChangesAsync();

            return new Estate(repoId, root, CatalogIdentity.Pipeline(repoId, SampleEstate.WellboreFlowName), noSubmissions);
        }

        /// <summary>A submission the flow planned from its ingestion tables, so an id it already uses can be refused for records.</summary>
        public static async Task<Guid> SeedPlannedSubmissionAsync(string cs)
        {
            var id = Guid.NewGuid();
            await using var osdu = SampleEstate.Context(cs);
            osdu.DeliverySubmissions.Add(new DeliverySubmission
            {
                SubmissionId = id,
                FlowId = FlowId.Of(SampleEstate.WellboreFlowName),
                FlowName = SampleEstate.WellboreFlowName,
                MappingReference = SampleEstate.WellboreMapping,
                RenderContext = "{}",
                ParametersJson = "{}",
                Kind = SubmissionKinds.Incremental,
                SourceConnection = "${env:OSDU_SAMPLE_DB}",
                SourceObject = "OsduSample.ing.Wellbore",
                Status = "completed",
                ReceivedUtc = DateTime.UtcNow,
            });
            await osdu.SaveChangesAsync();
            return id;
        }

        public async Task CleanupAsync(string cs)
        {
            await using (var osdu = SampleEstate.Context(cs))
            {
                var flows = new[] { SampleEstate.WellboreFlowName, NoSubmissionsFlow };
                await osdu.DeliverySubmissionLandings
                    .Where(l => osdu.DeliveryInlineSubmissions.Any(s => s.SubmissionId == l.SubmissionId && flows.Contains(s.FlowName)))
                    .ExecuteDeleteAsync();
                await osdu.DeliveryInlineSubmissions.Where(s => flows.Contains(s.FlowName)).ExecuteDeleteAsync();
                await osdu.DeliverySubmissions.Where(s => flows.Contains(s.FlowName)).ExecuteDeleteAsync();
                await osdu.DeliveryActivities.Where(a => flows.Contains(a.FlowName)).ExecuteDeleteAsync();
                await osdu.DeliveryMappings.Where(m => m.RepoId == RepoId).ExecuteDeleteAsync();
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.RunEvents.Where(e => e.RepoId == RepoId).ExecuteDeleteAsync();
                await db.Runs.Where(r => r.RepoId == RepoId).ExecuteDeleteAsync();
                await db.RunGroups.Where(g => g.RepoId == RepoId).ExecuteDeleteAsync();
                await db.FlowDependencies.Where(d => d.RepoId == RepoId).ExecuteDeleteAsync();
                await db.Pipelines.Where(p => p.RepoId == RepoId).ExecuteDeleteAsync();
                await db.Repos.Where(r => r.Id == RepoId).ExecuteDeleteAsync();
            }

            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // A transient lock on a run-log file must not fail the test; the temp directory is disposable.
            }
        }

        private static CatalogPipeline Pipeline(Guid repoId, string name, string kind, int wave, string yaml, string relativePath, DateTime now) => new()
        {
            Id = CatalogIdentity.Pipeline(repoId, name),
            RepoId = repoId,
            Name = name,
            Kind = kind,
            Batch = "recall",
            RelativePath = relativePath,
            ContentHash = new string('0', 64),
            Yaml = yaml,
            DefinitionJson = string.Create(CultureInfo.InvariantCulture, $$"""{"name":"{{name}}","flowKind":"{{kind}}"}"""),
            Active = true,
            Wave = wave,
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };
    }
}
