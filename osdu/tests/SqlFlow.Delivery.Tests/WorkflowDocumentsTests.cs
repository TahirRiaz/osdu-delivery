using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The Stage 6 routes as a flow document declares them (docs/interfaces-design.md sections 5.2 and 5.5 to 5.9): the
/// composed routes a record's files and bulk data resolve to, the dataset route, manifests by reference, and the workflow
/// route with its stages, templates, inputs, results, secrets and Airflow, each checked while the document is read.
/// </summary>
public sealed class WorkflowDocumentsTests
{
    private readonly DeliveryDocumentLoader _loader = new();

    private static string Source(string interfaces, string target = "") => ($$"""
        flowType: delivery
        name: estate
        source:
          connection: ${env:ESTATE_DB}
          work: ../.work/estate
        target:
          endpoint: ${env:OSDU_URL}
          headers:
            data-partition-id: opendes
          protocolOptions:
            ddmsRoot: /api/os-wellbore-ddms
        {{target}}
        interfaces:
        {{interfaces}}
        """).ReplaceLineEndings("\n");

    private static string Single(string target, string payloads = "") => ($$"""
        flowType: delivery
        name: csv-wells
        source:
          connection: ${env:ESTATE_DB}
          work: ../.work/csv
          record: { object: Estate.ing.CsvFile, key: [file_id] }
        {{payloads}}
        render:
          mapping: CsvDescriptor@1.0.0
        target:
          endpoint: ${env:OSDU_URL}
          headers:
            data-partition-id: opendes
        {{target}}
        """).ReplaceLineEndings("\n");

    private const string CsvWorkflow = """
        workflow:
              anchorTag: osduDeliveryAnchor
              stages:
                - workflow: csv_ingestion
                  context:
                    id: "{record:id}"
                    dataPartitionId: "{partition}"
                    data_service_to_use: file
              results:
                search: { kind: "osdu:wks:master-data--Well:1.*.*", query: "tags.osduDeliveryAnchor:\"{anchorTag}\"" }
                minimum: 1
                waitSeconds: 300
        """;

