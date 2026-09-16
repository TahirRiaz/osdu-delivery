# Reservoir DDMS - OpenETPServer

[[_TOC_]]

## Introduction

OpenETPServer is the open-source implementation of the Reservoir Domain Data Management Services (Reservoir DDMS) which is one of the backend services and a part of the Open Subsurface Data Universe (OSDU) software ecosystem. OpenETPServer is a single, containerized service written in C++ which stores reservoir data in the RESQML format inside a PostgreSQL database.

The service API is based on [Energistics Transfer Protocol version 1.2](http://docs.energistics.org/EO_Resources/ETP_Specification_v1.2_Doc_v1.1.pdf), and allows the exchange of data based on the [RESQML version 2.0.1+](http://docs.energistics.org/#RESQML/RESQML_TOPICS/RESQML-000-000-titlepage.html), WITSML2.1 and PRODML 2.2+ formats.

**Attention**: It is not main role of etp-server to neither check if the datasets/content is correct nor output **what** is wrong. It makes sure that all relations and format are correct. If a wrong dataset is given, the logs can help track what is wrong, but do not expect to see detailed outputs pointing to the exact issue.

**Important deployment topic**: [Performance](#performance-configuration)

### Restrictions added on top of ETP specifications

- No default dataspace: Since dataspaces are units of access control inside OSDU RDMS, having a default dataspace would require "default write access".
- Two level path for dataspace id: example (project/scenario).
- Use of "data-partition-id" as part of connection headers. see [Partitions modes](#partition-modes)
- Use of "fromDataspace" as Dataspace extra-metadata in PutDataspaces will allow duplication of an existing dataspace (referenced by its URI) when creating a new one.

## Deployment on Azure

To deploy openETPserver on Azure, follow the [instructions](devops/azure/README.md).

## Deployment on Google

To deploy openETPserver on Google, follow the [instructions](devops/gc/README.md).

## Build

### Building inside a Docker container

#### Requirements

- A Linux-based system (or Windows Subsystem for Linux)
- Docker (which now comes with docker-compose)
- For older Docker versions: docker-compose

#### <a name="buildSection"></a> Build process

The solution is based on two docker images:

- open_etp_server_build is used to build the code both in debug and release, and run the unit tests;
- open_etp_server_runtime is an image created with the result from open_etp_server_build and is packaging a server ready to run.

The solution also uses a Postgres Docker image, for the unit tests and in the default runtime mode.

To use an alternative Postgres server, you need to provide a Postgres connection string in the variable POSTGRESQL_CONN_STRING in a format like "host=172.0.0.1 port=5432 dbname=postkv user=tester password=tester".

#### Creating Docker Image (Golden Path)

To run the Docker container, build OpenETPServer and run unit tests:

```bash
docker-compose up --abort-on-container-exit
```

Note that `--abort-on-container-exit` is used to remove the postgresql container required by unit tests.

To create and test the runtime image, execute:

```bash
docker-compose up --abort-on-container-exit open_etp_server_runtime
```

This should display the help information about the ETP server options.

If the postgresql container is still running, you can remove it with this command:

```bash
docker-compose down
```

#### Running the Docker Runtime Image

By default, the `open_etp_server_runtime` image will show usage information when you run it. If you want to run the server from this image instead, you need to manually run it with a different command. You will also need to go through [PostgreSQL setup](#postgresql-setup) to get a local database running first.

The following command will run the container with the typical local configuration. To run this command, you need to either set the `$LOCAL_PSQL_CONN_STR` environment variable or replace it with your own connection string.

```shell
docker run -it -d -e RDMS_DATA_PARTITION_MODE=single -e RDMS_DATA_CONNECTIVITY_MODE=standalone -e POSTGRESQL_CONN_STRING=$LOCAL_PSQL_CONN_STR -p 9002:9002 --name open-etp-server open_etp_server_runtime openETPServer server --start --overwrite --authN none --authZ none
```

### Building via CMake on your native OS

If you prefer to develop inside a more conventional build environment, on your native OS and with favorite IDE, then follow this guide.

1. Install prerequisites:
    ```shell
    # Linux
    sudo apt-get install -y curl zip unzip tar cmake g++ pkg-config bison flex autoconf ninja-build
    ```
1. Install [VCPKG manager](https://github.com/microsoft/vcpkg). This should be done in a directory outside of your clone of this repo.
    ```shell
    # Linux/MacOS
    git clone https://github.com/microsoft/vcpkg && ./vcpkg/bootstrap-vcpkg.sh

    # Windows
    git clone https://github.com/microsoft/vcpkg && .\vcpkg\bootstrap-vcpkg.sh
    ```
1. Add the `VCPKG_ROOT` environment variable. Replace the placeholder below with the path to the directory where you cloned VCPKG in the previous step. 
    ```shell
    # Linux/MacOS
    # To set this permanently, add this to your ~/.profile or ~/.bash_profile, then run `source .`
    export VCPKG_ROOT=</path/to/vcpkg>

    # Windows (session)
    SET VCPKG_ROOT=<\path\to\vcpkg>

    # Windows (permanent)
    SETX VCPKG_ROOT <\path\to\vcpkg>
    ```
1. Configure the CMake build targets. If you want to build using Visual Studio, you may need to [configure your IDE](#configure-your-ide) first. You only need to run this command once to configure everything, and CMake will re-run this setup as needed when you run the build command in the next step:
    ```shell
    # Linux/MacOS
    cmake -GNinja -B build -DCMAKE_TOOLCHAIN_FILE=$VCPKG_ROOT/scripts/buildsystems/vcpkg.cmake -DCMAKE_BUILD_TYPE=Debug

    # Windows
    cmake -GNinja -B build -DCMAKE_TOOLCHAIN_FILE=%VCPKG_ROOT%/scripts/buildsystems/vcpkg.cmake -DCMAKE_BUILD_TYPE=Debug
    ```
1. Build the Open-ETP-Server:
    ```shell
    cmake --build build
    ```

Once everything is built, you need to make sure you go through the [PostgreSQL setup](#postgresql-setup) before you can run the openETPServer or the tests.

#### Configure your IDE

To run the CMake commands from your IDE, you will need to configure the `CMAKE_TOOLCHAIN_FILE` parameter. If you are using Visual Studio, parameters are automatically loaded from the [`CMakeSettings.json`](./CMakeSettings.json) file when using the "Open Folder" feature.

To run the build commands in Visual Studio on Windows, you may need to install the "x64 Native Tools Command Prompt", which can be found through Windows search.

### Configuration file

It is possible to set configuration through a json config file. By the default all the configuration will be provided by the default configuration file `config/openETPServer_config.json` which all the variables are set to be provided through environment variables with the syntax `$ENV{ENV_VAR_NAME}`. 


The benchmark and osduStubServer will get the configuration from `config/benchmark_config.json` and `config/stub_server_config.json`. The testing also has a dedicated config file `config/testing_config.json`. Only the file `config/openETPServer_config.json` will be installed, every other config files are designed for dev and tests purpose only.

There is the environment variable `OES_ENV` that the user can assign other configuration files. If it is set to `local` and run the app `oesBenchmark`, then the app will take the values in `benchmark_config_local.json`; if a value in this new config file is not set, then the value in the default `benchmark_config.json` will be assumed. The values in the new config file overwrite the default values in the default configuration file. This logic is valid for all apps. This variable can receive any string, except the word `default` that will read the default configuration file of the respective app.

The `--config` command option can be used with a full path to another configuration file. If it is a custom config file, the default config file `openETPServer_config.json` will be searched in the same directory where the custom config file is located. If not found, it will be searched in the default install locations.

### Logger

Logging is enabled by default. It is possible to tune the existing logger with environment variables.

To override the defaults, add variables to the [configuration](/devops/azure/chart/templates/configmap.yaml):

| Variable           | Description                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                          |
| ------------------ | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `LOG_FORMAT`       | Set up the logging format. A conversion specification starts with a `%` character, and terminates with a conversion specifier character. The valid values are as follows:<br />`%P`: the message prefix (Fatal, Critical, etc.),<br />`%T`: the current date and time in the format `YYY-MM-DD HH:MM:SS`,<br />`%F`: the file that sent the message,<br />`%L`: the line number in the file,<br />`%f`: the function in the file (if available),<br />`%M`: the message text,<br />`%%`: expands to a single character `%`.<br />The defaul format is `[%T] %P: %M`. |
| `LOG_MIN_SEVERITY` | Set up the min severity for logging. The values are `debug`, `verbose`, `info`, `warning`, `error`, `alert`, `critical`, `fatal`, `status`, `notice`. The default value is `debug`.                                                                                                                                                                                                                                                                                                                                                                                  |
| `LOG_MAX_SEVERITY` | Set up the max severity for logging. The values are the same as for min severity. The default value is `fatal`.                                                                                                                                                                                                                                                                                                                                                                                                                                                      |

## Usage

### PostgreSQL Setup
The Open ETP Server requires a connection to a [PostgreSQL](https://www.postgresql.org/) server to operate. If you're running things locally, there are two options to set up a local PostgreSQL instance:

- **Run the official docker image (recommended).** The following command is an example of how to do this. The environment variables correspond to the configuration as seen in [config/tests_config_local.json](./config/tests_config_local.json). Note that if you're also running the Open ETP Server as a docker container, then both containers need to be on the same docker network.
    ```shell
    docker run -it -d -e POSTGRES_USER=tester -e POSTGRES_PASSWORD=tester -e POSTGRES_DB=openetp -p 5432:5432 postgres
    ```
- **Install PostgreSQL locally.** Follow the [instructions for your OS](https://www.postgresql.org/download/). Note that a local PostgreSQL server uses ports that will conflict with the standard config for docker builds, so it's recommended to use the postgres docker image instead.

### Partition modes

There are two modes of how the ETP server handles partitions. Locally, you can run either the single-partition or the multi-partition mode.

* **single-partition mode** (`single`) , the openETPServer deals with a specific partition and you do not have to specify the partition in commands. The `--db-connection` command option or `POSTGRESQL_CONN_STRING` environment variable is required.

* **multi-partition mode** (`multiple`) allows you to work with several partitions. To enable it, the openETPserver requests the Partition Service. So, you need the Reservoir DDMS deployed and running on Azure. When using the **multi-partition mode**, specify the partition as a command option, e.g.: `--data-partition-id opendes`. If you use your own ETP1.2 client, you must specify the partition by sending an extra header field `data-partition-id` with the websocket handshake.

### Connectivity modes

There are two connectivity modes to choose how openETPServer will work with the partition service:

* **standalone-connectivity mode** (`standalone`) option allows the openETPServer to work with standalone partition service where the postgres server is running on premise. The DB connection string must be given explicitly.

* **osdu-connectivity mode** (`osdu`) option allows the OpenETPServer to work with OSDU core services. You can use --delegate to point to your instance of OSDU if the DDMS is deployed outside of OSDU.


### Partition x Connectivity modes
|| `standalone` | `osdu` |
|-| - | - |
| `single` | openETPServer opens a connection using the `--db-connection`  command option or  `POSTGRESQL_CONN_STRING` | openETPServer opens a connection using the `--db-connection`  command option or  `POSTGRESQL_CONN_STRING`  environment variable defined. The RDDMS will be abble to connect to other OSDU core service. The name of the OSDU instance can be given by the `--delegate` option |
| `multiple` | openETPServer opens a connection with a Standalone partition service (TO DO). Still getting OSDU repos for while. | openETPServer opens a connection with OSDU Partition service. The postgres server is running in OSDU. Connection string will be discovered by the rddms |

#### Setting mode on Azure

To set the single-partition mode on Azure, define the following variables before starting the server (connectivity mode does not have any impact here):

```bash
export RDMS_DATA_PARTITION_MODE=single
export RDMS_DATA_CONNECTIVITY_MODE=osdu
export POSTGRESQL_CONN_STRING="host=open-etp-postgresql port=5432 dbname=postkv user=tester password=tester"
```

To set the multi-partition mode on Azure, define the following variables before starting the server:

```bash
export RDMS_DATA_PARTITION_MODE=multiple
export RDMS_DATA_CONNECTIVITY_MODE=osdu
export IDENTITY_ENDPOINT=https://abc.io/metadata/identity/oauth2/token
export IDENTITY_HEADER=test
export KEYVAULT_URL=https://abc.io
export PARTITION_URL=https://dev-abc.centralus.cloudapp.azure.com/api/partition/v1
```

### Setting up AWS environment

The <osdu-api-base-url> needs to be substituted with the API's base URL, which can be found under the Systems Manager
Parameter with the name: `/osdu/eks/${EKS_CLUSTER_NAME}/instances/${OSDU_INSTANCE_NAME}/ingress/osdu-gateway/api/url`.

The `<entitlements_domain_name>` needs to be substituted with the Entitlements Domain Name, which  can be found under
the Systems Manager Parameter with the name: `/osdu/instances/${EKS_CLUSTER_NAME}/core/entitlements-v2/domain-name`

```shell
export RDMS_DATA_PARTITION_MODE=multiple
export RDMS_DATA_CONNECTIVITY_MODE=osdu
export AWS_OSDU_INSTANCE_NAME=main
export AWS_OSDU_TENANT_GROUP=osdu
export AWS_OSDU_TENANT_NAME=shared
export AWS_REGION=us-east-1
export DOMAIN_NAME=<entitlements_domain_name>
export PARTITION_URL=https://<osdu-api-base-url>/api/partition/v1
```

In addition, the server's default AWS profile needs to have permissions to read a range of SSM and Secrets Manager
secrets. The SSM Parameters accessed are:

| Parameter Purpose     | Parameter Path                                                                                  |
|-------------------------|---------------------------------------------------------------------------------------------------|
| Idp Name                | `/osdu/instances/<<osdu_instance_name>>/config/idp/name`                                          |
| IdP Client ID           | `/osdu/idp/<<idp_name>>/client/client-credentials/id`                                             |
| IdP Token URL           | `/osdu/idp/<<idp_name>>/oauth/token-uri`                                                          |
| IdP OAuth Scope         | `/osdu/idp/<<idp_name>>/oauth/custom-scope`                                                       |
| PostgreSQL Hostname     | `/osdu/tenant-groups/<<tenant_group>>/tenants/<<tenant_name>>/reservoir-ddms/PostgreSQL/hostname` |
| PostgreSQL Port         | `/osdu/tenant-groups/<<tenant_group>>/tenants/<<tenant_name>>/reservoir-ddms/PostgreSQL/port`     |
| PostgreSQL Secrets Name | `/osdu/tenant-groups/<<tenant_group>>/tenants/<<tenant_name>>/reservoir-ddms/PostgreSQL/secrets`  |

The Secrets Manager Secrets accessed are:

| Secret Purpose | Secret Name |
|--|--|
| IdP Client Secret | `/osdu/idp/<<idp_name>>/client-credentials-secret`                                |
| PostgreSQL Secret | Whatever is stored in the `PostgreSQL Secrets Name` SSM Parameter mentioned above |

### Networking Configuration

When running inside an [Istio service mesh](https://istio.io/), we recommend excluding the PostgreSQL port from
the Istio Envoy proxy redirection. To enable this, add the following pod annotation, replacing `5432` with the
port that the PostgreSQL server is exposing (which by default is `5432`):

```
traffic.sidecar.istio.io/excludeOutboundPorts: "5432"
```

Without setting this, your ETP Server will likely fail on the first request after idling betwen 2 and 5 hours
when running on Kubernetes with the [Istio service mesh](https://istio.io/).

### Performance Configuration

***NOTE***: If you do not specify one of the following configurations, the ETP Server will default to running in
on one thread-per-CPU, which when running in Kubernetes, will limit it to one thread per CPU allocated, which
will likely be lower than what the ETP Server could use. In some testing, a CPU with 8 cores could support 20
threads with less than 35% total CPU load, so a Kubernetes pod that has 800m CPU allocated should be able to
support ~6 threads. Running in single-threaded mode is ***EXTREMELY*** non-performant, as the main thread which
handles the incoming requests will also need to handle the data processing, which will cause performance issues
when used in a production environment. Also pay attention to not deploy it in DEBUG mode!

Set the environment variable `ENABLE_ANY_NUMBER_OF_THREADS` to `true` to allow the ETP Server to override the
default of limiting to the number of detected threads.

Explicitly tell the ETP Server to run with a given number of threads by specifying the command-line-argument
`-j<<NUMBER_OF_THREADS>>`. There should **NOT** be a space between the `-j` and the number. The minimum value is
3 when specifying via this configuration.

Also it is very important to have both etp-server and PostgreSQL deployed in the same zone in order to minimize cross-zone latency, this way increasing the performance.

## Telemetry service
OpenETPServer is instrumented using [OpenTelemetry C++](https://github.com/open-telemetry/opentelemetry-cpp/). The telemetry information is sent to an in-memory OTLP collector. To enable the telemetry service, you must set three environment variables:

1. ```OTEL_ENABLED```: Enable or disable telemetry. The default value is ```false```.
```bash
export OTEL_ENABLED="true" # or "false"
```
2. ```OTEL_SERVICE_NAME```: the service name, if not set or empty, the telemetry will be disabled.
```bash
export OTEL_SERVICE_NAME="your_service_name"
```
3. ```OTEL_EXPORTER_OTLP_ENDPOINT```: list of endpoints. All the endpoints will be tested and the ones that succeed the test will be used and the another ones will be ignored.
```bash
export OTEL_EXPORTER_OTLP_ENDPOINT="endpoint1 endpoint2 ..."
```

The telemetry service supports multiple exporters. Each exporter sends the telemetry data to a backend service, such as Jaeger locally deployed or a commercial service. To define the backend service to be used, the environment variable ```OTEL_EXPORTER_OTLP_ENDPOINT``` must be set and, in case of multiple exporters, separate each endpoint by a simple space like the sample bellow:
```bash
export OTEL_EXPORTER_OTLP_ENDPOINT=http://hostname1:port1 http://hostname2:port2 ...
```

The collected traces can be viewed by a locally deployed [Jaeger](https://www.jaegertracing.io/) viewer using http://localhost:16686.


Define the following environment variables before starting Jaeger:

```bash
export OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318
export OTEL_SERVICE_NAME=etp_otel_service
```

If starting Jaeger within the open_etp_server_build docker service, set: ` OTEL_EXPORTER_OTLP_ENDPOINT=http://open-etp-jaeger:4318`

To deploy Jaeger: 
```bash
docker compose up open-etp-jaeger
```

The [OpenTelemetry Collector Contrib](https://github.com/open-telemetry/opentelemetry-collector-contrib) distribution is used to forward telemetry to different destinations such as Azure App insights and Prometheus.

To deploy the collector in docker and set environment for Azure monitor:

```bash
docker run -it --rm --name otel-collector -e OTEL_SERVICE_NAME=etp_otel_service -e AZURE_INSTRUMENTATION_KEY=xxxx-xxxx-xxxx-xxxx -e OTEL_EXPORTER_OTLP_ENDPOINT=otel-collector:4318 -p 9464:9464 --network open-etp-server_otel-network  -v "$PWD/otel-collector-config.yaml":/etc/otel-collector-config.yaml otel/opentelemetry-collector-contrib --config=/etc/otel-collector-config.yaml
```

And to deploy Prometheus locally:

```bash
docker run -it --rm --name prometheus -e OTEL_SERVICE_NAME=etp_otel_service -e OTEL_EXPORTER_OTLP_ENDPOINT=otel-collector:4318 --network open-etp-server_otel-network -p 9090:9090 -v "$PWD/prometheus.yml":/etc/prometheus/prometheus.yml prom/prometheus:latest
```

The telemetry service is able to pass the telemetry context from the client to server through a dedicated http server. When the environment variable `OTEL_CONTEXT_SERVER_ENABLED` is set to `true`, then the context server will be avaiable to clients.

### Starting ETP API server

The default configuration uses the open_etp_postgresql container (see docker-compose.yml) from which the open_etp_server_build container depends, unless POSTGRESQL_CONN_STRING redefines this behavior, see [Build process](#a-name"buildsection"a-build-process).
The PostgreSQL "admin" schema is automatically created, checked, or upgraded when openETPServer
starts. It is also possible to run the initialization part using:

```bash
# Initialize the database
docker-compose run open_etp_server_runtime openETPServer server --init
```

To start openETPServer on its default port 9002, execute:

```bash
# Start the ETP server
docker-compose run -p 9002:9002 open_etp_server_runtime openETPServer server --start

# Known issue: Since docker-compose does not wait for postgres to be ready, before starting the
# server, you may need to use this command twice

# List all server options available
docker-compose up open_etp_server_runtime
```

### OSDU Integration

Within the OSDU context the server should be started with

```bash
# Start the ETP server with default OSDU authentication
openETPServer server --start --authN none --authZ delegate=''
```

This will prevent the server to use its own authentication and completely rely on OSDU authorization part for user entitlement.
The empty "delegate" argument will instead make authorization uses the ENTITLEMENTS_URL environment variable. This variable must point to the entitlement service endpoint (.../api/entitlements/v2) and since the entitlement service is a platform service, this approach should be cloud provider independent.
The Basic authentication is irrelevant for OSDU usage.
When creating a dataspace, it is required to specify the legaltags information and the list of viewers and owners, that will be part of the acl. "owners" and "viewers". Client code is required to provide valid values for the extra medata-data ["viewers","owners","legaltags","otherRelevantDataCountries"] when creating dataspaces.
Example using open-etp-server client:

```bash
docker run -it --rm open-etp:client openETPServer space -S wss://${RDDMS_URL} --new -s demo/Volve --data-partition-id ${PARTITION} --auth bearer --jwt-token ${TOKEN} --xdata "{\"viewers\":[\"data.myviewergroup@mycompany.com\"],\"owners\":[\"data.myownergroup@mycompany.com\"],\"legaltags\":\"test-legal-tag\",\"otherRelevantDataCountries\":[\"US\"]}"
```

### Interacting with the server data spaces

The server executable also implements some client-side functionalities that allow content management.

#### Server URL

Once the open_etp_server_runtime container is running in the server mode, we can retrieve its name to fulfill the ETP server URL connection option (-S).
You can find its name using the following command:

```bash
# Find the name of the container running the ETP server
docker container list

CONTAINER ID   IMAGE                                   COMMAND                  CREATED              STATUS              PORTS          NAMES
8c027a1db984   open_etp_server_runtime   "openETPServer serve…"   About a minute ago   Up About a minute   0.0.0.0:9002->9002/tcp   open-etp-server_open_etp_server_runtime_run_b0d954f0bc42
```

The ETP server can be accessed on your local machine using the full container name: **ws://open-etp-server_open_etp_server_runtime_run_b0d954f0bc42:9002**

#### Data Spaces

The storage is organized in data spaces (see [Etp 1.2 documentation](http://docs.energistics.org/EO_Resources/ETP_Specification_v1.2_Doc_v1.1.pdf)), each data space defines a scope for unique identifiers of contained resources.

Consequently, two identical pieces of data can be ingested inside the solution without conflict when assigned to different data spaces.

Although ETP supports the concept of the default data space, there is no default space in the RDDMS, and currently, the RDDMS enforces that data space paths are composed of two parts, example 'project/study'.

#### SSL (TLS) Support

The openETPServer binary contains both the server and the client. When in local develop mode (e.g., using docker-compose), both client and server work without SSL. But when server is deployed behind a gateway ingress controller (or reverse proxy) with SSL termination.

The client is able to accept connections using or not SSL. If the given server-url starts with `ws` then it is going to use a client without SSL, if it starts with `wss` then it is going to use a SSL client.

##### Steps to build SSL client locally

Currently, there is TLS (SSL) support only for ETP client. To build it locally you need to build the build image:

```bash
#docker build . -f Dockerfile.bundle -t open-etp:ssl-client --build-arg CLIENT_SSL=true
docker compose up open_etp_server_build && docker compose up open_etp_server_runtime
```

If you want to re-tag your image you can run:

```bash
docker tag open_etp_server_runtime YOUR_IMAGE_NAME:YOUR_TAG
```

using ```open-etp:client``` as a tag example:

```bash
docker tag open_etp_server_runtime open-etp:client
```

See the examples in [further](#example).

#### Authentication (Azure only)

On Azure, ETP supports authentication with a token. If you have a SSL client, you can connect a remote server over WSL and use an access token.

Obtain your access token in an appropriate way and use it to authenticate your requests to the Reservoir DMS until your token is valid.

Specify the authentication method and your token as command parameters: `--auth bearer --jwt-token ey...Bg`

#### Example

The following scripts create a new data space on the server, imports a RESQML file, checks its content, and deletes the data space.
Note that the EPC document and its HDF5 companion storing binary data must already be copied in the ./data directory so that it can be seen from inside the container.

Define the variables before running the script: `RDDMS_URL`, `TOKEN`, and `PARTITION`. If the server runs in the single-partition mode, you do not have to define this variable and specify this option in commands.

If you have both the server and the client locally, use the WS protocol. To interact with a remote server over WSS, you should have a [local client](#ssl-tls-support).


##### Authentication delegation 
The openETPServer authentication can be delegated to a external service. To do it you just have to pass the option `--authN delegate=<URL>` or `-N delegate=<URL>`

To start the ETPServer with the `groups` based authorization you need to start it with `--authZ delegate=<URL>`
And to start the ETPServer with the `policy` based authorization (CRUD) needs to be started with `--authZ policyDelegate=<URL>`


#### Authorization delegation
The authorization can be also delegated to an external service. The ETPServer authorization can be based on `groups` or `policy`. The `groups` based authorization is currently being in use in OSDU service. 

there is an environment variable called `AUTHORIZATION_MODE` can be assigned to `osdu` (default) or `simple`. Using the `osdu` mode the ETPServer will perform all record and authorization processes described by OSDU rules (calling both Entitlement and Authorization services). On the other hand the `simple` mode has simpler functionalities and request bodies. Also with this mode you can perform bulk authorization requests. 

And the `policy` based authorization is not properly implemented yet since the OSDU entitlement services is not `policy` based. Because of it, when you're running with `policy` based authorization and `AUTHORIZATION_MODE` on `osdu`, then the OSDU `group` based entitlement service will be called and every `owner` user will have all access while the `viewers` will just have authorization to read.


#### the --reject-duplicates option
When you import an epc file with this flag, the client will check if the incomming objects are already in database. If any of objects are already there with **different dates**, then the upload will fail!

##### the --check-activities option
When you start the etp-server with this flag, then the server will, for each transaction (local or not), check if there is an incomming `activity object` and relates other incomming objects. If any of these incomming objects are not related to any activity object, then the server will create an automated import activity to relate them.

##### Using default openETPserver client

The following commands are examples to show the functionality and the syntax. Remove the `data-partition-id` options when in the single-partition mode. Remove the `auth` and `jwt-token` options if authentication is not enabled. Change the protocol in the URL if needed.

```bash
# Ping server
docker-compose run open_etp_server_runtime openETPServer probe -S wss://${RDDMS_URL} --ping --auth bearer --jwt-token ${TOKEN} --data-partition-id ${PARTITION}

# Create a new data space named 'demo/Volve'
docker-compose run open_etp_server_runtime openETPServer space -S wss://${RDDMS_URL} --new -s demo/Volve --data-partition-id ${PARTITION} --auth bearer --jwt-token ${TOKEN}

# Make a list of all data spaces on the partition
docker-compose run open_etp_server_runtime openETPServer space -S wss://${RDDMS_URL} -l --data-partition-id ${PARTITION} --auth bearer --jwt-token ${TOKEN}

# Import a RESQML file in the Volve data space
# This assumes that the EPC and HDF5 companion are available in ./data directory
docker-compose run open_etp_server_runtime openETPServer space -S wss://${RDDMS_URL} -s demo/Volve --import-epc ./data/Volve_Demo_Reservoir_Grid_Depth.epc --data-partition-id ${PARTITION} --auth bearer --jwt-token ${TOKEN}

# Check the content
docker-compose run open_etp_server_runtime openETPServer space -S wss://${RDDMS_URL} -s demo/Volve --stats --data-partition-id ${PARTITION} --auth bearer --jwt-token ${TOKEN}

# Delete the existing data space named 'demo/Volve'
docker-compose run open_etp_server_runtime openETPServer space --delete -S wss://${RDDMS_URL} -s demo/Volve --data-partition-id ${PARTITION} --auth bearer --jwt-token ${TOKEN}
```

##### Using client

These are the commands to run when you use the [local client](#ssl-tls-support).

```bash
# Ping server
docker run -it --rm open-etp:client openETPServer probe -S wss://${RDDMS_URL} --auth bearer --jwt-token ${TOKEN} --ping --data-partition-id ${PARTITION} --log_level=info

# Create a new data space named 'demo/Volve'
docker run -it --rm open-etp:client openETPServer space -S wss://${RDDMS_URL} --new -s demo/Volve --data-partition-id ${PARTITION} --auth bearer --jwt-token ${TOKEN}

# Make a list of all data spaces on the partition
docker run -it --rm open-etp:client openETPServer space -S wss://${RDDMS_URL} -l --data-partition-id ${PARTITION} --auth bearer --jwt-token ${TOKEN}

# Import a RESQML file in the Volve data space
# This assumes that the EPC and HDF5 companion are available in ./data directory
# You should attach the data folder as a volume
docker run -it --rm -v ~/reservoir/open-etp-server/data:/data open-etp:client openETPServer space -S wss://${RDDMS_URL} -s demo/Volve --import-epc ./data/Volve_Demo_Horizons_Depth.epc --data-partition-id ${PARTITION} --auth bearer --jwt-token ${TOKEN}

# Check the content
docker run -it --rm open-etp:client openETPServer space -S wss://${RDDMS_URL} -s demo/Volve --stats --data-partition-id ${PARTITION} --auth bearer --jwt-token ${TOKEN}

# Delete the existing data space named 'demo/Volve'
docker run -it --rm open-etp:client openETPServer space --delete -S wss://${RDDMS_URL} -s demo/Volve --data-partition-id ${PARTITION} --auth bearer --jwt-token ${TOKEN}
```

## Additional Notes for Developers

### Developing inside the build container using VSCode

Required Visual Studio extensions:

- C++: ms-vscode.cpptools
- Remote container support: ms-vscode-remote.remote-containers

Create the container image using the instruction in [Build](#a-name"buildsection"a-build-process) section.

To debug, you can use:

```bash
docker-compose run open_etp_server_build bash
```

It will launch a batch in the build container, allowing to interact with it, or attach VSCode to the container.

Open the open-etp-server directory in Visual Studio Code (View -> Command Palette Ctrl+P). Then choose: Remote container/Attach to running container...
Sometimes you may need to reopen the folder in /source
The first time in the container, you may have to (re)enabled the C++ extension locally.
This creates a new Visual Studio Code window that you can use to build (Ctrl B) and debug (Ctrl D).

### Selecting PostgreSQL Server

docker-compose will also start a container of a PostgreSQL instance.

Running:

```bash
docker-compose run open_etp_server_build bash
```

will use the docker postgres image so that PostgreSQL dependent unit test can run.

It is also possible to work with a pre-existing PostgreSQL server and not to use the container.
You simply need to update the POSTGRESQL_CONN_STRING in the launch.json for the Unit tests and
in the docker-compose.yml for the server usage.

## E2E Testing

[Here](docs/testing.md) you can find test cases and the link to the end-to-end tests collection.

## Recommendations for writing clients

When writing clients, there is some recommended behavior when interacting with the server. You can see the details [here](docs/bestPracticesForClients.md).