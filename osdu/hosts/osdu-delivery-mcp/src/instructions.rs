//! The delivery module's section of the server instructions: what a client reads at `initialize`, after SQLFlow's own.
//!
//! It is written for a model choosing a tool: the vocabulary the tools share, which tool answers which question, how
//! an OSDU flow is run, and what this server does not do. Each tool's own description says the rest.

/// The section. Every tool, doc id and flow kind it names is checked against what the server really has by the tests
/// in `tests/server.rs`, so a rename cannot leave the instructions pointing at nothing.
pub const INSTRUCTIONS: &str = "\
OSDU DELIVERY (the delivery_* tools). METADATA ONLY.
This estate publishes records into an OSDU platform: a pre flow lands source files, an ing flow loads keyed ingestion
tables, and a delivery flow renders each row through a mapping (checked against a saved template, resolving ids from
the partition's cache) and delivers it. Every record is tracked in the ledger. The delivery_* tools read that ledger
and the module's definitions. No tool here returns record content, source rows, cached values or dimension values,
and SQLFlow's tools that read rows are not offered. When a question needs the data itself, say this server does not
expose it and give the GUI link the result carries.

Vocabulary:
- record: one deliverable, addressed by flowId (the LEDGER's flow id, never a pipeline id) plus deliveryKey; both are
  on every hit of delivery_find_records and every row of delivery_flow_records. Custody states: pending, delivering,
  delivered, held (stopped until released), failed, deleted, waiting (for another record to land first).
- interface: one OSDU type of a source that delivers several. Pass `interface` when a tool asks which.
- partition: an OSDU data-partition-id, the `partition` argument everywhere. delivery_partitions lists them.
- submission: one plan a run made over the ingestion tables. activity: one entry of the audit trail.

Which tool:
- One record ('was X delivered', 'why did it fail', 'which file is it from', 'who deleted it'): delivery_find_records,
  then delivery_record.
- One flow ('how is it doing', 'what failed', 'where does it deliver'): list_pipelines(kind=\"delivery\"), then
  delivery_flow, then delivery_flow_records(status=...).
- One run ('what did last night's run do'): list_runs and get_run for the run, delivery_submissions for what it planned
  and delivered, delivery_flow_records(runId=...) for its records.
- Who did what, and when: delivery_activities.
- Environments: delivery_partitions, then delivery_config for where a flow really delivers.
- The cache: delivery_caches(partition) for its health, delivery_cache_versions for its history,
  delivery_cache_changes for what awaits approval.
- Mappings and schemas: delivery_mappings, delivery_templates, delivery_osdu_schemas, delivery_osdu_schema_compare.
  Writing a mapping: delivery_scaffold_mapping, then delivery_check_mapping until valid, then propose_pipelines.
- Tests and dimensions: delivery_assertions, delivery_assertion_runs, delivery_dimensions. Retrievals: delivery_retrievals.
- Documentation: search_docs covers this product too (its page ids start with delivery-), and validate_flow,
  list_flow_keys and describe_flow_key know flowType delivery, retrieval, cache, assertion and dimension.

Running an OSDU flow: trigger_run with the kind's own arguments, never fullLoad or a backfill window.
- operation: delivery takes deliver (default), plan, intake, drain, verify, sync; cache takes refresh (default), plan;
  retrieval takes retrieve (default), plan; assertion takes test (default), plan; dimension takes build (default), plan.
  plan changes nothing and is the safe first step.
- values: the flow's parameters by name (delivery_flow lists them), and partition for a flow that names its partitions.
- payload: for delivery, force, submissionId, recordKeys, redeliver, interface, interfaces; for assertion, tests and
  tags; for dimension, dimensions. get_doc(\"delivery-cli-the-run-options\") has the full table.

Operator actions (operate scope; each is recorded in the audit trail under the caller's name): delivery_probe_target,
delivery_verify_record, delivery_sync_with_source, delivery_release_records, delivery_redeliver_record,
delivery_decide_cache_changes. Use one only when the person asked for that action. Removing records from OSDU is not
offered here: it is done in the GUI, where the confirmation shows what it takes away.";
