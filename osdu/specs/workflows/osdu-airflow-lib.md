# OSDU Airflow Library

OSDU Airflow Library is the package providing Airflow specific logic.

## Contents

- [OSDU Airflow Library](#osdu-airflow-library)
    - [Contents](#contents)
- [Getting Started](#getting-started)
    - [Required Airflow Variables](#required-airflow-variables)
    - [Optional Airflow Variables](#optional-airflow-variables)
    - [Backward Compatibility](#backward-compatibility)
    - [Installation from source](#installation-from-source)
    - [Installation from Package Registry](#installation-from-package-registry)
- [Package Lifecycle](#package-lifecycle)
    - [Licence](#licence)

# Getting Started

## Required Airflow Variables

The following variables are used by `osdu_api` clients. If some of them are missing `osdu_api` will try to find
corresponding values in `osdu_api.ini` file, which path is set in `OSDU_API_CONFIG_INI` environmental variable.

|Variable name|Example|
|-------------|-------|
|core__service__dataset__url | \<http\|\|https\>://\<service-host\>/api/dataset/v1 |
|core__service__file__host | \<http\|\|https\>://\<service-host\>/api/file |
|core__service__file__url | \<http\|\|https\>://\<service-host\>/api/file/v2 |
|core__service__partition__url | \<http\|\|https\>://\<service-host\>/api/partition/v1/ |
|core__service__schema__host | \<service-host\> |
|core__service__schema__url | \<http\|\|https\>://\<service-host\>/api/schema-service/v1 |
|core__service__search__host | \<service-host\> |
|core__service__search__url | \<http\|\|https\>://\<service-host\>/api/search/v2 |
|core__service__seismic__url | \<http\|\|https\>://\<service-host\>/api/seismic-store/v3 |
|core__service__storage__host | \<service-host\> |
|core__service__storage__url | \<http\|\|https\>://\<service-host\>/api/storage/v2 |
|core__service__unit__url | \<http\|\|https\>://\<service-host\>/api/unit/v2 |
|core__service__workflow__host | \<http\|\|https\>://\<service-host\>/api/workflow |
|core__service__workflow__url | \<http\|\|https\>://\<service-host\>/api/workflow/v1 |

## Optional Airflow Variables

| Variable name                       | Example    | Description                                                  |
|-------------------------------------|------------|--------------------------------------------------------------|
| core__ingestion__raise_on_any_error | false      | Flag if a pipeline should stop executing if any error occurs |
| core__ingestion__batch_save_enabled | true/false | Enable batch saving in Storage                               |
| core__ingestion__batch_save_size    | 400        | Size of batches to save in Storage (default: 500)            |
| core__ingestion__thread_save_number | 10         | Number of simultaneous writings in Storage                   |

## Backward Compatibility

Airflow 1.10.15 is as a “bridge” release but in OSDU Airflow 1.10.10 version should be supported.

Install Airflow via package extras (**`[AF3]` preferred**; `[AF2]` deprecated due to Airflow 2 security issues):

```shell
pip install 'osdu-airflow[AF3]~=0.30.0' --extra-index-url=https://community.opengroup.org/api/v4/projects/668/packages/pypi/simple
pip install 'osdu-airflow[AF2]~=0.30.0' --extra-index-url=https://community.opengroup.org/api/v4/projects/668/packages/pypi/simple  # deprecated
```

For full local/CI dependency sets, shared pins are in [`requirements-core.txt`](requirements-core.txt); use [`requirements.txt`](requirements.txt) / [`requirements-dev.txt`](requirements-dev.txt) for AF2 (`apache-airflow==2.11.2`) or [`requirements-airflow3.txt`](requirements-airflow3.txt) / [`requirements-airflow3-dev.txt`](requirements-airflow3-dev.txt) for AF3 (`apache-airflow==3.2.2`). CI runs unit tests on both majors (`compile-and-unit-test` and `compile-and-unit-test-airflow3`). See [`SECURITY_AIRFLOW.md`](SECURITY_AIRFLOW.md) for vulnerability issue context (#23, #52, #53).

## Installation from source

1. Pull the latest changes from https://community.opengroup.org/osdu/platform/data-flow/ingestion/osdu-airflow-lib

2. Use Python 3.11. Also, it is highly recommended using an isolated virtual environment for development purposes
  (Creation of virtual environments: https://docs.python.org/3.11/library/venv.html)

3. Make sure you have setuptools and wheel installed

```shell
pip install --upgrade setuptools wheel
```

4. Change directory to the root of the project

```shell
cd path/to/osdu-airflow-lib
```

5. Make sure osdu-airflow isn't already installed

```shell
pip uninstall osdu-airflow
````

6. Install OSDU Airflow

```shell
python setup.py install
```

Example import after installing:

```python
from osdu_airflow.backward_compatibility import update_default_args
```

## Installation from Package Registry

Preferred (Airflow 3):

```shell
pip install 'osdu-airflow[AF3]~=0.30.0' --extra-index-url=https://community.opengroup.org/api/v4/projects/668/packages/pypi/simple
```

Deprecated (Airflow 2: security issues in Airflow 2; prefer `[AF3]`):

```shell
pip install 'osdu-airflow[AF2]~=0.30.0' --extra-index-url=https://community.opengroup.org/api/v4/projects/668/packages/pypi/simple
```

Bare `pip install 'osdu-airflow'` does not pull `apache-airflow`; use `[AF3]` (preferred) or `[AF2]` (deprecated).

## Package Unit Test

In EDS, we follow Test Driven Development (TDD) approach using `pytest` which requires writing tests before writing the
actual code.
The unit test are designed to be independent of the Airflow environment by inheriting the `base_airflow.py` and is able
to run on local environment as well as in cloud.

Install pytest:

```shell
pip install pytest
```

Navigate to the `test folder`:

```shell
cd osdu_airflow/tests
```

Run all unit tests in a folder (replace `<folder_name>` with the actual folder):

```shell
pytest ./<folder_name>
```

Run a unit test by folder (replace `<folder_name>` and `<test_file_name>` with the actual folder and file names):

```shell
pytest ./<folder_name>/<test_file_name>.py
```

## Package Unit Test Coverage Report

Code Coverage Report (replace `<folder_name>` with the actual folder)

```shell
coverage run -m pytest ./<folder_name>   # Run tests with coverage
```

Code Coverage Generate Report in CMD and HTML (pick one of the following commands)

```shell
coverage report -m                      # Show coverage report in cmd
coverage html                           # Show coverage report in html
```

# Documentation
There is a documentation for EDS available at https://osdu.pages.opengroup.org/platform/data-flow/ingestion/osdu-airflow-lib. The deployment will be automatically triggered when the documentation are updated inside the branch `eds-documentation`.

It is recommended to run locally and develop the documentation locally before pushing the code for increased productivity and troubleshooting purposes.
## Run the documentation locally
```shell
# Change directory to the docs folder that contains contains mkdocs.yml file
cd docs

# Installing mkdocs package
pip install mkdocs-material
pip install mkdocs-git-revision-date-plugin
pip install mkdocs-video

# Building and serving mkdocs on localhost
mkdocs build --site-dir local_docs_host
mkdocs serve
```

# Running CI/CD Pipeline commands on local machine

## 1. Pytype/Python Static Analysis
To run the type checking commands on a local machine, you can use the following commands:

```shell
pytype osdu_airflow -k -j auto --exclude osdu_airflow/tests osdu_airflow/operators/process_manifest_r2.py osdu_airflow/eds/models
```

## 2. Isort
To check if the code is sorted correctly, you can use the following command:

```shell
isort -c -v osdu_airflow --profile black --multi-line 3 --nlb LOCALFOLDER --ensure-newline-before-comments
```

To auto resolve the code sorting issues, you can use the following command:

```shell
isort -v osdu_airflow --profile black --multi-line 3 --nlb LOCALFOLDER --ensure-newline-before-comments
```

## 3. Pylint
To run the pylint commands on a local machine, you can use the following command:

```shell
pylint --rcfile=.pylintrc osdu_airflow
```


# Package Lifecycle

The project can be deleted once Airflow 1.10.10 version support will be deprecated and no any additional logic will be
added.

## License

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
A package to interface with OSDU microservices
