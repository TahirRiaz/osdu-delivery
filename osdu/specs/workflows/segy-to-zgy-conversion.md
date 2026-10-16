# SEGY to ZGY conversion

The SEGY to ZGY conversion is one step of the ingestion workflow. This conversion step is available as [Airflow DAG](https://airflow.apache.org/docs/apache-airflow/stable/concepts.html#dags) integrated with the Workflow Service. The DAG provided in this project calls a KubernetesPodOperator, which runs the container that is provided [here](https://community.opengroup.org/osdu/platform/data-flow/ingestion/segy-to-zgy-conversion/container_registry).

## FAQ

### Storage provider support

- Q: Does the converter support my cloud/storage provider: AWS, Azure, Google Cloud...?
- A: Not directly. The converter supports reading and writing seismic data via the Seismic DMS service and its client library (SDAPI). SDAPI supports AWS, Azure, Google Cloud at the time of writing this document, and can be extended for additional storage providers.

### Missing features

- Q: Is supporting {my provider, use case, new feature...} on your roadmap?
- A: Please start a conversation about your use case by opening an issue.

### Missing documentation

- Q: The documentation does not cover {important detail}
- A: Please open a merge request if you believe you are able to fill the documentation gap, or an issue if you need assistance.

## Prerequisites

Assembling all prerequisites for a successful conversion requires several steps, and may seem complicated at first. Read [testing instructions](doc/testing.md) carefully for detailed instructions.

## Registering DAG

#### Requirements

The [Airflow DAG](/airflow/workflow-svc-v2/segy_to_zgy_ingestion_dag.py) has dependencies from [osdu-airflow-lib](https://community.opengroup.org/osdu/platform/data-flow/ingestion/osdu-airflow-lib) package for common operators and backward compatibility.

Install it in Airflow Environment:

```shell
pip install 'osdu-airflow~=0.30.0' --extra-index-url=https://community.opengroup.org/api/v4/projects/668/packages/pypi/simple
```

#### For Azure this step can be skipped

The DAG must be registered in the Workflow Service using `POST /v1/workflow` API. The workflow name must match the [DAG_NAME]( https://community.opengroup.org/osdu/platform/data-flow/ingestion/segy-to-zgy-conversion/-/blob/master/airflow/segy_to_zgy_ingestion_dag.py#L32) and the [DAG_CONTENT]( https://community.opengroup.org/osdu/platform/data-flow/ingestion/segy-to-zgy-conversion/-/blob/master/airflow/segy_to_zgy_ingestion_dag.py) must be passed as string to the property `workflowDetailContent`.

#### Curl request

```
curl --location --request POST 'https://{path}/api/v1/workflow' \
    --header 'Authorization: Bearer {token}' \
    --header 'data-partition-id: {data-partition-id}' \
    --header 'Content-Type: application/json' \
    --data-raw '{
        "workflowName": "{DAG_NAME}",
        "description": "string"
        "concurrentTaskRun": 1,
        "concurrentWorkflowRun": 1,
        "workflowDetailContent": "{DAG_CONTENT}"
    }'
```

#### Expected response body

```
{
  "workflowId": "REFHX05BTUU=",
  "workflowName": "DAG_NAME",
  "description": "workflow-description",
  "concurrentWorkflowRun": 1,
  "concurrentTaskRun": 1,
  "active": true,
  "createdBy": "some-user@some-company-cloud.com",
  "creationDate": 1614251571221,
  "version": 1
}
```

## Docker container - overview

### Conversion process

- read necessary records from storage service
- (optional) create SEG-Y index
- prepare OpenZGY output parameters
- read each brick from SEG-Y source, write each brick to ZGY output
- update records with reference to converted output

### Read necessary records from storage service

The converter will read the following records:

```
   |
   +-- SEG-Y file collection (ID specified on command line) [type: dataset--FileCollection.SEGY]
   |
   +-- Work product (ID specified on command line) [type: work-product--WorkProduct]
        |
        | ( references in: data.Components[] expaned below)
        |
        +-- Seismic trace data [type: work-product-component--SeismicTraceData]
        |
        +-- Seismic bin grid [type: work-product-component--SeismicBinGrid]
```

### Create SEG-Y index

If indexing is enabled, it occurs before first conversion of the SEG-Y input file and the result is saved back to SDMS for future use. The whole file is read during this process.

### Prepare OpenZGY output parameters

Geometry, units, which are required to create the ZGY output, are extracted from the `work-product-component--SeismicTraceData` and `work-product-component--SeismicBinGrid`.

Output file path will be generated from the input: insert a GUID after the file name, replace `.sgy` extension with `.zgy`.

### Read each brick from SEG-Y source, write each brick to ZGY output

Reads are done in `SD_READ_CACHE_PAGE_SIZE` sized blocks, writes are handled by OpenZGY library.

### Update records with reference to converted output

The following records will be written:

```
   |
   +-- Seismic trace data (new version) [type: work-product-component--SeismicTraceData]
        |
        | (added reference in: data.Artefacts[], ArtefactRole:ConvertedContent)
        |
        +-- (new record) ZGY file collection [type: dataset--FileCollection.Slb.OpenZGY]
```

## Docker container - service dependencies and environment variables

### General

Data partition ID is required and used for all services: `OSDU_DATAPARTITIONID=my_data_partition`

Indexing: `SEGYTOZGY_GENERATE_INDEX(*)` Indexing is a prerequisite of the conversion (indexing collects the required geometry data from the SEG-Y trace headers). If the index does not exist, it will be created if `SEGYTOZGY_GENERATE_INDEX` is active, otherwise the conversion will fail.

Verbose output for debugging: `SEGYTOZGY_VERBOSITY(*)` and `SEGYTOZGY_INSECURE_PRINT_TOKEN(*)` Debug output (if active) is written to stdout. Without verbosity there is practically no output from the conversion process, only an error message in case of failure. All debug output is prefixed with a timestamp relative to process start.

[!] Security concern: only use `SEGYTOZGY_INSECURE_PRINT_TOKEN(*)=1` while debugging potential token issues.

Variables marked with `(*)` should be set to `1` to activate or not set at all.

All tokens will be prefixed with `Bearer:` unless they already start with `Bearer:`.

### Azure authentication (Azure deployments only)

For Azure, authentication is typically handled by the Azure auth plugin using `DefaultAzureCredential`.

Key environment variables in the container:

```bash
AUTHENTICATION_PLUGIN_FILE=/usr/local/bin/segy/libsegysdk.auth_plugin.Azure.so
SD_SVC_TOKEN=                 # Usually left empty when using the auth plugin
STORAGE_SVC_TOKEN=            # Usually left empty when using the auth plugin
```

### Storage Service

Storage service URL and access token are required:

```
STORAGE_SVC_URL=https://my-osdu-api-gateway/api/storage/v2/
STORAGE_SVC_TOKEN=eyJ0eXAiO.......
```

The converter will read and update records in the storage service.

[!] Concurrency: data races may occur if multiple conversions related to the same `SeismicTraceData` work product component run at the same time: references to one or more converted file collections may be missing after the record update.

### SDAPI

```
SD_SVC_URL=https://my-osdu-api-gateway/seistore-svc/api/v3
SD_SVC_TOKEN=eyJ0eXAiO.......
SD_READ_CACHE_PAGE_SIZE=<see below>   This is the size of network read requests and the size might vary depending on cloud provider. Google Cloud worked well with a value of 4mb, and Azure works well with a value of 1Mb.
SD_READ_CACHE_MAX_PAGES=<see below>   *** NEW MEANING - See below
```

`SD_SVC_URL` is not tolerant to trailing `/`

Cache settings:

- `SD_READ_CACHE_PAGE_SIZE` is the number of bytes read from SD in a single read operation. `1048576` (4MB) is a safe default for Azure, 4194304 works well for Google Cloud
- `SD_READ_CACHE_MAX_PAGES` max number of pages is the max number of read-ahead cache pages.
                            CAUTION: This specifies number of traces should be in the read-ahead buffer. For a SEG-Y file with 2000 samples per trace,
                            a value of 16 means up to 1 GB of memory will be used for the cache memory. A value of 8 means 520MB of memory will be used, and a value of 64 means 4 GB of memory might be allocated!
                            Using an inappropiate size might cause the DAG to run out of available memory if the kubernetes cluster does not have loads of available RAM.
  - crossline-ordered and non-linear input files may require different values than the recommended default cache in some cases

Baremetal provider:

- `S3_ENDPOINT_OVERRIDE` is the API URL for MinIO instance that Seismic DMS on Anthos works with.

## Command line

### Command line

First argument MUST be `--osdu`.

`Usage: path/to/SegyToZgy --osdu filecollection_segy_reference work_product_reference`

- `filecollection_segy_id` reference to a FileCollection.SEGY.1.0.0 object on the storage service
- `work_product_id` reference to a WorkProduct.1.0.0 object on the storage service

References should be latest version references (no version number specified).

### Example Docker command line

`docker run --env-file d:\osdu\my_osdu.env --rm -it my-segytozgy-image-tag --osdu opendes:dataset--FileCollection.SEGY:<ID>: opendes:work-product--WorkProduct:<ID>:`

### Docker Env-file

Minimum required environment variables (see above for values)

```
OSDU_DATAPARTITIONID=...
STORAGE_SVC_TOKEN=...
STORAGE_SVC_URL=...
SD_SVC_URL=...
SD_SVC_TOKEN=...
SD_READ_CACHE_MAX_PAGES=...   << This should have a value between 8 and 64, 16 works for a DAG with a 16GM RAM limit 
SD_READ_CACHE_PAGE_SIZE=...
```

## Troubleshooting

- To verify environment setup, environment variables are dumped to stdout if `SEGYTOZGY_VERBOSITY=1` is set
- Error message `StatsGenerator::getVector : Unable to locate usable vector` is usually an indicator that the `VectorHeaderMapping` for inline and crossline index byte positions is incorrect.
- In case of an error
  - if the ZGY was already created and/or written to, the stale ZGY file will remain on SDMS.
  - if updating the SeismicTraceData record fails, there will be a dangling ZGY file collection record left in the storage service
