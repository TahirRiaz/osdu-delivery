# Reservoir Management (RM) DDMS

# Table of Contents
1. Introduction
2. Project structure
3. Requirements
4. Project Startup
   1. Run the service locally
   2. Set Environment Variables
      1. Run with Uvicorn
      2. Run with Docker
      3. Run Unit Tests
   3. Usage


## Introduction 
Reservoir Management Domain Data Management Services (RM DDMS) Open Subsurface Data Universe (OSDU) is a microservices-based project that comprises OSDU software ecosystem, written in Python that provides an API for Reservoir Management related data.

## Project structure
     ├──app
     │   ├── api                    - web related files.
     │   │   ├── dependencies       - dependencies for routes definition.
     │   │   ├── errors             - definition of error handlers.
     │   │   └── routers            - route and endpoints defintion.
     │   ├── core                   - application configuration, startup events, logging.
     │   ├── db                     - database related files.
     │   ├── exceptions             - exceptions definitions 
     │   ├── models                 - pydantic models for this application.
     │   ├── schemas                - schemas for using in web routes (request, responses bodies).
     │   ├── services               - all logic classified by BO
     │   └── main.py                - FastAPI application creation and configuration.
     ├──docs                  
     ├──provider
     └── ests
         ├── resources
         └── services
         


## Requirements

1. Clone the rm-ddms [repository](https://github.com/TotalEnergiesCode/rm-ddms.git)
2. Download [Python](https://www.python.org/downloads/) >=3.8
3. Ensure pip, a pre-installed package manager and installer for Python, is installed and is upgraded to the latest version.

      ```bash
      # Windows
      python -m pip install --upgrade pip
      python -m pip --version

      # macOS and Linux
      python3 -m pip install --upgrade pip
      python3 -m pip --version
      ```


## Project Startup

### Run the service locally

1. Create virtual environment in the wellbore project directory. This will create a folder inside of the wellbore project directory. For example: ~/os-wellbore-ddms/nameofvirtualenv

    ```bash
    # Windows
    python -m venv env

    # macOS/Linux
    python3 -m venv env
    ```

2. Activate the virtual environment

    ```bash
    # Windows
    .\env\Scripts\activate

    # macOS/Linux
    source env/bin/activate
    ```

5. Install dependencies

    ```bash
    pip install -r requirements.txt
    ```

    Or, for a developer setup, this will install tools to help you work with the code.

    ```bash
    pip install -r requirements-dev.txt
    ```

    Note: If you encounter an error, ensure pip is updated to the latest version in the context of the virtual environment and install dependencies again.

    ```bash
    python -m pip install --upgrade pip
    ```

6. Run the service


### Set Environment Variables
```
CLIENT_SECRET=xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
CLIENT_ID=xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
TENANT_ID=xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
SCOPE=xxxxxxxx-xxxx-xxxx-xxx-xxxxxxxxxxxx/.default
POSTGRES_USER=exemple
POSTGRES_PASSWORD=password-exemple
POSTGRES_SERVER=serveur-exemple
POSTGRES_PORT=XXXX
POSTGRES_DB=database-name
BACKEND_CORS_ORIGINS = "http://SERVEUR:PORT"
OSDU_HOST="https://domain-url"
```

### Run with Uvicorn

```bash
uvicorn main:app --port LOCAL_PORT
```

Then access app on `http://127.0.0.1:<LOCAL_PORT>/docs`

### Run with Docker

### Run Unit Tests

## Usage

1. Generate bearer token to be authorized to use the API endpoints.

    - Navigate to `http://127.0.0.1:8000/docs`. Click `Authorize` and enter your token. That will allow for authenticated requests.


# CI/CD

The deployment should be triggered once the infrastructure is available with all secrets in the Keyvault.
See [AKS Setup status here](https://github.com/TotalEnergiesCode/osduapps-aks-infra)

# Database

You can use as base the rmddms-db-dump.sql as base to init the database you want to try. Notice than you will need to 
insert all data dependencies to make it works and create at least one master data. Then you will be able to 
create the different objects from the Ingestion Workflow when related to OSDU object or directly with the API endpoint 
when POST available.

Mandatory data are needed in the following tables:
pool (as master-data)
forecast
r_cor_gas_visc
r_cor_oil_visc
r_cor_pbrsbo
r_fluid
r_hc_type
r_ori