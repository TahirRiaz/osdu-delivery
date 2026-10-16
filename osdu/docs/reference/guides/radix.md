---
id: delivery-guide-radix
title: "Deploying OSDU Delivery on Radix: the control plane, the GUI, the MCP server and the workers"
type: guide
summary: "Run OSDU Delivery as one Radix application: what to set up per environment, how to build and deploy, and the first start."
keywords:
  - radix
  - radixconfig
  - deploy
  - workload identity
  - key vault
  - azure sql
  - ai foundry
  - mcp server
  - job component
  - managed workers
  - github actions
  - rx
  - osdu partition per environment
related:
  - delivery-guide-managed-workers
  - delivery-concept-partitions
  - delivery-guide-deployment
  - guide-deployment
  - guide-chat-assistant
  - cli-worker
sourceRefs:
  - osdu/deploy/radix/radixconfig.yaml
  - osdu/deploy/radix/radix-deploy.workflow.yml
  - osdu/deploy/docker/control-plane.Dockerfile
  - osdu/deploy/docker/gui.Dockerfile
  - osdu/deploy/docker/mcp.Dockerfile
  - osdu/deploy/docker/worker.Dockerfile
  - osdu/src/SqlFlow.Delivery.ControlPlane/ManagedWorkers/RadixWorkerLauncher.cs
  - osdu/src/SqlFlow.Delivery/Catalog/ConfiguredPartitions.cs
---

# Deploying OSDU Delivery on Radix

OSDU Delivery runs on Radix as one application, `osdu-delivery`, defined by
[osdu/deploy/radix/radixconfig.yaml](../../../deploy/radix/radixconfig.yaml), in the shape Radix applications commonly
take: images built by GitHub Actions and pushed to an Azure Container Registry, a deploy-only pipeline that rolls them
out, a workload identity per environment, and secrets from each environment's Key Vault.

| Component | Image | Public address (dev) | What it does |
| --- | --- | --- | --- |
| `controlplane` | `osdu-delivery-control-plane` | `https://controlplane-osdu-delivery-dev.<zone>` | The API, scheduling, dispatch and the chat assistant. One replica. It starts and stops the workers ([managed workers](managed-workers.md)) and calls the model in AI Foundry. |
| `gui` | `osdu-delivery-gui` | `https://gui-osdu-delivery-dev.<zone>` | The SPA. It reads the control plane's address from `SQLFLOW_API_BASE_URL`. |
| `mcp` | `osdu-delivery-mcp` | `https://mcp-osdu-delivery-dev.<zone>/mcp` | The MCP server. Public, because the model's MCP tool calls it from AI Foundry; every call carries a SQLFlow token, which it checks with the control plane and forwards to it. |
| `worker` (job) | `osdu-delivery-worker` | none | The workers. The control plane starts each as a job through the job component's scheduler at `http://worker:8080`. |

