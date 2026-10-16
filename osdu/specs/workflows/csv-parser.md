# CSV Parser

* Currently, the CSV parser does not support ingestion of data into nested attributes
* CSV parser supports ingestion in its original form. There is no conversion done on the data be it related to Date/time, Unit or CRS.

## Running the Application locally

The CSV Parser is a Maven multi-module project with each cloud implementation placed in its submodule.

## Running the Application as Airflow DAG step

Instructions for running the DAG are [here](./airflowdags/README.md)

### Azure

Instructions for running the Azure implementation locally can be found [here](./provider/csv-parser-azure/README.md)

### Google Cloud

Instructions for running the Google Cloud implementation locally can be found [here](./provider/csv-parser-gc/README.md)

### Other platforms

1. Navigate to the module of the cloud of interest, for example, ```csv-parser-azure``` and configure ```application.properties```. Intead of changing these files in the source, you can also provide external files at run time.

2. Navigate to the root of the application, build and run unit tests in command line:

    ```bash
    mvn clean package
    ```

## Cloud Deployment

This section describes the deployment process for each cloud provider.

### Azure

Instructions for running the Azure implementation in the cloud can be found [here](https://dev.azure.com/slb-des-ext-collaboration/open-data-ecosystem/_git/infrastructure-templates?path=%2Fdocs%2Fosdu%2FSERVICE_DEPLOYMENTS.md&_a=preview).
(This link may not be reachable for everyone, it points to Azure infrastructure templates and ensuing documentation. We are trying to find a home for that, so please stay tuned, or reach our to Dania.Kodeih@microsoft.com to get early access)

## Running integration tests

Integration tests are located in a separate project for each cloud in the ```testing``` directory under the project root directory.

### Azure

Instructions for running the Azure integration tests can be found [here](./testing/csv-parser-azure-test/README.md).

### Google Cloud

Instructions for running the Google Cloud integration tests can be found [here](./testing/csv-parser-gc-test/README.md).

## What SPI needs to implemented by CSP in case of csv parser -

Note: CSPs don't have to implement any SPIs for R3.

Following are the interfaces that are required -
[BlobIterator](./csv-parser-core/src/main/java/org/opengroup/osdu/csvparser/blob/BlobIterator.java)

Azure implementation for BlobIterator can be found [here](./provider/csv-parser-azure/src/main/java/org/opengroup/osdu/csvparser/provider/azure/blob/AzureBlobIterator.java).

[BlobStorage](./csv-parser-core/src/main/java/org/opengroup/osdu/csvparser/blob/BlobStorage.java)

Azure implementation for BlobStorage can be found [here](./provider/csv-parser-azure/src/main/java/org/opengroup/osdu/csvparser/provider/azure/blob/AzureBlobStorage.java).

## License

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

[http://www.apache.org/licenses/LICENSE-2.0](http://www.apache.org/licenses/LICENSE-2.0)

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
