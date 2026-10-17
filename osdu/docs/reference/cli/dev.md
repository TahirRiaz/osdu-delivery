---
id: delivery-cli-dev
title: "sqlflow dev: set a checkout up for a local run, and check what it has and what it is missing"
type: cli-command
summary: "Set a checkout up for a local run (SQL Server, databases, admin, OSDU, flows), and check every tool, setting and database it needs, each with its fix."
keywords:
  - local development
  - run locally
  - dev.bat
  - dev.sh
  - first start
  - setup wizard
  - .sqlflow/env
  - dev.env.example
  - what is missing
  - readiness check
  - sql server not installed
  - sql server in docker
  - mac
  - macos
  - linux
  - catalog database missing
  - snapshot isolation
  - first admin password
  - osdu sign-in test
  - legal tag
  - env reference not set
  - windows authentication
  - sql login
cliCommand: dev
related:
  - delivery-guide-getting-started
  - delivery-concept-environment-variables
  - delivery-concept-partitions
  - delivery-cli-partition
  - delivery-cli-db
  - concept-environment-variables
  - cli-control-plane
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Cli/LocalSetup/DevVerbs.cs
  - osdu/src/SqlFlow.Delivery.Cli/LocalSetup/DevSetupWizard.cs
  - osdu/src/SqlFlow.Delivery.Cli/LocalSetup/DevReadiness.cs
  - osdu/src/SqlFlow.Delivery.Cli/LocalSetup/DevFacts.cs
  - osdu/src/SqlFlow.Delivery.Cli/LocalSetup/DevEnvFile.cs
  - osdu/src/SqlFlow.Delivery.Cli/LocalSetup/DevEnvTemplate.cs
  - osdu/src/SqlFlow.Delivery.Cli/LocalSetup/DevValues.cs
  - osdu/src/SqlFlow.Delivery.Cli/LocalSetup/SqlTarget.cs
  - osdu/src/SqlFlow.Delivery.Cli/LocalSetup/SqlServerProbe.cs
  - osdu/src/SqlFlow.Delivery.Cli/LocalSetup/LocalSqlServers.cs
  - osdu/src/SqlFlow.Delivery.Cli/LocalSetup/OsduSignIn.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryCliModule.cs
  - sqlflow/src/SqlFlow.Core/Secrets/LocalEnvFile.cs
  - sqlflow/src/SqlFlow.Cli/Hosting/CliVerb.cs
  - dev.env.example
  - dev.bat
  - dev.sh
---

# sqlflow dev

OSDU Delivery's command line is `sqlflow`: SQLFlow's CLI with the OSDU verbs added. `dev` is one of those verbs. It sets
a checkout of OSDU Delivery up to run on this machine, and says what a local run has and what it is missing. The
launchers run it: `dev.bat` on Windows and `./dev.sh` on macOS and Linux run `dev setup` when `.sqlflow/env` is missing,
and `dev check` at every other start, and stop only when the check says a start would fail. Everything a launcher does
beyond that is find .NET, build the command line, and start what the check cleared.

`dev` is about this machine running OSDU Delivery itself. SQLFlow's `sqlflow doctor` is about a command line reaching a
control plane elsewhere ([the control plane verbs](../../../../sqlflow/docs/reference/cli/control-plane.md)).

## Synopsis

```bash
sqlflow dev setup [--env-file <path>]
sqlflow dev check [--env-file <path>] [--api] [--test-osdu] [--prepare] [--json]
sqlflow dev env   [--env-file <path>]
```

```bash
dev.bat              # Windows: the setup on a first start, then the check, then the GUI and the control plane
dev.bat setup        # the setup questions again, offering the current values
dev.bat check        # the check alone, with a test sign-in to OSDU; nothing is stopped or started
./dev.sh             # the same on macOS and Linux (./dev.sh setup, ./dev.sh check, ./dev.sh api, ./dev.sh managed)
```

## Description

A local run reads its settings from `.sqlflow/env` at the checkout's root: `NAME=value` lines, git-ignored, holding this
machine's values and secrets ([SQLFlow's environment variables and .sqlflow/env](../../../../sqlflow/docs/reference/concepts/environment-variables.md)).
The file is written from the template `dev.env.example`, which sits beside the launchers and explains every setting.
The template's sections decide what a missing setting means:

| Section | Settings | When one is missing or wrong |
| --- | --- | --- |
| Needed to start | the catalog database, the session signing key, the GUI's origin, the first admin | The check reports an error and the launchers stop. The first admin is needed only while the catalog has no user. |
| Needed to run flows | the data database; the OSDU platform, partition, client, groups and legal tag | The check warns; the launchers start, and a run that reads it fails. |
| Optional | the tenant, a flow repository, migrations, Azure sign-in, Microsoft sign-in, the assistant's ! questions, managed workers, the test database | Reported when set. |