    [Fact]
    public void Files_with_bulk_data_and_a_manifest_with_bulk_data_resolve_to_the_composed_routes()
    {
        var source = _loader.ParseSource(Source("""
              logs:
                record: { object: Estate.ing.WellLog, key: [log_id] }
                files: { root: ../data/las, locationColumn: las_folder, hashColumn: las_hash }
                bulk: { root: ../data/curves, locationColumn: curve_folder, hashColumn: curve_hash }
                mapping: WellLog@1.4.0
              trajectories:
                record: { object: Estate.ing.Survey, key: [survey_id] }
                bulk: { root: ../data/stations, locationColumn: station_folder, hashColumn: station_hash }
                mapping: WellboreTrajectory@1.3.0
                route: manifest
              named:
                record: { object: Estate.ing.WellLog2, key: [log_id] }
                files: { root: ../data/las, locationColumn: las_folder, hashColumn: las_hash }
                bulk: { root: ../data/curves, locationColumn: curve_folder, hashColumn: curve_hash }
                mapping: WellLog@1.4.0
                route: ddms
            """), "estate.yaml");

        var logs = source.Interface("logs");
        Assert.Equal(DeliveryProtocol.OsduFileAndDdms, logs.Target.Protocol);
        Assert.Null(logs.Target.ProtocolOptions.Payload);
        Assert.Equal(["files", "bulk"], PayloadParts.Of(logs)!.Select(p => p.Payload));
        Assert.Equal(["files", "bulk"], PayloadParts.Roles(logs));
        Assert.Contains("declares files and bulk", logs.RouteReason, StringComparison.Ordinal);
        Assert.Equal("fileAndDdms", RouteChecks.Name(logs.Target.Protocol));

        var trajectories = source.Interface("trajectories");
        Assert.Equal(DeliveryProtocol.OsduManifestAndDdms, trajectories.Target.Protocol);
        Assert.Equal(["bulk"], PayloadParts.Of(trajectories)!.Select(p => p.Payload));
        Assert.Contains("route names manifest and the interface declares bulk", trajectories.RouteReason, StringComparison.Ordinal);
        Assert.Equal("/api/os-wellbore-ddms", trajectories.Target.ProtocolOptions.DdmsRoot);

        var named = source.Interface("named");
        Assert.Equal(DeliveryProtocol.OsduFileAndDdms, named.Target.Protocol);
        Assert.Contains("route names ddms and the interface declares both files and bulk", named.RouteReason, StringComparison.Ordinal);

        // Redelivery names the parts each route sends.
        Assert.Equal(Ledger.RedeliverScope.Payload, RedeliverScopes.Of("bulk", logs).Scope);
        Assert.Equal(["bulk"], RedeliverScopes.Of("bulk", logs).Parts);
        Assert.Equal(["files"], RedeliverScopes.Of("files", logs).Parts);
        Assert.Empty(RedeliverScopes.Of("payload", logs).Parts);
        Assert.Equal(Ledger.RedeliverScope.Metadata, RedeliverScopes.Of("record", logs).Scope);
        Assert.Contains(
            "which sends the record, its bulk data, so a redelivery of 'files' has nothing to send",
            Assert.Throws<DeliveryException>(() => RedeliverScopes.Of("files", trajectories)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_dataset_route_takes_its_files_through_the_dataset_service()
    {
        var source = _loader.ParseSource(Source("""
              segy:
                record: { object: Estate.ing.SegyCollection, key: [survey] }
                files: { root: ../data/segy, locationColumn: segy_folder, hashColumn: segy_hash }
                mapping: FileCollectionSegy@1.0.0
                route: dataset
                protocolOptions: { datasetKind: "osdu:wks:dataset--FileCollection.Generic:1.1.0" }
            """), "estate.yaml");

        var segy = source.Interface("segy");
        Assert.Equal(DeliveryProtocol.OsduDataset, segy.Target.Protocol);
        Assert.Equal("files", segy.Target.ProtocolOptions.Payload);
        Assert.Equal("dataset", RouteChecks.Name(segy.Target.Protocol));
        Assert.Null(PayloadParts.Of(segy));
        Assert.Null(segy.Target.ProtocolOptions.DdmsRoot);
        Assert.Equal(["all", "record", "files", "metadata", "payload"], RedeliverScopes.For(segy));

        // A record of a dataset kind is the dataset; any other needs a dataset kind to register its files as.
        RouteChecks.Check(segy, "osdu:wks:dataset--FileCollection.SEGY:1.0.0");
        RouteChecks.Check(segy, "osdu:wks:work-product-component--SeismicTraceData:1.3.0");
        var generic = segy with { Target = segy.Target with { ProtocolOptions = segy.Target.ProtocolOptions with { DatasetKind = "osdu:wks:master-data--Well:1.0.0" } } };
        Assert.Contains("is not a dataset kind", Assert.Throws<DeliveryException>(() => RouteChecks.Check(generic, "osdu:wks:work-product-component--SeismicTraceData:1.3.0")).Message, StringComparison.Ordinal);

        // The file route mints its own dataset ids, so it cannot deliver a dataset.
        var file = segy with { Target = segy.Target with { Protocol = DeliveryProtocol.OsduFile } };
        Assert.Contains("Deliver a dataset with its files by the dataset route", Assert.Throws<DeliveryException>(() => RouteChecks.Check(file, "osdu:wks:dataset--File.Generic:1.0.0")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Files_go_as_their_own_type_beside_bulk_data_and_a_route_without_files_refuses_one()
    {
        var source = _loader.ParseSource(Source("""
              logs:
                record: { object: Estate.ing.WellLog, key: [log_id] }
                files: { root: ../data/las, locationColumn: las_folder, hashColumn: las_hash }
                bulk: { root: ../data/curves, locationColumn: curve_folder, hashColumn: curve_hash }
                mapping: WellLog@1.4.0
              named:
                record: { object: Estate.ing.WellLog2, key: [log_id] }
                files: { root: ../data/las, locationColumn: las_folder, hashColumn: las_hash }
                bulk: { root: ../data/curves, locationColumn: curve_folder, hashColumn: curve_hash }
                mapping: WellLog@1.4.0
                protocolOptions: { filesContentType: ' text/plain; charset=utf-8 ' }
              scans:
                record: { object: Estate.ing.Scan, key: [scan_id] }
                files: { root: ../data/scans, locationColumn: scan_folder, hashColumn: scan_hash }
                mapping: Document@1.0.0
                route: file
                protocolOptions: { payloadContentType: application/pdf }
            """), "estate.yaml");

        // Beside bulk data, files go as bytes unless the flow names their type; the bulk data keeps the payload's.
        var logs = source.Interface("logs").Target.ProtocolOptions;
        Assert.Null(logs.FilesContentType);
        Assert.Equal(ProtocolOptions.DefaultFilesContentType, logs.ForFiles(besideBulk: true).PayloadContentType);
        Assert.Equal("application/x-parquet", logs.PayloadContentType);
        var named = source.Interface("named").Target.ProtocolOptions;
        Assert.Equal("text/plain; charset=utf-8", named.FilesContentType);
        Assert.Equal("text/plain; charset=utf-8", named.ForFiles(besideBulk: true).PayloadContentType);
        Assert.Equal("application/x-parquet", named.PayloadContentType);

        // Where the files are the payload, the payload's type is theirs.
        var scans = source.Interface("scans").Target.ProtocolOptions;
        Assert.Equal("application/pdf", scans.ForFiles(besideBulk: false).PayloadContentType);

        var bulkOnly = Assert.Throws<FlowValidationException>(() => _loader.ParseSource(Source("""
              logs:
                record: { object: Estate.ing.WellLog, key: [log_id] }
                bulk: { root: ../data/curves, locationColumn: curve_folder, hashColumn: curve_hash }
                mapping: WellLog@1.4.0
                protocolOptions: { filesContentType: text/plain }
            """), "estate.yaml"));
        Assert.Contains("filesContentType of interface 'logs' says what type a record's files are uploaded as, and the flow's route (ddms) uploads no files", bulkOnly.Message, StringComparison.Ordinal);

        foreach (var (key, value) in new[] { ("filesContentType", "plain"), ("payloadContentType", "application/") })
        {
            var malformed = Assert.Throws<FlowValidationException>(() => _loader.ParseSource(Source($$"""
                  scans:
                    record: { object: Estate.ing.Scan, key: [scan_id] }
                    files: { root: ../data/scans, locationColumn: scan_folder, hashColumn: scan_hash }
                    mapping: Document@1.0.0
                    route: file
                    protocolOptions: { {{key}}: '{{value}}' }
                """), "estate.yaml"));
            Assert.Contains($"{key} of interface 'scans' '{value}' is not a media type", malformed.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_manifest_goes_by_reference_as_the_flow_says()
    {
        var source = _loader.ParseSource(Source("""
              wells:
                record: { object: Estate.ing.Well, key: [uwi] }
                mapping: Well@1.2.0
                route: manifest
                protocolOptions: { manifestByReference: auto, manifestInlineLimitKb: 5000, byReferenceWorkflowName: Osdu_ingest_by_reference_v2 }
            """), "estate.yaml");
        var wells = source.Interface("wells");
        Assert.Equal(ManifestReference.Auto, wells.Target.ProtocolOptions.ManifestByReference);
        Assert.Equal(5000, wells.Target.ProtocolOptions.ManifestInlineLimitKb);
        Assert.Equal("Osdu_ingest_by_reference_v2", wells.Target.ProtocolOptions.ByReferenceWorkflowName);

        var refused = Assert.Throws<FlowValidationException>(() => _loader.ParseSource(Source("""
              wells:
                record: { object: Estate.ing.Well, key: [uwi] }
                mapping: Well@1.2.0
                protocolOptions: { manifestByReference: always }
            """), "estate.yaml"));
        Assert.Contains("manifestByReference", refused.Message, StringComparison.Ordinal);
        Assert.Contains("the flow's route (storage) sends none", refused.Message, StringComparison.Ordinal);

        var limit = Assert.Throws<FlowValidationException>(() => _loader.ParseSource(Source("""
              wells:
                record: { object: Estate.ing.Well, key: [uwi] }
                mapping: Well@1.2.0
                route: manifest
                protocolOptions: { manifestByReference: auto, manifestInlineLimitKb: 0 }
            """), "estate.yaml"));
        Assert.Contains("manifestInlineLimitKb", limit.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_workflow_interface_is_read_with_its_stages_results_and_anchor()
    {
        var source = _loader.ParseSource(Source($$"""
              csv:
                record: { object: Estate.ing.CsvFile, key: [file_id] }
                files: { root: ../data/csv, locationColumn: csv_folder, hashColumn: csv_hash }
                mapping: CsvDescriptor@1.0.0
                {{CsvWorkflow}}
            """), "estate.yaml");

        var csv = source.Interface("csv");
        Assert.Equal(DeliveryProtocol.OsduWorkflow, csv.Target.Protocol);
        Assert.Contains("declares a workflow, so it goes by the workflow route", csv.RouteReason, StringComparison.Ordinal);
        Assert.Contains("csv_ingestion", csv.RouteReason, StringComparison.Ordinal);
        var workflow = csv.Target.Workflow!;
        Assert.Equal(WorkflowAnchor.Dataset, workflow.Anchor);
        Assert.Equal("osduDeliveryAnchor", workflow.AnchorTagKey);
        Assert.Equal(WorkflowRunWhen.Changed, workflow.RunWhen);
        var stage = Assert.Single(workflow.Stages);
        Assert.Equal("csv_ingestion", stage.Workflow);
        Assert.Equal("{record:id}", stage.Context["id"]!.GetValue<string>());
        Assert.Equal(WorkflowResultStrategy.Search, workflow.Results.Strategy);
        Assert.Equal("osdu:wks:master-data--Well:1.*.*", workflow.Results.Kind);
        Assert.Equal(300, workflow.Results.WaitSeconds);
        Assert.Equal(["files"], PayloadParts.Of(csv)!.Select(p => p.Payload));
        Assert.Equal(["files", "workflow"], PayloadParts.Roles(csv));
        Assert.Equal(["all", "record", "files", "workflow", "metadata", "payload"], RedeliverScopes.For(csv));
        Assert.Equal(["workflow"], RedeliverScopes.Of("workflow", csv).Parts);
        Assert.Null(csv.Target.ProtocolOptions.DdmsRoot);

        // The anchor is a dataset, so its kind is checked when the mapping is known.
        RouteChecks.Check(csv, "osdu:wks:dataset--File.Generic:1.0.0");
        Assert.Contains("is not a dataset, and the workflow is anchored on a dataset", Assert.Throws<DeliveryException>(() => RouteChecks.Check(csv, "osdu:wks:master-data--Well:1.0.0")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_workflow_in_the_single_form_reads_its_inputs_secrets_and_airflow()
    {
        var flow = _loader.ParseFlow(Single("""
              protocol: workflow
              airflow:
                endpoint: ${env:AIRFLOW_URL}
                apiVersion: v2
                auth: { type: basic, secretRef: "${keyvault:airflow-password}", secondarySecretRef: "${keyvault:airflow-user}" }
              workflow:
                anchor: dataset
                inputs:
                  h5: { root: ../data/h5, locationColumn: h5_folder, hashColumn: h5_hash, optional: true }
                secrets:
                  token: ${keyvault:sd-token}
                runWhen: created
                stages:
                  - workflow: Energyml_Converter
                    timeoutMinutes: 120
                    context:
                      dataset_xml: ["{record:id}"]
                      dataset_h5: "{input:h5}"
                      data_partition_id: "{partition}"
                      tags_every_entity_keys: ["{anchorTag}"]
                    outputs:
                      manifestId: { xcom: { task: update_status_finished_task, key: saved_record_ids, match: dataset--File.Generic } }
                  - workflow: Osdu_ingest_by_reference
                    context:
                      manifest: "{stage:1.manifestId}"
                results:
                  search: { kind: "*:*:*--*:*.*.*", query: "data.Tags:\"{anchorTag}\"" }
            """, """
              payloads:
                files: { root: ../data/epc, locationColumn: epc_folder, hashColumn: epc_hash }
            """), "energistics.yaml");

        Assert.Equal(DeliveryProtocol.OsduWorkflow, flow.Target.Protocol);
        var workflow = flow.Target.Workflow!;
        Assert.Equal(WorkflowRunWhen.Created, workflow.RunWhen);
        var input = Assert.Single(workflow.Inputs);
        Assert.Equal(new WorkflowInput("h5", "osdu:wks:dataset--File.Generic:1.0.0", true), input);
        Assert.Equal("../data/h5", flow.Source.Payloads["h5"].Root);
        Assert.Equal(["files", "h5"], PayloadParts.Of(flow)!.Select(p => p.Payload));
        Assert.True(PayloadParts.Of(flow)![1].Optional);
        Assert.Equal("${keyvault:sd-token}", workflow.Secrets["token"]);
        Assert.Equal(2, workflow.Stages.Count);
        Assert.Equal(120, workflow.Stages[0].TimeoutMinutes);
        var output = workflow.Stages[0].Outputs["manifestId"];
        Assert.Equal("update_status_finished_task", output.XComTask);
        Assert.Equal("saved_record_ids", output.XComKey);
        Assert.Equal("dataset--File.Generic", output.Match);
        Assert.Equal(AirflowApiVersion.V2, flow.Target.Airflow!.ApiVersion);
        Assert.Equal(TargetAuthType.Basic, flow.Target.Airflow.Auth.Type);

        // Without Airflow, an XCom output is read through the Workflow service's latestInfo, which serves the run's latest task.
        var latestInfo = _loader.ParseFlow(Single("""
              protocol: workflow
              workflow:
                stages:
                  - workflow: Energyml_Converter
                    context:
                      dataset_xml: ["{record:id}"]
                      dataset_h5: []
                    outputs:
                      manifestId: { xcom: { task: update_status_finished_task, key: saved_record_ids } }
                  - workflow: Osdu_ingest_by_reference
                    context:
                      manifest: "{stage:1.manifestId}"
            """, """
              payloads:
                files: { root: ../data/epc, locationColumn: epc_folder, hashColumn: epc_hash }
            """), "energistics.yaml");
        Assert.Null(latestInfo.Target.Airflow);
        Assert.Equal(WorkflowAnchor.Dataset, latestInfo.Target.Workflow!.Anchor);
        Assert.Equal("update_status_finished_task", latestInfo.Target.Workflow.Stages[0].Outputs["manifestId"].XComTask);
        Assert.Null(latestInfo.Target.Workflow.Stages[0].Outputs["manifestId"].Match);
    }

    public static TheoryData<string, string> RefusedWorkflows => new()
    {
        // A credential is a reference, never a value in the document.
        {
            """
              workflow:
                anchor: storage
                stages:
                  - workflow: segy-to-zgy-conversion
                    context:
                      data_partition_id: "{partition}"
                      filecollection_segy_id: "{record:data.Datasets[0]|ref}"
                      work_product_id: "{record:data.WorkProductID}"
                      sd_svc_api_key: plain-text-key
                      storage_svc_api_key: "{secret:storageKey}"
                secrets:
                  storageKey: ${env:STORAGE_KEY}
            """,
            "'sd_svc_api_key' is a credential (osdu/specs/workflows/INTEGRATION.md section 3.10); give it as {secret:name}"
        },
        {
            """
              workflow:
                anchor: storage
                stages:
                  - workflow: eds_ingest
                    context: { connectedSourceDataJobId: "{record:id}" }
                secrets:
                  key: plain
            """,
            "target.workflow.secrets.key must be a whole secret reference"
        },
        // What a contract requires is in the context.
        {
            """
              workflow:
                stages:
                  - workflow: csv_ingestion
                    context: { dataPartitionId: "{partition}" }
            """,
            "the csvParser contract requires 'id' (osdu/specs/workflows/INTEGRATION.md section 3.5), and the context does not set it"
        },
        {
            """
              workflow:
                stages:
                  - workflow: csv_ingestion
                    context: { id: "{record:id}", dataPartitionId: "{partition}", data_service_to_use: blob }
            """,
            "'data_service_to_use' is 'blob', and osdu/specs/workflows/INTEGRATION.md section 3.5 allows file or dataset"
        },
        {
            """
              workflow:
                anchor: storage
                stages:
                  - workflow: segy_to_mdio_conversion
                    context:
                      work_product_id: "{record:id}"
                      filecollection_segy_id: "{record:data.Datasets[0]}"
                      mdio_sd_path: sd://opendes/mdio/out
                      client_id: "{secret:clientId}"
                secrets:
                  clientId: ${env:MDIO_CLIENT}
            """,
            "the segyToMdio contract takes all of client_id, client_secret, refresh_token, refresh_url or none of them"
        },
        {
            """
              workflow:
                anchor: storage
                stages:
                  - workflow: manifest_ingestion
                    context: {}
            """,
            "manifest_ingestion is the Workflow service's test DAG"
        },
        {
            """
              workflow:
                stages:
                  - workflow: Enyparser_Translation
                    context: { source_dataset_ids: ["{record:id}"] }
            """,
            "runs Enyparser_Translation, which only translates and ingests nothing"
        },
        {
            """
              workflow:
                stages:
                  - workflow: my_dag
                    contract: nothing
                    context: {}
            """,
            "stages[0].contract 'nothing' is not a workflow OSDU Delivery knows"
        },
        // Placeholders name what the route has.
        {
            """
              workflow:
                stages:
                  - workflow: my_dag
                    context: { a: "{stage:1.out}" }
            """,
            "reads stage 1, and only the stages before it (none) have run by then"
        },
        {
            """
              workflow:
                stages:
                  - workflow: my_dag
                    context: { a: "{input:h5}" }
            """,
            "names the input 'h5', and the route registers files"
        },
        {
            """
              workflow:
                stages:
                  - workflow: my_dag
                    context: { a: "{secret:missing}" }
            """,
            "names the secret 'missing', which target.workflow.secrets does not declare"
        },
        {
            """
              workflow:
                stages:
                  - workflow: my_dag
                    context: {}
                results:
                  ids: "{record:id}"
                  search: { kind: "*:*:*:*" }
            """,
            "target.workflow.results names ids, search; the results are found one way"
        },
        {
            """
              workflow:
                stages:
                  - workflow: my_dag
                    context: {}
                results:
                  minimum: 2
            """,
            "results.minimum asks for records, and the results say no way to find them"
        },
        {
            """
              workflow:
                anchor: dataset
                stages:
                  - workflow: my dag
                    context: {}
            """,
            "stages[0].workflow 'my dag' is not a workflow name"
        },
        {
            """
              workflow:
                stages: []
            """,
            "target.workflow.stages lists no stage"
        },
    };

    [Theory]
    [MemberData(nameof(RefusedWorkflows))]
    public void A_workflow_the_document_shows_cannot_run_is_refused(string target, string expected)
    {
        var yaml = Single("  protocol: workflow\n" + target, """
              payloads:
                files: { root: ../data/csv, locationColumn: csv_folder, hashColumn: csv_hash }
            """);
        var refused = Assert.Throws<FlowValidationException>(() => _loader.ParseFlow(yaml, "csv.yaml"));
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_workflow_on_another_route_or_a_route_without_one_is_refused()
    {
        var elsewhere = Assert.Throws<FlowValidationException>(() => _loader.ParseFlow(Single("""
              protocol: storage
              workflow:
                stages:
                  - workflow: eds_scheduler
            """), "wells.yaml"));
        Assert.Contains("target.workflow declares a workflow, which only the workflow route runs", elsewhere.Message, StringComparison.Ordinal);

        var none = Assert.Throws<FlowValidationException>(() => _loader.ParseFlow(Single("  protocol: workflow"), "wells.yaml"));
        Assert.Contains("the workflow route runs the workflow target.workflow declares, and the document declares none", none.Message, StringComparison.Ordinal);

        var sourceLevel = Assert.Throws<FlowValidationException>(() => _loader.ParseSource(Source(
            """
              wells:
                record: { object: Estate.ing.Well, key: [uwi] }
                mapping: Well@1.2.0
            """,
            """
              workflow:
                stages:
                  - workflow: eds_scheduler
            """), "estate.yaml"));
        Assert.Contains("target.workflow (the workflow an interface runs is interfaces.<name>.workflow)", sourceLevel.Message, StringComparison.Ordinal);

        var airflow = Assert.Throws<FlowValidationException>(() => _loader.ParseFlow(Single("""
              protocol: storage
              airflow: { endpoint: "https://airflow.example.com" }
            """), "wells.yaml"));
        Assert.Contains("target.airflow says where the Airflow behind the Workflow service is, which only the workflow route reads", airflow.Message, StringComparison.Ordinal);

        var anchor = Assert.Throws<FlowValidationException>(() => _loader.ParseFlow(Single("""
              protocol: workflow
              workflow:
                anchor: dataset
                stages:
                  - workflow: eds_scheduler
            """), "wells.yaml"));
        Assert.Contains("target.workflow.anchor is dataset, which registers each record with its files through the dataset service, and source.payloads.files declares none", anchor.Message, StringComparison.Ordinal);

        var input = Assert.Throws<FlowValidationException>(() => _loader.ParseFlow(Single(
            """
              protocol: workflow
              workflow:
                anchor: storage
                inputs:
                  files: { root: ../x, locationColumn: c, hashColumn: h }
                stages:
                  - workflow: eds_scheduler
            """), "wells.yaml"));
        Assert.Contains("target.workflow.inputs.files takes a name the document already gives a payload set", input.Message, StringComparison.Ordinal);
    }
}
