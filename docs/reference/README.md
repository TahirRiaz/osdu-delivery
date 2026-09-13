# Platform reference

Reference pages for the platform OSDU Delivery runs on: the CLI verbs, the control plane and its
authentication, deployment, and notifications. The delivery domain itself (the flow and mapping documents, the
drop contract, the ledger, the protocols, the operations) is documented under [../delivery/](../delivery/README.md),
and the platform's shape under [../architecture.md](../architecture.md). Every environment variable and
secret reference is listed once, in [../environment-variables.md](../environment-variables.md).

## CLI

| Page | Covers |
| --- | --- |
| [cli/delivery.md](cli/delivery.md) | `sqlflow check`, `sqlflow snapshot`, `sqlflow template`, and the delivery run options of `run` and `trigger`. |
| [cli/validate.md](cli/validate.md) | `sqlflow validate`: a flow or mapping document, or a whole estate, checked offline. |
| [cli/db.md](cli/db.md) | `sqlflow db migrate`, `status`, `sync`: the catalog schema and the projection of a local estate into it. |
| [cli/worker.md](cli/worker.md) | `sqlflow worker`: the compute node, its claim loop, failure handling, and shutdown. |
| [cli/control-plane.md](cli/control-plane.md) | The remote verbs: signing in, runs and groups, schedules, repos, pipelines, search, nodes. |
| [cli/auth.md](cli/auth.md) | `sqlflow auth`: verifying Azure authentication for Key Vault and storage access. |

## Concepts

| Page | Covers |
| --- | --- |
| [concepts/control-plane.md](concepts/control-plane.md) | The API surface, configuration, bootstrap, the managed git sync, triggering and cancelling runs. |
| [concepts/authentication-and-identity.md](concepts/authentication-and-identity.md) | Tokens, scopes and roles, local login, Entra ID sign-on, the device grant the CLI uses, user administration. |

## Guides

| Page | Covers |
| --- | --- |
| [guides/deployment.md](guides/deployment.md) | Docker compose, Kubernetes with KEDA-scaled worker pools, Azure Container Apps with Bicep. |
| [guides/notifications.md](guides/notifications.md) | Failure notifications: subscriptions, immediate or digest pacing, the email and Slack channels, lifecycle gating. |