The verb reads the env file itself: the command line does not apply it before `dev` runs (as it does for every other
verb), so a file the hosts would refuse still reaches the check, which names each line it refuses.

### The setup

`dev setup` asks for what it cannot know, offering the current file's values as the defaults, and changes nothing until
it has shown its plan and you have said yes:

1. **SQL Server.** It lists the SQL Servers on this machine: Windows services, LocalDB instances and Docker containers
   from a SQL Server image. When none runs and Docker's engine does, it offers to start SQL Server 2022 in a container
   (`osdu-delivery-sql`, port 1433, its databases kept in the volume of that name), with a generated `sa` password handed
   to Docker through its environment, never its command line, and waits up to three minutes for it to accept the
   login. That is how SQL Server runs on a Mac. It then asks for the server and the sign-in (Windows authentication on
   Windows, a SQL login otherwise), and connects. A server that does not answer is explained with its fix, and can be
   left for later.
2. **Databases.** The catalog (`OsduDelivery` by default) and the data database (`OsduData`); flows name them in
   three-part names, so the names are kept unless the flows say otherwise. A missing catalog is created with the
   catalog and the `osdu` schema after the file is written, through `sqlflow db migrate --create`. A missing data
   database is created with snapshot isolation allowed, and an existing one without it is offered it.
3. **Signing in.** The first admin's user name, and its password (12 characters at least; Enter generates one). A user
   that already exists keeps its password, so none is asked for. The session signing key is generated, or the current
   valid one kept.
4. **OSDU.** The platform's base URL, the data partition, the tenant (from which the token URL is built) or the token URL
   itself, the scope, the client id and secret, the owners and viewers groups, and the legal tag. On Azure Data Manager
   for Energy (`*.energy.azure.com`) the scope and the groups default to its conventions. On request it signs in with the
   client and reads the legal tag from the partition (`GET /api/legal/v1/legaltags/{name}`), writing nothing, and offers
   the questions again when that fails.
5. **Flows.** A flow repository to register: a folder on this machine, read live, or a git URL, cloned and pulled, with
   an access token kept in `SQLFLOW_GIT_TOKEN` and named by reference. One the catalog registers already is left as it
   is.
6. **Optional.** Whether flows read Azure through `az login` (`SQLFLOW_AZURE_AUTH=cli`), and the assistant's ! questions.

It then writes the file, keeping the one it replaces beside it as `env.<time>.bak`, carries over every name the old file
set that no question asked about and every setting it kept commented out, creates or adjusts the databases, and
registers the partition with `sqlflow partition add` (as the default when the catalog has none), applying the catalog's
pending migrations first. On macOS and Linux the file is readable by its owner only. Then it runs the check.

### What the check reports

`dev check` prints a line per finding, with the fix under each that has one:

| Section | What it reads |
| --- | --- |
| Tools | the .NET SDK (as `global.json` takes it), Node.js 22 or later with npm (not with `--api`), the Azure CLI and its sign-in (when the file reads Azure through it), Docker (where SQL Server runs in it) |
| The file | lines the hosts refuse, names set twice (the first counts), values `dev.bat` cannot set (a double quote beside `&`, `\|`, `<`, `>` or `^`, or more than 8,000 characters), and names this terminal sets to another value |
| Settings | every setting of the template, by section: set, missing, still a placeholder, or wrong (a signing key under 32 bytes, an origin that is not the GUI's, a URL that is not https, a group that is not an e-mail, Windows authentication off Windows); misspelt names, with the one meant |
| SQL Server | the servers on this machine; the catalog's server, its release and edition; the catalog database (missing, empty, another application's, offline, closed to the login) and the data database (missing, without snapshot isolation) |
| Sign-in, partitions and flows | the users and the first admin; the partitions registered; each repository (its folder missing here, its last sync's error); every `${env:NAME}` the repositories' YAML names that nothing sets (the file, this terminal, or the central configuration) |
| OSDU | with `--test-osdu`: the client's sign-in and the legal tag |

A server that does not answer is explained from what this machine has: SQL Server not installed (with the installer to
run: `winget install Microsoft.SQLServer.2022.Developer` on Windows, Docker Desktop on a Mac), its service stopped or
disabled, its container stopped, a login the server refuses (with the `CREATE LOGIN` for your Windows account), or a
certificate it does not trust.

Nothing printed shows a secret: a secret's value is shown by its length, and a connection string by its server,
database and kind of sign-in.

### env

`dev env` prints the file's settings, one `NAME=value` line each, exactly as the hosts read them (surrounding quotes
taken off, the first of a name set twice). The launchers set them in their own process: the control plane builds its
configuration from the process environment before it reads `.sqlflow/env` itself, so its `ControlPlane__*` settings
reach it only that way.

