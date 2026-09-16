# Ingestion DAGs

## Contents

* [Introduction](#introduction)
* [Deployment](#deployment)
  * * [Google Cloud Composer](#gc-composer)
  * * [Installing Python Dependencies](#installing-python-dependencies)
* [DAG Implementation Details](#dag-implementation-details)
* [Required Variables](#required-variables)
  * * [Internal Services](#internal-services)
  * * [Configuration](#configuration)
  * * [Ingestion](#ingestion)
  * * [OSDU Python SDK](#osdu-python-sdk)
* [Testing](#testing)
  * * [Running E2E Tests](#running-e2e-tests)
  * * [Postman tests](#postman-tests)
* [Logging](#logging)
  * * [Logging Configuration](#logging-configuration)
  * * [Remote Logger](#remote-logger)
* [DAGs Description](#dags-description)
  * * [Osdu_ingest](#osdu_ingest)
  * * [Osdu_ingest R2](#osdu_ingest-r2)
* [Operators Description](#operators-description)
  * * [Workflow Status Operator](#workflow-status-operator)
  * * [Validate Manifest Schema Operator](#validate-manifest-schema-operator)
  * * [Ensure Manifest Integrity Operator](#ensure-manifest-integrity-operator)
  * * [Process Manifest Operator](#process-manifest-operator)
* [Backward compatibility](#backward-compatibility)
* [Licence](#licence)

## Introduction

The project is a set of Apache Airflow DAGs implementations to orchestrate data ingestion within OSDU platform.
The following DAGs are implemented:

* Osdu_ingest - R3 Manifest Ingestion DAG
* Osdu_ingest_r2 - R2 Manifest Ingestion DAG (deprecated)

## Deployment

### Google Cloud Composer

Google Cloud provides Cloud Composer a fully managed workflow orchestration service built on Apache Airflow.

To deploy the Ingestion DAGs on Google Cloud Composer just upload files from */src* folder into *DAGS_FOLDER* and *PLUGINS_FOLDER* accordingly into the DAG bucket that provided by Composer environment. [More info in documentation.](https://cloud.google.com/composer/docs/quickstart#uploading_the_dag_to)

*DAGS_FOLDER* and *PLUGINS_FOLDER* are setting up by Composer itself.

According to the [DAG implementation details](#dag-implementation-details) need to put [osdu_api] directory into the *DAGS_FOLDER*. Moreover, all required variables have to be set in Airflow meta store by Variables mechanism. [List of the required variables](#required-variables).

### Azure

To deploy the Ingestion DAGs to airflow, follow below steps.
* Identify the file share to which DAGs need to be copied. [This](https://community.opengroup.org/osdu/platform/deployment-and-operations/infra-azure-provisioning/-/blob/master/infra/templates/osdu-r3-mvp/service_resources/airflow.tf#L71) is the file share where DAGs for airflow reside.
* Copy contents of */src/dags* to *airflowdags/dags* folder
* Copy contents of */src/plugins/hooks* to *airflowdags/plugins/hooks*
* Copy contents of */src/plugins/operators* to *airflowdags/plugins/operators*

#### Installing Python Dependencies

Environment dependencies might be installed by several ways:

1. Install packages via `pip` on the environment where Airflow runs.
2. Although it is **not recommended**, it is possible to install Python libraries locally. Put your dependencies into *DAG_FOLDER/libs* directory. Airflow automatically adds *DAG_FOLDER* and *PLUGINS_FOLDER* to the *PATH*.

Python dependencies can be specified as extra pip packages in airflow deployment [here](https://community.opengroup.org/osdu/platform/deployment-and-operations/infra-azure-provisioning/-/blob/master/charts/airflow/helm-config.yaml#L211)

The DAGs require [Python SDK](https://community.opengroup.org/osdu/platform/system/sdks/common-python-sdk) to be installed.
It can be installed to the environment via `pip`:

```shell
pip install osdu-api --extra-index-url https://community.opengroup.org/api/v4/projects/148/packages/pypi/simple
```

Also, the DAGs require [osdu-airflow-lib](https://community.opengroup.org/osdu/platform/data-flow/ingestion/osdu-airflow-lib) package to be installed for common code (operators, osdu-airflow utils etc.).

```shell
pip install 'osdu-airflow' --extra-index-url=https://community.opengroup.org/api/v4/projects/668/packages/pypi/simple
```

#### Environment Variables & Airflow Variables

Add variables manually in the Airflow UI or through airflow helm charts. [List of the required variables](#required-variables).

Adding variables to helm charts can be found [here](https://community.opengroup.org/osdu/platform/deployment-and-operations/infra-azure-provisioning/-/blob/master/charts/airflow/helm-config.yaml#L157)

More details about airflow variables can be found [here](https://airflow.apache.org/docs/apache-airflow/1.10.12/concepts.html?highlight=airflow_var#variables)

## DAG Implementation Details

OSDU DAGs are cloud platform-agnostic by design. However, there are specific implementation requirements by cloud
platforms, and the OSDU R2 Prototype provides a dedicated Python SDK to make sure that DAGs are independent from the
cloud platforms. This Python SDK must be installed from the corresponding [repository](https://community.opengroup.org/osdu/platform/system/sdks/common-python-sdk).

## Required Variables

### Common naming convention

Some variables are defined using Airflow Variables.
Variable should has prefix which define where variable are used:
* **core__** - use in common part of DAGs;
* **gcp__**, **azure__**, **ibm__** - use in cloud-specific modules of DAGs;
* **sdk__** - pass to Python SDK.

If variable defines URL to internal services it should have suffix which show the completeness of the URL:
* **__url** - the variable should contain full path to service API endpoint;
* **__host** - the variable should contain just service host value. The full path to service API endpoint constructed inside operators.

### Internal Services

|Variable |Value Description|
|---|---|
| core__service__storage__url  | Storage Service API endpoint to save records |
| core__service__search__url |  Search Service API endpoint to search queries |
| core__service__workflow__host | Workflow Service host |
| core__service__workflow__url | (Deprecated) Workflow Service API endpoint to update status |
| core__service__file__host | File Service host |
| core__service__schema__url | Schema Service API endpoint to get schema by Id |

### Configuration

|Variable |Value Description|
|---|---|
| core__config__dataload_config_path| Path to dataload.ini file. Used in R2 manifest ingestion|

### Ingestion

|Variable |Value Description|
|---|---|
| core__ingestion__batch_save_enabled | If this value is set to `true`, then save the Manifest's entities in Storage Service by batches|
| core__ingestion__batch_save_size | Size of the batch of entities to save in Storage Service|

## Testing

### Running E2E Tests

~~~
tests/./set_airflow_env.sh
~~~

~~~
chmod +x tests/unit_tests.sh && tests/./unit_tests.sh
~~~

### Postman tests

[Postman collection](https://community.opengroup.org/osdu/qa/-/blob/main/Postman%20Collection/47_CICD_IngestionByReference/Ingestion%20By%20Reference%20CI-CD%20v3.0.postman_collection.json) requires manual update of manifest files. Sample files could be found in [QA repo](https://community.opengroup.org/osdu/qa/-/tree/main/Postman%20Collection/47_CICD_IngestionByReference). For **GC** and **anthos** this Pre-request scripts in `02 - Upload XXX file` in this collection generates manifest file with randomized record IDs that will be uploaded instead of local.

## Logging

### Logging Configuration

As Airflow initializes the logging configuration at startup, it implies that all logger configuration settings will be kept in `airflow.cfg` file within your project or will be specified as environment variables according to the [Airflow specification](https://airflow.apache.org/docs/apache-airflow/1.10.14/howto/set-config.html).

Look Airflow logging properties in the [documentation](https://airflow.apache.org/docs/apache-airflow/1.10.14/howto/write-logs.html)

### Remote Logger

For Google Cloud Composer `google_cloud_default` connection is used by default to store all logs into GCS bucket.
Look Airflow [documentation](https://airflow.apache.org/docs/apache-airflow/1.10.14/howto/write-logs.html) documentation for Azure.

## DAGs Description

### Osdu_ingest

R3 manifest processing with providing integrity.
The DAG can process batch of manifests or single manifest. The logic to manage of direction is implemented inside the DAG by `check_payload_type` task.

### Osdu_ingest R2

**Note:** The DAG is deprecated.

## Operators Description

### Workflow Status Operator

The Workflow Status Operator is an Airflow operator callable from each DAG. It's purpose is to receive the latest status
of a workflow job and then update the workflow record in the database. Each DAG in the system has to invoke the Workflow
Status Operator to update the workflow status.

This operator isn't designed to directly update the status in the database, and it queries the OSDU Workflow
service's API endpoint. Once the operator sends a request to update status, it cedes control back to the DAG.

### Validate Manifest Schema Operator

The Workflow Status Operator is an Airflow operator to process manifest against the schemas definitions from Schema Service.
The operator's logic is split by two steps:

1. Common schema validation (without references check)
2. Ensure manifest schema integrity (all references are resolving and validates against its schemas)

**Note:** All invalid values will be evicted from the manifest and logged.

The operator output is a new manifest with only valid manifest data (stores in Airflow XCOM).

```json
{"manifest": <OUTPUT>}
```

### Ensure Manifest Integrity Operator

Operator to validate ref inside manifest R3 and remove invalid entities.
This operator is responsible for the parent - child validation.
All orphan-like entity will be logged and excluded from the validated manifest .

The operator output is a new manifest with only valid manifest data (stores in Airflow XCOM).

```json
{"manifest": <OUTPUT>}
```

### Process Manifest Operator

Operator to process manifest (single manifest file or a list of them). The processing includes validation against schemas, storing records etc.

The operator output is a set of ingested records ids (stores in Airflow XCOM).

```json
{"record_ids": [<SET_OF_INGESTED_RECORDS>]}
```


## Backward compatibility

At the current moment, Ingestion DAGs can work with Airflow 2.x and >=1.10.10 with [osdu-airflow-lib](https://community.opengroup.org/osdu/platform/data-flow/ingestion/osdu-airflow-lib) package installed.

To avoid incompatibilities a few code changes must be introduced.

Use `osdu_airflow.backward_compatibility.airflow_utils:apply_default` instead of `airflow.utils.apply_default` in operators.

Example:

```python
from osdu_airflow.backward_compatibility.airflow_utils import apply_defaults
...

class SomeOperator(BaseOperator):

    @apply_defaults
    def __init__(self, *args, **kwargs):
        ...
```

Also, do not pass `provide_contex=True` to tasks directly. Use `libs.airflow.backward_compatibility.default_args:update_default_args` instead.

```python
from osdu_airflow.backward_compatibility.default_args import update_default_args

default_args = {
    "start_date": airflow.utils.dates.days_ago(0),
    "retries": 0,
    "retry_delay": timedelta(seconds=30),
    "trigger_rule": "none_failed",
}

default_args = update_default_args(default_args)
```

## Licence

Copyright © Google LLC
Copyright © EPAM Systems

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

[http://www.apache.org/licenses/LICENSE-2.0](http://www.apache.org/licenses/LICENSE-2.0)

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
