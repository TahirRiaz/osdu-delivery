# Authentication and identity

How the control plane signs users in, issues tokens, validates them, and administers users and roles is SQLFlow's,
unchanged, and documented in
[../../../../sqlflow/docs/reference/concepts/authentication-and-identity.md](../../../../sqlflow/docs/reference/concepts/authentication-and-identity.md):
local login, Microsoft Entra single sign-on, the device grant the CLI uses, personal access tokens, rolling
sessions, and the role and scope model.

This page covers what that means for OSDU Delivery.

## Who may operate the delivery surface

Authorization has two tiers. The `read`, `operate` and `author` policies all resolve to "authenticated", and only
`admin` consults the token's scope claim. So any signed-in user gets the whole operational product, the delivery
surface included: reading the ledger, triggering and cancelling runs, releasing and redelivering records, probing
a target, and saving templates. Only user administration, and pruning the ledger, are fenced off behind `admin`.

That is a deliberate platform choice rather than an OSDU one, and it has a consequence worth stating plainly for
this product: **a signed-in user can send records to a live OSDU platform and can remove delivered records.** The
gate is who gets an account, and with Entra sign-on that is the app registration's assignment requirement. The
bundled Bicep provisions the registration with an app role, `SqlFlow.User`, and assignment required, so nobody
signs in until a group or a user is assigned to it.

## Nodes have a scope of their own

A compute node is not a user. It authenticates to the control plane with a personal access token minted with the
`node` scope, which is what the dispatcher accepts for taking work, reporting outcomes and streaming a trace. The
scaler reads the same endpoint with the same token. Mint it from an account whose own scopes cover it, keep it out
of the catalog and the images, and supply it as `SQLFLOW_TOKEN` from a secret store.

The `node` scope can never be requested through the device grant, so a node credential is always a deliberate,
revocable act by an administrator.

## The ledger records who asked

Every delivery run and every intervention is attributed. The run row records who requested it, or the trigger
source (`schedule`, `manual`, `cli`) when nobody did, and the ledger's audit trail records the requesting user
against each release, redelivery, verification and removal, with the run or compute task it produced. Revoking an
account does not rewrite that history: it is the record of what was done to the data.

Because a personal access token's effective scopes are its own cap intersected with the owner's current role,
deactivating a person narrows or disables every token they hold without touching the token rows, and their name
stays on what they did.

## Local development shares the real user table

`dev.bat` runs against the estate's own catalog, so a local sign-in is the same account, with the same role, as
the deployed GUI. A first Entra sign-in provisions the user just in time with the configured default role, in the
shared catalog, so it is a real row in the real estate rather than a local one. `osdu/tools/dev-setup.ps1`
therefore copies the estate's signing key, issuer and audience into `.sqlflow/env`, which is what makes a token
minted by the cloud GUI valid locally, and deliberately writes no bootstrap admin: re-provisioning one would
rewrite the estate's admin password.

## See also

- [The control plane](control-plane.md): the delivery endpoints and the policy groups they are mapped into.
- [../../environment-variables.md](../../environment-variables.md): `SQLFLOW_TOKEN`, `SQLFLOW_URL` and the rest.
- [../../../deploy/README.md](../../../deploy/README.md): minting the node token as part of a first deployment.
