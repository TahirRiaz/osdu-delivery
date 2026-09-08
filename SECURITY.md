# Security Policy

## Supported Versions

OSDU Delivery is pre-1.0 and under active development. Security fixes are applied to the `main` branch.

## Reporting a Vulnerability

**Please do not report security vulnerabilities through public GitHub issues.**

Instead, report them privately through the repository's Security Advisories page, or by email to
**<security@sqlflow.io>**.

Please include:

- A description of the issue and its impact
- Steps to reproduce
- Affected version / commit

We aim to acknowledge reports within 3 business days and to provide a remediation timeline after triage.

## Handling of credentials

OSDU Delivery never stores secrets in flow or mapping documents. Connection strings, OSDU client secrets, and
storage credentials are supplied by reference and resolved at run time, on every platform .NET runs on. The
canonical contract lives in [docs/environment-variables.md](docs/environment-variables.md):

- Explicit references: `${env:NAME}` and `${keyvault:vault/secret}`.
- Local development values belong in the git-ignored `.sqlflow/env` file; the process environment (CI,
  Kubernetes, schedulers) always wins over it.
- `sqlflow validate` and `sqlflow run` warn loudly when a document embeds a `Password=`-style literal, without
  echoing the value. Resolved secrets are redacted before they reach any log, trace, error message, or run
  artifact, and the catalog stores redacted errors only.

Please follow this convention in any contribution and never commit real connection strings or credentials.