Environments are `dev`, `test` and `prod`. Radix gives each public component the address
`<component>-<app>-<env>.<zone>`, where `<zone>` is the DNS zone of the cluster the application is registered on (the
Radix web console shows each component's address). The file is written for one cluster's zone; registered on another,
every address in it changes with the zone.

## Before the first deploy

Every value in angle brackets in `radixconfig.yaml` is the estate's own. Set up the following once per environment,
then fill them in.

### 1. The Radix application and the deploy identity

1. Register `osdu-delivery` in the Radix web console with this repository and the config file path
   `osdu/deploy/radix/radixconfig.yaml`.
2. Make the deploy identity (the Entra app or managed identity GitHub signs in as) an administrator of the
   application, and give it `AcrPush` on the registry the file names (`privateImageHubs`). Add a federated credential on it for this
   repository's GitHub environments (`repo:<owner>/osdu-delivery:environment:dev`, and `test`, `prod`), and set
   `AZURE_CLIENT_ID` and `AZURE_TENANT_ID` as repository or environment secrets.
3. In the application's Radix console, set the password of the registry's `radix-pull` user (`privateImageHubs`).

### 2. Two workload identities

The control plane and the workers each sign in to Azure as their own identity (a user-assigned managed identity or an
app registration), set as `identity.azure.clientId` per environment:

| Identity | Federated credential subject | Needs |
| --- | --- | --- |
| control plane | `system:serviceaccount:osdu-delivery-<env>:controlplane-sa` | Azure SQL (below), `Key Vault Secrets User` on the environment's vault, `Cognitive Services OpenAI User` on the Foundry account |
| worker | `system:serviceaccount:osdu-delivery-<env>:worker-sa` | Azure SQL (below), `Key Vault Secrets User` on the environment's vault |

The issuer of each federated credential is the Radix cluster's OIDC issuer; the Radix API's configuration
(`GET /api/v1/configuration` on the cluster's Radix API, signed in) lists it as `clusterOidcIssuers`. The
application signs in through Azure's default credential chain, which picks up the workload identity Radix gives the
pod; nothing else selects it.

### 3. Azure SQL

Two databases on one server: `OsduDelivery` holds SQLFlow's catalog and the `osdu` schema beside it, and `OsduData`
holds the ingestion tables the flows load and deliver from. Keep both names: the estate's flows name their tables
through them (`OsduData.arc.WellLog`, `OsduDelivery.osdu.v_dim_WellLogCurve`). Both must exist; the control plane migrates the catalog
and the `osdu` schema itself when it starts, and creates neither database. Both identities sign in with
`Authentication=Active Directory Default`, so no connection carries a password. Create a user for each from its
identity's name:

```sql
-- In OsduDelivery
CREATE USER [<control plane identity>] FROM EXTERNAL PROVIDER;
ALTER ROLE db_owner ADD MEMBER [<control plane identity>];       -- it migrates the catalog and the osdu schema

CREATE USER [<worker identity>] FROM EXTERNAL PROVIDER;           -- a worker reaches the osdu schema only
GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE, ALTER, REFERENCES ON SCHEMA::osdu TO [<worker identity>];
GRANT CREATE TABLE, CREATE VIEW TO [<worker identity>];           -- dimension builds create osdu.dim_* and osdu.v_dim_*

-- In OsduData
CREATE USER [<control plane identity>] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [<control plane identity>];   -- a person's reads of a flow

CREATE USER [<worker identity>] FROM EXTERNAL PROVIDER;           -- the flows create, widen, load and read their tables
ALTER ROLE db_ddladmin ADD MEMBER [<worker identity>];
ALTER ROLE db_datareader ADD MEMBER [<worker identity>];
ALTER ROLE db_datawriter ADD MEMBER [<worker identity>];
```

### 4. Key Vault

One vault per environment, named in `radixconfig.yaml` (`<dev key vault>` and so on), with these secrets:

| Secret | Read by | How | Content |
| --- | --- | --- | --- |
| `jwt-signing-key` | control plane | `secretRefs` | At least 32 random bytes (for example 64 hex characters). It signs every session; changing it signs everyone out. |
| `admin-password` | control plane | `${keyvault:...}` reference | The bootstrap `admin` account's password. |
| `osdu-client-secret` | worker, control plane | `${keyvault:...}` reference: the partition's `OSDU_CLIENT_SECRET` value | The secret of `OSDU_CLIENT_ID`, the client the flows sign in to OSDU with. A worker reads it when a run needs it, and the control plane for the reads a person waits on. |
| `git-token` | control plane, worker | `secretRefs` | A token that can read the estate repository: the control plane syncs it, a worker checks out the commit a run is pinned to. |
| `node-token` | worker | `${keyvault:...}` reference | The workers' credential. Created after the first deploy (below). |

A secret named by `secretRefs` must exist before the component starts, or its pod does not start; the ones read through
a `${keyvault:...}` reference are read by the application itself when it needs them, so `node-token` can follow the
first deploy.

### 5. The OSDU partition

Each environment's `controlplane` variables name the OSDU partition that environment delivers to, as the control plane's
configuration ([configured partitions](../concepts/partitions.md#configured-partitions)):
`Osdu__Partitions__<data-partition-id>__Default`, `__Description`, and one `__Properties__<NAME>` per value its runs
resolve (`OSDU_URL`, `OSDU_TOKEN_URL`, `OSDU_SCOPE`, `OSDU_CLIENT_ID`, the legal tag and access groups, and
`OSDU_CLIENT_SECRET` as the `${keyvault:<vault>/osdu-client-secret}` reference). The control plane registers it when it
starts; the workers hold no OSDU value of their own, since every run carries its partition's. Rename the partition in
the file to the environment's own data-partition-id; a second partition is another set of these under its own name, with
exactly one `Default`.

### 6. Sign-in

An Entra app registration per environment with a single-page application redirect URI of the GUI's address
(`https://gui-osdu-delivery-<env>.<zone>`); its client id is `ControlPlane__AzureAd__ClientId`. Sign-in is open to
the tenant `ControlPlane__AzureAd__AllowedTenantIds__0` names; to limit it to a group, require assignment on the
registration's enterprise application and assign the group.

### 7. AI Foundry

A Foundry project with a model deployment that serves the Responses API; the control plane calls
`https://<account>.openai.azure.com/openai/responses` with its workload identity (no key), so the account's name is
the first label of `ControlPlane__Assistant__Foundry__ProjectEndpoint`. Grant the control plane identity
`Cognitive Services OpenAI User` on the account. The model calls the MCP server at its public address with a token
the control plane delegates to that one run, so the MCP server must stay public. Questions starting with `!` run
read-only queries over the approved AI contexts on the workers, which therefore need the connections those contexts
open (`OSDU_DATA_DB` already).

### 8. Network

Radix lets components reach the internet unless `egress` rules are set, and this file sets none: a rule list blocks
every address it does not name, including Entra sign-in. Azure SQL, Key Vault and Foundry each need to admit the
cluster, in one of two ways:

- **Private endpoint**: request one per resource for the cluster with the private link form in the Radix platform
  repository (sub-resource `sqlServer`, `vault`, `account`), then approve the connection on the resource. Your
  subscription must be allowed to have endpoints in Radix's first.
- **Firewall**: admit the cluster's egress addresses, which the Radix API's configuration lists as
  `clusterEgressIps`.

An OSDU, storage account or proxy that resolves to a private address must also be listed in
`SQLFLOW_DELIVERY_PRIVATE_NETWORKS` (both on the control plane and on the worker); empty, a flow reaches public
addresses only.

## Deploy

The **Deploy to Radix** workflow is a sample beside the application, `osdu/deploy/radix/radix-deploy.workflow.yml`:
a repository that deploys with Radix copies it to `.github/workflows/` and sets the repository variables
`DEPLOY_METHOD` to `radix` (which also keeps the Container Apps workflow from running) and `RADIX_REGISTRY_NAME` to
its registry. Run it with the environment. It builds the four images from `osdu/deploy/docker`, tags each with the commit and with the environment (`dev`, `test`, or `latest` for
`prod`), pushes them, and asks Radix for a deploy pinned to the commit:
`rx create pipeline-job deploy --commitID <sha> --image-tag-name <component>=<sha>,...`. Radix runs exactly those
images, and the commit reaches every component as `RADIX_GIT_COMMIT_HASH`, which managed workers use to tell a rollout
from a restart.

The control plane is ready once `/health/ready` answers: the catalog and the `osdu` schema are migrated and verified.
Its start-up is done once `/health/startup` answers too: the partition its variables name is registered, and every
other step of the start-up pass is done (`GET /api/v1/bootstrap` names one that is not).

## After the first deploy

1. Sign in to the GUI as `admin` with `admin-password`.
2. Mint a personal access token with the `node` scope (the GUI's token page, or `POST /api/v1/me/tokens` with
   `{"name": "radix-workers", "scopes": ["node"]}`) and store it in the environment's vault as `node-token`. Until it
   exists, each worker the control plane starts exits at once and the pool backs off (up to 5 minutes between starts);
   the next start after it is stored polls.
3. Register the estate repository as a repo source; the control plane syncs it with `git-token`.
4. Watch the Nodes page: the default pool keeps one worker running and starts more for queued runs.

## Updating and promoting

Run the workflow again for the same environment, or for the next one: each environment gets the images of the commit
it was deployed from. A deploy rolls the control plane: for a few seconds a second replica runs beside the old one,
and a node call that reaches the one that does not own dispatch is refused and retried; the new control plane takes
over when the old one stops. Workers of the previous commit keep their runs; the new control plane starts their
replacements first and lets each old worker leave once it holds nothing ([managed workers](managed-workers.md)).

## When something does not start

| What you see | Why |
| --- | --- |
| The deploy is refused: a client id is not a GUID | A value in angle brackets was left in `radixconfig.yaml`. |
| A pod waits in `ContainerCreating` | A secret named by `secretRefs` is missing from the vault, or the identity cannot read it. |
| The control plane never becomes ready | It cannot reach or sign in to Azure SQL, the `OsduDelivery` database does not exist, or no partition is registered because the environment's variables name none; its log says which. |
| The control plane does not start, naming an `Osdu:Partitions` setting | A partition's setting is not one it takes: two defaults, a name that is no data-partition-id, or a secret's value where its `${keyvault:...}` reference belongs (the log never repeats the value). |
| Workers start and end at once; the Nodes page shows the pool failing | `node-token` is missing or lacks the `node` scope, or the worker cannot reach `SQLFLOW_OSDU_DB`; the job's log says which. |
| The chat answers that the assistant is not configured, or fails on its first tool | A Foundry setting is missing, the control plane identity lacks `Cognitive Services OpenAI User`, or Foundry cannot reach the MCP server's public address. |
| Sign-in fails in the GUI | The app registration's redirect URI is not the GUI's address, or `ControlPlane__AzureAd__ClientId` names another registration. |
