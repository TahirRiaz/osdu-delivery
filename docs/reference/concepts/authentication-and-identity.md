# Authentication and identity

The control plane offers several ways to sign in and issues one predominant kind of credential out of them: an HS256 bearer token. Because every token-minting path converges on the same token format and the same claims, authorization downstream is identical regardless of how the caller authenticated. Three of the paths are the interactive sign-in surfaces mapped under `/api/v1` (src/SqlFlow.ControlPlane/Api/AuthEndpoints.cs):

| Endpoint | Who | Availability |
| --- | --- | --- |
| `POST /api/v1/auth/login` | A local user (username and password held in the catalog) | Always mapped |
| `POST /api/v1/auth/exchange` | Microsoft Entra ID single sign-on (the SPA exchanges an Entra ID token) | Mapped when Entra is enabled (automatic once `AllowedTenantIds` and `ClientId` are set) |
| `POST /api/v1/auth/token` | The break-glass bootstrap secret | Mapped only when `ControlPlane:Jwt:BootstrapSecret` is set |

`GET /api/v1/auth/providers` is anonymous and tells the login page which of these are available. It returns `{ local, bootstrap, entra }`; when Entra is enabled, the `entra` object carries the `clientId` and resolved `authority` (see [More than one tenant](#more-than-one-tenant) for which authority that is) so the login page can configure MSAL without any client-side configuration file. Every sign-in response is sent with `Cache-Control: no-store` and `Pragma: no-cache`, so an intermediary never caches a bearer token.

Two further sign-in surfaces exist for clients that cannot drive an interactive login page:

- **The OAuth 2.0 device-authorization grant (RFC 8628)**, always mapped, whose client is the CLI's `sqlflow login --device` (src/SqlFlow.Cli/Remote/RemoteVerbs.cs and src/SqlFlow.Cli/Remote/ControlPlaneClient.cs). See [The device grant](#the-device-grant).
- **Personal access tokens (PATs)**, the long-lived bearer credential for headless clients (the CLI, automation). See [Personal access tokens](#personal-access-tokens).

## Tokens, scopes, and roles

`TokenIssuer` (src/SqlFlow.ControlPlane/Security/TokenIssuer.cs) signs tokens with HS256 using `ControlPlane:Jwt:SigningKey`, which must be at least 32 bytes; a shorter or missing key fails startup validation. Validation (src/SqlFlow.ControlPlane/Program.cs) pins the algorithm to HS256, checks issuer, audience, signature and lifetime, and allows 30 seconds of clock skew. Every token carries:

- `sub`: the subject (username, or the requested bootstrap subject, default `bootstrap`)
- `jti`: a unique token id
- `scope`: a space-delimited scope list, for example `read operate admin author`
- `iss` / `aud`: `ControlPlane:Jwt:Issuer` (default `sqlflow-control-plane`) and `ControlPlane:Jwt:Audience` (default `sqlflow`)

User-backed tokens (local login, Entra exchange, the device grant) additionally carry `role` (the role name) and `uid` (the catalog user id) so the GUI can shape itself without a second call. Interactive sessions (local login and Entra exchange) also carry `auth_time`: when the user actually proved who they are. Bootstrap tokens carry none of these. Expiry is `ControlPlane:Jwt:AccessTokenMinutes` (default 720, valid range 1 to 1440).

Authorization has exactly two tiers, defined in src/SqlFlow.ControlPlane/Program.cs. The `read`, `operate` and `author` policies all resolve to "authenticated"; only `admin` consults the token's `scope` claim. Any signed-in caller therefore gets the whole operational product: the read surface (catalog, repo trees and git history, runs and activity, search, schedules, nodes, repo sources, the summary, the caller's own `/me` resources, notifications, maintenance, compute tasks, the delivery ledger), the operate surface (catalog writes, triggering and cancelling runs, schedule and repo-source writes, node and compute-task control, the delivery actions), and the author surface (proposing flows to a repo source as a pull request). Only user and role administration is fenced off behind the `admin` scope. This is deliberate: a signed-in user is never stuck unable to use a feature the UI shows them, and elevating an account is only ever needed to let it administer other accounts. The `operate` and `author` scopes still exist on tokens and roles (a token minted with only `read` is usable across the operational surface), but no policy other than `admin` enforces a scope.

Roles are catalog rows (`CatalogRole` in src/SqlFlow.Catalog/CatalogEntities.cs) mapping a name to a scope string. `BootstrapProvisioningService` (src/SqlFlow.ControlPlane/Background/BootstrapProvisioningService.cs) seeds the built-in roles at startup through `UserStore.EnsureRoleAsync`, which only inserts a missing row and never overwrites an operator's scope edit:

| Role | Scopes |
| --- | --- |
| `admin` | `read operate admin author` |
| `operator` | `read operate author` |
| `viewer` | `read` |

When a session is issued, the user's role row is loaded and its scopes become the token's `scope` claim. If the role row no longer exists, sign-in fails closed with 403 `Role is not provisioned`; a user whose role was deleted never receives a fallback grant.

## Sessions roll, they do not expire under the user

`AccessTokenMinutes` is one token's life, not the user's. A signed-in GUI trades its token for a fresh one at `POST /api/v1/auth/renew` (mapped under the `read` policy) before the current one lapses, and again whenever OSDU Delivery is opened, so the person stays signed in for as long as they keep working while any single issued token (including a leaked one) stays short-lived. Ticking "Keep me signed in on this device" at sign-in puts the session in `localStorage` rather than `sessionStorage` (gui/src/auth/AuthContext.tsx), so it survives a browser restart and rolls on the next visit.

That tick is itself remembered, alongside the subject of the last successful sign-in, under the separate `sqlflow.login` key in `localStorage` (gui/src/auth/loginPrefs.ts). The login page prefills both. These outlive a sign-out on purpose: a session ending is precisely when someone would otherwise have to retype their own name, and it is what stops the "keep me signed in" choice from silently reverting on every visit. No secret is kept there, only the subject and the flag. `beginSession` records the prefill from the `subject` the server returned rather than from the typed field, so local and Entra sign-in are covered alike (the latter types no username at all) and a local sign-in records the account's canonical name rather than whichever casing was typed. Only a renewable (user-backed) session writes a prefill, so the break-glass path leaves no trace, and because `beginSession` only runs once the server has accepted a credential, a failed sign-in never becomes a prefill.

The renewal call is authenticated by the very token it replaces, so there is no second long-lived refresh credential to store or leak. Three things bound it:

- **`auth_time`**: carried unchanged through every roll, so the cap below is measured from the real sign-in and not from the newest token.
- **`ControlPlane:Jwt:SessionMaxDays`** (default 30): past this, renewal is refused with 401 `Session has reached its maximum age`, and a real sign-in is the only way back.
- **A re-read of the account on every roll**: a deactivated or removed account gets 401 `Account is no longer active`, and a role change or role deletion takes effect at the next renewal instead of lingering for the life of an issued token.

Renewal is refused outright with 403 `This credential does not renew` for any credential without `auth_time`: a personal access token (which carries its own lifetime), the break-glass bootstrap token, and a device-grant token, whose long-lived path is a personal access token instead.

## Local login

`POST /api/v1/auth/login` takes `{ "username": "...", "password": "..." }` and returns a session:

```json
{
  "accessToken": "eyJhbGciOiJIUzI1NiIs...",
  "tokenType": "Bearer",
  "expiresIn": 43200,
  "subject": "ops.lead",
  "role": "admin",
  "scopes": ["read", "operate", "admin", "author"]
}
```

Passwords are hashed with the ASP.NET Identity `PasswordHasher` (PBKDF2). The length rule and the hasher live in `LocalPasswords` (src/SqlFlow.Catalog/LocalPasswords.cs), so the API and the CLI's offline `sqlflow user reset-password` write interchangeable hashes. When a login verifies against an older hash format, the hash is transparently upgraded in place (rehash-on-login); a failed rehash is logged and the old, still valid hash remains. A successful login stamps the account's last-login time.

Two defenses protect the endpoint:

- **Timing hardening.** Exactly one password hash is always verified per attempt. When the username does not exist, the account is inactive, or the account is an SSO account with no password, a decoy hash is verified instead, so response time does not reveal whether a username exists. Both outcomes return the same generic 401 `Invalid username or password`.
- **Per-pair throttling.** `LoginThrottle` (src/SqlFlow.ControlPlane/Security/LoginThrottle.cs) tracks consecutive failures per (username, client IP) pair, with the username case-folded: 5 failures within a 900 second window lock the pair out for 300 seconds, during which login attempts get HTTP 429 `Too many failed sign-in attempts`. A successful sign-in clears the pair's history. The throttle is in-memory per node (capped at 100,000 tracked pairs) and complements the global per-IP rate limiter. Behind a reverse proxy, enable `ControlPlane:Proxy` with trusted `KnownNetworks`/`KnownProxies` so the client IP is the real caller rather than the proxy.

## Bootstrap tokens

`POST /api/v1/auth/token` is the break-glass path for a fresh deployment or automation that has no user yet. The endpoint exists only when `ControlPlane:Jwt:BootstrapSecret` is configured (at least 32 UTF-8 bytes, enforced at startup). The caller presents the exact secret, compared in fixed time:

```json
{
  "secret": "the-configured-bootstrap-secret-value",
  "subject": "ci-pipeline",
  "scopes": ["read", "operate"]
}
```

Requested scopes are filtered against the allowed set `read`, `operate`, `author`, `admin`; omitting `scopes` defaults to `["read"]`. If no requested scope is valid, the response is 400 `No valid scope requested` listing the allowed scopes. A wrong secret returns 401 `Invalid bootstrap secret`. The response is `{ accessToken, tokenType, expiresIn }` with no role or uid claim: a bootstrap session cannot renew and cannot own personal access tokens, and audit trails record it under its requested subject.

## The device grant

`POST /api/v1/auth/device` mints a device code (32 random bytes as hex) and a human-typed user code (eight characters from an alphabet without 0, O, 1 and I, shown as `XXXX-XXXX`), both valid for 600 seconds, and answers with `deviceCode`, `userCode`, `verificationUri` (`{origin}/device`), `verificationUriComplete` (the same URL with `?code=`), `expiresIn`, and a poll `interval` of 5 seconds. The request's space-delimited `scope` is filtered against `read`, `operate` and `author` (an empty request asks for all three); `admin` can never be requested. Start and `POST /api/v1/auth/device/token` polling are anonymous, since the opaque `device_code` is the only secret the polling client holds. Until the flow resolves, the token endpoint answers with the RFC 8628 error envelope on HTTP 400: `authorization_pending`, `slow_down` when polled faster than the interval, `access_denied`, `expired_token` for an unknown or lapsed code, and `invalid_request`.

Approval and denial (`POST /api/v1/auth/device/approve` and `POST /api/v1/auth/device/deny`, body `{ "userCode": "..." }`) run under an authenticated session (the `read` policy), so a human binds their own identity to the device. The granted scopes are the requested ones intersected with the approver's own, minus `admin`; a session holding none of them gets 403 `Insufficient scope`. The next poll then mints a normal HS256 token for the approving user, with `role` and `uid` but without `auth_time`, scoped to that grant (the response uses the OAuth field names `access_token`, `token_type`, `expires_in`, `scope`), and the entry is removed. `GET /device` (src/SqlFlow.ControlPlane/Security/DeviceApprovalPage.cs) is a self-contained page the control plane serves as the advertised approval URL: it takes the code plus a local username and password, signs in through `POST /api/v1/auth/login`, and posts the approval. Pending grants live only in process memory (`DeviceCodeStore`), so a control-plane restart abandons them.

The CLI drives the whole exchange: `sqlflow login --device` prints the approval URL and the code, polls until the grant is approved or denied, and immediately uses the short-lived token to mint a personal access token, which is what it stores (see [the control-plane verbs](../cli/control-plane.md)). tests/SqlFlow.ControlPlane.Tests/CliControlPlaneBinaryTests.cs exercises that path against the built binary.

## Personal access tokens

A PAT is a long-lived bearer credential for headless clients rather than a short-lived session token. A PAT and an HS256 token share one `Authorization: Bearer` header: the policy scheme `PersonalAccessTokenDefaults.PolicyScheme` (src/SqlFlow.ControlPlane/Program.cs) forwards a value that starts with `sqlf_` to `PersonalAccessTokenHandler` and everything else to JWT validation, so authorization downstream sees one principal shape (`sub`, `scope`, `role`, `uid`) regardless of which credential arrived.

`AccessTokenGenerator` (src/SqlFlow.ControlPlane/Security/AccessTokenGenerator.cs) mints the secret as `sqlf_` followed by 256 bits of randomness in URL-safe base64. Only its SHA-256 hash and a 12-character display prefix are stored (`CatalogAccessToken` in src/SqlFlow.Catalog/CatalogEntities.cs); the cleartext is returned exactly once at creation and is never retrievable again. At authentication (src/SqlFlow.ControlPlane/Security/PersonalAccessTokenHandler.cs) the presented value is hashed and looked up; an unknown, revoked or expired token, or one whose owner is inactive or whose role no longer exists, all fail with the same answer. The effective scopes are the token's own cap intersected with the owner's current role scopes, so demoting or deactivating the owner narrows or disables every token they hold without touching the token rows. The token's last-used time is refreshed at most every five minutes.

PATs are self-managed through the `/me` endpoints (src/SqlFlow.ControlPlane/Api/MeEndpoints.cs), mapped under the `read` policy:

| Endpoint | Purpose |
| --- | --- |
| `GET /api/v1/me` | Who the presented credential is: subject, role, effective scopes, and the backing user id (null for a bootstrap token). The CLI's `sqlflow whoami` |
| `GET /api/v1/me/tokens` | The caller's own tokens, newest first, active and revoked alike |
| `POST /api/v1/me/tokens` | Create one: `{ name, scopes, expiresInDays }`. Scopes are capped server-side to the caller's own (omitted means all of them); expiry is 1 to 3650 days, or null for never; the name is at most 200 characters |
| `DELETE /api/v1/me/tokens/{id}` | Revoke one of the caller's own active tokens (404 for any other) |

A session without a `uid` (the bootstrap token) gets 400 `No user account` from the token endpoints. Deleting a user removes their tokens outright.

## Microsoft Entra ID single sign-on

Entra single sign-on turns on automatically once `ControlPlane:AzureAd:AllowedTenantIds` (at least one entry) and `ControlPlane:AzureAd:ClientId` are configured: supplying both credentials is the whole switch, with no separate enable flag to remember. (Set `ControlPlane:AzureAd:Enabled` explicitly only to override the default: `true` requires SSO and fails startup if a credential is missing, `false` forces it off even when credentials are present.) Local username/password sign-in is always available alongside it; enabling Entra only adds the "Sign in with Microsoft" option. When enabled, the SPA signs the user in against Entra with MSAL (authorization code + PKCE) and posts the resulting ID token to `POST /api/v1/auth/exchange` as `{ "token": "..." }`. The control plane validates the Entra token and issues its own session token, so downstream API calls use one token type regardless of sign-in method.

### More than one tenant

`AllowedTenantIds` is a list, not a single value, so an estate can trust users from more than one Entra tenant at once (for example, the app registration's own home tenant plus a customer organization's corporate tenant). Two things have to agree for a foreign tenant to actually work:

- **The app registration itself must be multi-tenant** (`signInAudience: AzureADMultipleOrgs`). With more than one allowed tenant, MSAL authenticates the SPA against the shared `organizations` authority (`ResolveAuthority()`, src/SqlFlow.ControlPlane/Configuration/ControlPlaneOptions.cs), which accepts a sign-in attempt from any Entra work/school tenant; a single-tenant app registration rejects a foreign account at Microsoft's own login page (`AADSTS650059`) before a token is ever issued, regardless of what the control plane is configured to allow.
- **Each additional tenant's own admin must consent to the app once.** Consent is what creates that tenant's own local enterprise-application object; without it, Entra refuses to issue a token for that tenant at all. If the deployment also enforces app-role assignment, that tenant's admin then assigns the role to their own users or groups, entirely within their own directory; this deployment's credentials never reach into a foreign tenant.

The control plane's `AllowedTenantIds` is the actual trust boundary on the backend: a multi-tenant app registration will let any consenting tenant obtain a token, but `EntraTokenValidator` only accepts one whose `tid` claim is in this list, so unnamed tenants are rejected token by token even after they have consented to the app.

**Adding a second tenant changes how existing B2B guests sign in, and that is usually the deciding factor.** With exactly one allowed tenant the authority is pinned to it, so an external address (a consultant's `@partner.com`) resolves as a guest of that tenant: Entra delegates the password check to their home tenant but issues the token from the pinned one, needing no consent anywhere and no separate credential. Adding a second tenant forces the shared `organizations` authority, and the same address now resolves to the person's own home tenant, where this app is unknown; they get an admin-approval wall, and their guest identity no longer applies. So an estate whose external users are guests should stay single-tenant and invite people as guests, which needs no cooperation from any outside directory. Reach for a second tenant only when a whole partner organization should sign in with its own accounts, and accept that each such tenant's admin must consent and assign the app role themselves.

`deploy/bicep/main.bicep` automates this: `provisionEntraApp` (default on) creates the app registration via `entra-app.bicep`, always trusting the deployment's own subscription tenant; `azureAdAdditionalAllowedTenantIds` names further tenants, which automatically flips the registration to multi-tenant and is surfaced back as the `entraForeignTenantReminder` output (the manual consent-then-assign step each named tenant's admin still has to do by hand).

`EntraTokenValidator` (src/SqlFlow.ControlPlane/Security/EntraTokenValidator.cs) reads the token's own `tid` claim first (unvalidated) to pick which allowed tenant to validate against, rejecting outright (before any metadata fetch) when `tid` is not in `AllowedTenantIds`, then pins validation to that tenant:

- **Issuer**: that tenant's own issuer, taken from its OIDC metadata at `{TenantAuthority(tid)}/.well-known/openid-configuration`, so a token asserting any other tenant (even an allowed one, if it lied about `tid`) fails signature/issuer validation against the metadata actually fetched
- **Audience**: `ControlPlane:AzureAd:ClientId`
- **Signature**: that tenant's JWKS, RS256 only, with a single refresh-and-revalidate when the key is unknown (key rollover); a still-unknown key after refresh is an untrusted token. Each allowed tenant gets its own cached metadata manager, refreshed independently.
- **Lifetime**: with 30 seconds of clock skew

Any validation failure returns 401 `Invalid identity token`, as does a valid token that lacks `oid` or a username claim. An OIDC metadata outage (network failure, misconfigured authority) is logged as an outage and fails closed with the same 401; it never falls back to cached-forever keys or unsigned acceptance.

Users are keyed on the immutable `oid` claim (`ExternalObjectId`), so a UPN rename never duplicates a user: the row's sign-in name follows the token unless another account already owns the new name, in which case the old name is kept and the sign-in still succeeds. The sign-in name is taken from `preferred_username`, falling back to `email` then `upn`; the display name from `name`; the email from `email`, or from the sign-in name when it contains `@`. First sign-in provisions the user just in time with `ControlPlane:AzureAd:DefaultRole` (default `viewer`, least privilege; an admin raises it afterwards). Exchange-specific errors:

| Status | Title | Meaning |
| --- | --- | --- |
| 403 | `Account is deactivated` | The account exists but an administrator deactivated it |
| 409 | `Sign-in name already in use` | A different account already uses this sign-in name; an administrator must resolve it |
| 500 | `Identity is not provisioned` | The configured default role does not exist; bootstrap provisioning has not completed |

Because the Entra token already proved who the caller is, these account-state responses are safe to disclose (unlike local login's generic 401).

## User and role administration

The admin surface (src/SqlFlow.ControlPlane/Api/UserEndpoints.cs) requires the `admin` scope:

| Endpoint | Purpose |
| --- | --- |
| `GET /api/v1/users` | Paged list, filterable by `username`, `role`, `provider`, `active` |
| `GET /api/v1/users/{id}` | One user |
| `POST /api/v1/users` | Create a local user (`{ username, password, role, email, displayName }`) |
| `POST /api/v1/users/{id}/profile` | Rename and rewrite display name and email (`{ username, email, displayName }`; a blank optional field clears it) |
| `POST /api/v1/users/{id}/role` | Change role (`{ role }`) |
| `POST /api/v1/users/{id}/activate` | Reactivate |
| `POST /api/v1/users/{id}/deactivate` | Deactivate |
| `POST /api/v1/users/{id}/password` | Reset a local user's password (`{ password }`) |
| `DELETE /api/v1/users/{id}` | Delete permanently |
| `GET /api/v1/roles` | List roles with their scopes and descriptions |

Rules enforced by the endpoints and the store (src/SqlFlow.Catalog/UserStore.cs, where every mutation runs in one serializable transaction so concurrent admin actions cannot race the catalog into a lockout):

- Local passwords must be at least 12 characters (`LocalPasswords.MinLength`); shorter ones get 400 `Password too short`. Length is the only composition rule; character-class requirements are deliberately not imposed.
- The username and display name are capped at 256 characters and the email at 320, matching the catalog columns; an over-long field gets 400 `Field too long`.
- Deactivation is the reversible removal and keeps the account attributable. Delete is permanent: it takes the user's private rows (access tokens, notification subscriptions and delivery history) with it, while run history and activity events keep naming the actor. An admin cannot delete the account they are signed in as (409 `That is your own account`).
- The last active admin can never be demoted, deactivated or deleted; the attempt gets 409 `Last active admin`, so the catalog cannot be administered into a lockout.
- Setting a password on an SSO account, or renaming one, returns 409 `Not a local user`: the credential and the sign-in name live in Entra (the name is refreshed at every sign-in). An SSO account's display name and email can still be edited.
- Creating a user with an unknown role returns 400 `Unknown role`; a duplicate username, on create or rename, returns 409 `Username already in use`.

SSO users never appear via `POST /users`; they are provisioned by their first sign-in and only governed here. Without an admin session, the CLI's offline `sqlflow user reset-password <username> [--db <conn-ref>]` resets a local password straight against the catalog (hidden prompt, never a flag); it refuses SSO users too.

## First-run provisioning

`BootstrapProvisioningService` runs at startup and retries with backoff (5, 10, 20, 40, then 60 seconds) until the catalog database is reachable, so a control plane that starts before its database converges instead of crashing. In order, and all idempotently: it provisions the catalog schema from the EF model, seeds the three built-in roles, creates the initial admin, registers the optional demo repo source (`ControlPlane:Bootstrap:DemoRepo`), and seeds the default worker pool with a floor of one replica when no desired state exists yet.

Provisioning follows two switches. With `ControlPlane:Bootstrap:ApplyMigrations` false, an unprovisioned or stale database is only logged as a warning. Otherwise the default (`AllowCreate` false) initialises an existing but empty database and verifies it: a missing database, a populated database that is not a catalog, or one missing tables this build declares, stops bootstrap with a critical log instead of provisioning against the wrong server, and readiness stays red. `ControlPlane:Bootstrap:AllowCreate=true` opts into creating the database itself, for first-time provisioning and ephemeral test databases.

The initial admin is created when `ControlPlane:Bootstrap:AdminUsername` and `ControlPlane:Bootstrap:AdminPasswordReference` are configured and the user is absent. The password reference is a secret reference (`${env:...}` or `${keyvault:...}`) resolved through the secret resolver, and it only seeds the first credential; an existing admin's password is never reset by configuration. An admin password shorter than 12 characters or an empty resolved secret aborts admin creation with an error log, and the two bootstrap settings must be set together (startup validation fails otherwise). With no users and no configured admin, a startup warning notes that only the bootstrap secret can access the API.

## Configuration touchpoints

All settings live in the `ControlPlane` configuration section (appsettings or environment variables in the `ControlPlane__Jwt__SigningKey` double-underscore form) and are validated at startup by `ControlPlaneOptions.Validate` (src/SqlFlow.ControlPlane/Configuration/ControlPlaneOptions.cs); a misconfigured deployment never starts serving.

| Setting | Default | Purpose |
| --- | --- | --- |
| `ControlPlane:Jwt:Issuer` | `sqlflow-control-plane` | Token issuer claim |
| `ControlPlane:Jwt:Audience` | `sqlflow` | Token audience claim |
| `ControlPlane:Jwt:SigningKey` | none, required | HS256 key, at least 32 bytes, sourced from a secret |
| `ControlPlane:Jwt:AccessTokenMinutes` | `720` | One token's lifetime, 1 to 1440. Not how long a user stays signed in: the GUI rolls its token at `POST /auth/renew` |
| `ControlPlane:Jwt:SessionMaxDays` | `30` | Absolute ceiling on a rolling session, measured from the actual sign-in, 1 to 365 |
| `ControlPlane:Jwt:BootstrapSecret` | unset | Enables `POST /auth/token`; at least 32 bytes when set |
| `ControlPlane:AzureAd:Enabled` | unset (auto) | Explicit override. Unset: SSO auto-enables when `AllowedTenantIds` and `ClientId` are both set. `true`: require SSO (startup error if a credential is missing). `false`: force off even with credentials |
| `ControlPlane:AzureAd:AllowedTenantIds` | none | The Entra tenant(s) whose users may sign in (a list; `__0`, `__1`, ... in env-var form; no blank entries). Setting this and `ClientId` auto-enables SSO. More than one entry requires the app registration itself to be multi-tenant |
| `ControlPlane:AzureAd:ClientId` | none | The SPA app registration; the required token audience; setting this and `AllowedTenantIds` auto-enables SSO |
| `ControlPlane:AzureAd:Authority` | `https://login.microsoftonline.com` | The authority host. Override only for sovereign clouds (for example `https://login.microsoftonline.us`) |
| `ControlPlane:AzureAd:DefaultRole` | `viewer` | Role JIT-provisioned Entra users receive; must not be blank |
| `ControlPlane:Bootstrap:ApplyMigrations` | `true` | Whether startup provisions the catalog schema from the EF model |
| `ControlPlane:Bootstrap:AllowCreate` | `false` | Whether startup may create the catalog database and initialise an empty one; off, it migrates an existing catalog only |
| `ControlPlane:Bootstrap:AdminUsername` | unset | Initial admin sign-in name; set with the password reference |
| `ControlPlane:Bootstrap:AdminPasswordReference` | unset | Initial admin password as a `${env:...}` / `${keyvault:...}` reference |
| `ControlPlane:Bootstrap:DemoRepo` | unset | An optional repo source (`Name`, `RemoteUrl`, `Branch`, `SyncIntervalSeconds`) registered at every start |
| `ControlPlane:Proxy:Enabled` | `false` | Honor `X-Forwarded-For` from the trusted `KnownNetworks`/`KnownProxies` so the throttle and rate limiter key on real client IPs |

## Example: bootstrap a fresh deployment and create the first users

```bash
# 1. Get a break-glass admin token with the configured bootstrap secret.
curl -s -X POST https://osdu-delivery.example.com/api/v1/auth/token \
  -H "Content-Type: application/json" \
  -d '{"secret": "'"$SQLFLOW_BOOTSTRAP_SECRET"'", "subject": "setup", "scopes": ["read", "operate", "admin"]}'
# -> { "accessToken": "...", "tokenType": "Bearer", "expiresIn": 43200 }

# 2. Create a local operator (password must be at least 12 characters).
curl -s -X POST https://osdu-delivery.example.com/api/v1/users \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"username": "ops.lead", "password": "a-long-strong-passphrase", "role": "operator", "email": null, "displayName": "Ops Lead"}'

# 3. Sign in as that user; the session token carries role and uid claims.
curl -s -X POST https://osdu-delivery.example.com/api/v1/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username": "ops.lead", "password": "a-long-strong-passphrase"}'
```

The end-to-end behavior of this surface is exercised in tests/SqlFlow.ControlPlane.Tests/IdentityApiTests.cs (providers discovery, login success and throttle lockout, Entra exchange with JIT provisioning, session renewal, the admin API, role and admin seeding), tests/SqlFlow.ControlPlane.Tests/UserStoreTests.cs (the store's uniqueness, last-admin, SSO and delete guards), tests/SqlFlow.ControlPlane.Tests/AuthAndSurfaceTests.cs (bootstrap token issuance and its use on the read surface), and tests/SqlFlow.ControlPlane.Tests/CliControlPlaneBinaryTests.cs (the device grant and credential storage through the CLI).

## See also

- [Control plane](./control-plane.md)
- [Control-plane CLI verbs](../cli/control-plane.md): `sqlflow login`, `logout` and `whoami`
- [Deployment guide](../guides/deployment.md)
- [Environment variables and secrets](../../environment-variables.md)