## Options

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `--env-file <path>` | path | `<checkout>/.sqlflow/env` | The env file to set up, check or print. |
| `--api` | flag | off | `check`: the GUI does not start, so Node.js is not needed and the GUI's origin is not checked. |
| `--test-osdu` | flag | off | `check`: sign in with the OSDU client and read the legal tag. Writes nothing to OSDU. |
| `--prepare` | flag | off | `check`: first replace a copied template's `<generated>` values with generated ones, and run `az login` when the file reads Azure through the Azure CLI and nobody is signed in. The launchers pass it at every start. |
| `--json` | flag | off | `check` only: one JSON document (`ready`, `errors`, `warnings`, and each section's findings) instead of text. |

## Behavior and output

```text
OSDU Delivery: what this machine has for a local run, and what it is missing

Tools
  ok     .NET SDK                                      9.0.305
  ok     Node.js                                       v22.12.0

Settings needed to start (.sqlflow/env)
  ok     SQLFLOW_CATALOG_DB                            localhost / OsduDelivery, Windows authentication
  ok     ControlPlane__Jwt__SigningKey                 set, 64 characters
  ...
Settings needed to run flows (.sqlflow/env)
  WARN   OSDU platform                                 not set up: OSDU_URL, OSDU_DATA_PARTITION, OSDU_TOKEN_URL, ...
           -> Run dev.bat setup, which asks for them and can test them; dev.bat starts without them, and runs that reach OSDU fail.

SQL Server
         On this machine                               localhost (running)
  ok     localhost                                     SQL Server 2022 (16.0.4135.4), Developer Edition (64-bit), signed in as WELLDB\dev
  ERROR  Catalog OsduDelivery                          does not exist
           -> Create it: dev.bat setup offers to, or run: dotnet run --project osdu\hosts\SqlFlow.Delivery.Cli.Host -- db migrate --create

Not ready to start: 1 ERROR line(s) above. Fix them (dev.bat setup asks the setup questions again), then start dev.bat again.
```

### Errors

| Message | Cause |
| --- | --- |
| `No dev.env.example was found in <folder> or above it. Run 'sqlflow dev' from an OSDU Delivery checkout ...` | The verb ran outside a checkout. |
| `The template <path> is missing; it ships with the repository. Restore it with: git checkout -- dev.env.example` | The checkout lost its template. |
| `<path>(<line>): the section '<title>' is none of 'Needed to start', 'Needed to run flows' and 'Optional'.` | An edited template renamed a section. |
| `name what to do: setup, check or env.` | No subcommand, or (`'<word>' is none of setup, check and env.`) an unknown one; the verb's usage follows. |
| `--json applies to 'dev check' alone.` | `--json` with `setup` or `env`. |
| `The input ended before the setup was done; nothing was written.` | `dev setup` ran with no one to answer (its input ended); exit 1. |
| `<env file> is missing; dev.bat setup writes it.` | `dev env` with no file; exit 2. |

Each error is printed as `ERROR  <message>` on stderr.

## Examples

A first start on a Windows machine with SQL Server installed:

```bat
dev.bat
```

```text
.sqlflow\env is missing, so this is a first start: a few questions set this machine up.

1. SQL Server: where the catalog and the data database live
  On this machine: localhost (running)
  SQL Server name [localhost]:
  Sign in with your Windows account (WELLDB\dev)? n for a SQL login [Y/n]:
    Connected: SQL Server 2022 (16.0.4135.4), Developer Edition (64-bit), as WELLDB\dev.
  ...
```

A first start on a Mac with Docker Desktop running and no SQL Server:

```bash
./dev.sh
```

```text
  No SQL Server is installed on this machine.
  Start SQL Server 2022 in Docker now (container osdu-delivery-sql, port 1433, its databases kept in the volume osdu-delivery-sql)? [Y/n]:
    Starting SQL Server 2022 in Docker; the first start downloads its image (about 1.5 GB) ...
    SQL Server is up in osdu-delivery-sql.
```

The check from a script, as JSON:

```bash
sqlflow dev check --api --json
```

## Exit behavior

| Exit code | `setup` | `check` | `env` |
| --- | --- | --- | --- |
| 0 | written, and a start would succeed | a start would succeed (warnings may remain) | printed |
| 1 | stopped before writing, or a usage error | a usage error | a usage error |
| 2 | written, and a start would fail | a start would fail | the file is missing or the hosts refuse a line of it; nothing printed |

## See also

- [Getting started](../guides/getting-started.md): the settings by hand, for a run from the command line alone
- [Environment variables OSDU Delivery adds](../concepts/environment-variables.md)
- [Partitions](../concepts/partitions.md)
- [sqlflow partition](partition.md)
- [sqlflow db](db.md)
- [SQLFlow: environment variables and .sqlflow/env](../../../../sqlflow/docs/reference/concepts/environment-variables.md)
