# Reference

Reference pages for OSDU Delivery. The platform underneath is SQLFlow's, and SQLFlow documents it in the vendored
tree; the pages here cover only what OSDU Delivery adds or changes, and link across rather than repeat.

Every environment variable and secret reference is listed once, in
[../environment-variables.md](../environment-variables.md). The shape of the whole thing is in
[../architecture.md](../architecture.md), and the delivery domain itself in [../README.md](../README.md).

## CLI

| Page | Covers | SQLFlow's page |
| --- | --- | --- |
| [cli/delivery.md](cli/delivery.md) | `sqlflow check`, `sqlflow cache`, `sqlflow template`, and the run options an OSDU flow takes. | (OSDU only) |
| [cli/validate.md](cli/validate.md) | What `sqlflow validate` checks in a delivery, retrieval, cache or mapping document. | [validate](../../../sqlflow/docs/reference/cli/validate.md) |
| [cli/db.md](cli/db.md) | The `osdu` module database in `sqlflow db migrate` and `db status`. | [db](../../../sqlflow/docs/reference/cli/db.md) |
| [cli/worker.md](cli/worker.md) | What an OSDU Delivery node needs beyond a SQLFlow node. | [worker](../../../sqlflow/docs/reference/cli/worker.md) |
| [cli/control-plane.md](cli/control-plane.md) | Triggering and following OSDU runs from a terminal. | [control-plane](../../../sqlflow/docs/reference/cli/control-plane.md) |
| [cli/auth.md](cli/auth.md) | Which OSDU Delivery paths the Azure credential `sqlflow auth` checks is used by. | [auth](../../../sqlflow/docs/reference/cli/auth.md) |

## Concepts

| Page | Covers | SQLFlow's page |
| --- | --- | --- |
| [concepts/control-plane.md](concepts/control-plane.md) | The delivery API surface, the module's background services and its configuration. | [control-plane](../../../sqlflow/docs/reference/concepts/control-plane.md) |
| [concepts/authentication-and-identity.md](concepts/authentication-and-identity.md) | Who may operate the delivery surface, and what the ledger records about them. | [authentication-and-identity](../../../sqlflow/docs/reference/concepts/authentication-and-identity.md) |

## Guides

| Page | Covers | SQLFlow's page |
| --- | --- | --- |
| [guides/deployment.md](guides/deployment.md) | Deploying OSDU Delivery: the three images, the tiers, and triggering from an external scheduler. | [deployment](../../../sqlflow/docs/reference/guides/deployment.md) |
| [guides/notifications.md](guides/notifications.md) | Failure notifications for delivery, retrieval and cache flows. | [notifications](../../../sqlflow/docs/reference/guides/notifications.md) |

The deployment assets themselves, with their own README, are in [../../deploy/](../../deploy/README.md).
