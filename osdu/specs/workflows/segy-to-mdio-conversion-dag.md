# SEGY to MDIO conversion DAG

See ADR: [Contribution of MDIO Components to OSDU Seismic DMS](https://community.opengroup.org/osdu/platform/domain-data-mgmt-services/seismic/home/-/issues/21)

## Project Overview

This project contains of two main parts:

- [OSDU Segy-to-MDIO convertor](app) - an application to convert SEGY-files to MDIO datasets with Seismic DDMS.
- [Airflow DAG](airflow/dags/segy_to_mdio_conversion_dag.py) - the DAG runs the application in Kubernetes CLuster as a separate pod

## Testing
This project contains unit, integration and end-to-end (e2e) tests.

The instructions to run the first two, can be found in the [README.md](app/README.md) of the app folder.
