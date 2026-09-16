# Core External Data Workflow

This repository contains Airflow DAGs for managing the ingestion, naturalization, and scheduling of external data into the OSDU platform.

## DAGs

### EDS Ingest DAG (`eds_ingest`)

Handles fetching data from an external connected source and ingesting it into the OSDU platform.

**Trigger**

- Manual via Workflow API.
- Manual via Airflow UI.
- Queued by EDS Scheduler DAG.

**Task Flow**

- `fetch_client` → `send_email_notification`

**High-Level Flow**

1. Receives an `execution_context` object as a DAG parameter.
2. Fetches records from the external source defined in the execution context.
3. Modifies records with the appropriate ACL and legal tags.
4. Ingests the records into the OSDU platform.
5. Pushes run results (ingested/failed record IDs, error details, manifest DAG run ID) to XCom.
6. Sends an email notification summarising the ingestion outcome.

**XCom Output**

- Manifest DAG Run ID.
- Total fetched, ingested, and failed record counts and ID lists.
- Referential integrity, schema validation, and ACL/legal tag errors.
- EDS Naturalization DAG Run ID.

---

### EDS Naturalization DAG (`eds_naturalization`)

Handles naturalization of Work Product Component (WPC) datasets in parallel.

**Trigger**

- Manual via Workflow API.
- Manual via Airflow UI.
- Triggered by EDS Ingest DAG.

**Task Flow**

- `generate_batches` → `Naturalization_0-3` (parallel) → `update_wpc_record` → `send_email_notification`

**High-Level Flow**

1. Receives an `execution_context` object as a DAG parameter containing WPC IDs with or without dataset paths.
2. Divides the input into 4 parallel batches for concurrent processing.
3. Each batch task naturalizes its assigned WPC records and stores results in XCom.
4. After all batches complete, aggregates results and updates WPC records in OSDU storage.
5. Sends an email notification summarising successful and failed naturalizations per WPC, including dataset counts and total file size.

---

### EDS Scheduler DAG (`eds_scheduler`)

Orchestrates scheduled execution of the EDS Ingest DAG and sends activity report emails.

**Trigger**

- Configurable schedule interval (read dynamically from `AirflowUtility.scheduler_interval()`)

**Task Flow**

- `trigger_eds_ingest` → `send_email_notification`

**High-Level Flow**

- Queries ConnectedSourceDataJob (CSDJ) records and triggers `eds_ingest` DAG runs for each job with `ActiveIndicator` set to `True`.
- Cleans up scheduled data jobs that no longer exist or are inactive.
- Sends an activity report email covering the Connected Source Registry Entry (CSRE) records.
