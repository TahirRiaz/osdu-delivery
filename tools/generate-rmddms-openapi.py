"""Generates the OpenAPI document of the Reservoir Management DDMS, which the service only produces at runtime.

The service (GitLab project 1470, osdu/platform/domain-data-mgmt-services/reservoir-management/
reservoir-management-domain-services) builds its routes from two generic router classes and publishes no static
contract. The contract OSDU Delivery designs the service's route against is therefore the document the service's own
FastAPI app generates at the commit osdu/specs/sources.json records for
reservoir-management-ddms/openapi.generated.json. Nothing is called while the document is generated:

- osdu_api, an external library the service installs unpinned from another package index, is replaced by inert stubs,
  because generating the document never calls it;
- the settings the app reads when it is imported get placeholder values, so no network or database is opened.

Usage, from the repository root:

    git clone https://community.opengroup.org/osdu/platform/domain-data-mgmt-services/reservoir-management/reservoir-management-domain-services.git rmddms
    git -C rmddms checkout <the commit sources.json records>
    python -m venv rmddms-venv
    rmddms-venv/Scripts/python -m pip install <every package rmddms/requirements.txt lists except osdu-api>
    rmddms-venv/Scripts/python tools/generate-rmddms-openapi.py rmddms

requirements.txt is a UTF-16 file. The document committed was generated with FastAPI 0.88.0, Starlette 0.22.0,
Pydantic 1.10.26, SQLAlchemy 2.0.54 and psycopg2-binary 2.9.13, the versions its ranges resolved to. The script writes
osdu/specs/reservoir-management-ddms/openapi.generated.json (or the file --out names) and prints what it holds.
"""

import argparse
import json
import os
import sys
import types
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_OUT = ROOT / "osdu" / "specs" / "reservoir-management-ddms" / "openapi.generated.json"

# What the app's settings require when it is imported. Placeholders: generating the document connects to nothing.
PLACEHOLDER_SETTINGS = {
    "CLIENT_ID": "placeholder",
    "CLIENT_SECRET": "placeholder",
    "TENANT_ID": "placeholder",
    "SCOPE": "placeholder",
    "POSTGRES_USER": "placeholder",
    "POSTGRES_PASSWORD": "placeholder",
    "POSTGRES_SERVER": "localhost",
    "POSTGRES_PORT": "5432",
    "POSTGRES_DB": "placeholder",
    "BACKEND_CORS_ORIGINS": "http://localhost",
    "OSDU_HOST": "http://osdu.invalid",
}

# The osdu_api classes the app imports, each replaced by an inert stand-in.
OSDU_API_STUBS = {
    "osdu_api.clients.schema.schema_client": "SchemaClient",
    "osdu_api.clients.storage.record_client": "RecordClient",
    "osdu_api.clients.search.search_client": "SearchClient",
    "osdu_api.model.search.query_request": "QueryRequest",
    "osdu_api.model.storage.record": "Record",
}

EM_DASH = "\u2014"  # written as an escape: the character itself is not allowed in this repository


class _Inert:
    """Stands in for an osdu_api class: constructing it records its arguments and does nothing else."""

    def __init__(self, *args, **kwargs):
        self.args = args
        self.kwargs = kwargs


def stub_osdu_api():
    for module_name, class_name in OSDU_API_STUBS.items():
        parts = module_name.split(".")
        for i in range(1, len(parts) + 1):
            name = ".".join(parts[:i])
            if name not in sys.modules:
                sys.modules[name] = types.ModuleType(name)
        setattr(sys.modules[module_name], class_name, _Inert)


def plain_punctuation(value):
    """The document with every em dash written as plain punctuation, which this repository requires of its files."""
    if isinstance(value, str):
        return value.replace(" " + EM_DASH + " ", " - ").replace(EM_DASH, "-")
    if isinstance(value, list):
        return [plain_punctuation(item) for item in value]
    if isinstance(value, dict):
        return {key: plain_punctuation(item) for key, item in value.items()}
    return value


def main():
    parser = argparse.ArgumentParser(description="Generate the Reservoir Management DDMS OpenAPI document from a checkout of the service.")
    parser.add_argument("checkout", type=Path, help="a checkout of the service at the commit osdu/specs/sources.json records")
    parser.add_argument("--out", type=Path, default=DEFAULT_OUT, help="where to write the document")
    arguments = parser.parse_args()

    checkout = arguments.checkout.resolve()
    if not (checkout / "app" / "main.py").is_file():
        parser.error(f"{checkout} is not a checkout of the Reservoir Management DDMS: app/main.py is missing")

    out = arguments.out.resolve()
    for key, value in PLACEHOLDER_SETTINGS.items():
        os.environ.setdefault(key, value)

    stub_osdu_api()

    # The app reads example files relative to the checkout when it is imported.
    os.chdir(checkout)
    sys.path.insert(0, str(checkout))
    from app.main import app  # noqa: E402  (imported from the checkout named on the command line)

    document = plain_punctuation(app.openapi())
    out.parent.mkdir(parents=True, exist_ok=True)
    with out.open("w", encoding="utf-8", newline="\n") as file:
        file.write(json.dumps(document, indent=2) + "\n")

    operations = sum(len(methods) for methods in document["paths"].values())
    print(f"OpenAPI {document.get('openapi')}: {document['info']['title']} {document['info']['version']}")
    print(f"{len(document['paths'])} paths, {operations} operations")
    print(f"written to {out}")


if __name__ == "__main__":
    main()
