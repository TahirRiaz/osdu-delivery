# Failure notifications: email and Slack alerts, estate digests, and anti-spam controls

The control plane watches the run history and tells the users who asked when something goes wrong: a run failed, was cancelled, or was skipped because an upstream member of its run group did not succeed. Subscriptions are strictly opt-in and per user: nobody receives a message until they create one, and every subscription belongs to exactly one account. Independently of subscriptions, the control plane writes an estate digest per period (daily by default), the standing record of what went wrong in each window, readable in the GUI and sendable on demand.

## The pipeline at a glance

Every poll tick (30 seconds by default) the notification service (`src/SqlFlow.ControlPlane/Background/NotificationService.cs`) runs claim-based phases over the catalog, so any number of control-plane replicas can run it without double-sending, and a restart never loses a message:

1. **Detect.** Terminal runs with status `failed`, `cancelled` or `skipped` become rows in `catalog.NotificationEvent` (`NotificationStore.DetectAsync`), one event per run, bounded by a watermark over `Run.WrittenUtc` that re-scans an overlap window each tick. Runs of flows declared `lifecycle: development` are skipped here, at the source. Detection runs whether or not anyone subscribes, so the watermark keeps pace and the event table doubles as an audit of what would have been notified.
2. **Digest.** When the scheduled estate digest is due, one replica claims the window (compare-and-swap on the watermark's due instant) and writes a `catalog.NotificationDigest` covering every event since the previous digest.
3. **Dispatch.** Each due subscription is claimed (compare-and-swap on its next-due instant, the same discipline the scheduler uses) and everything pending since its private cursor is composed into ONE message: grouped per flow with repeat counts ("failed x12"), the latest error excerpt (300 characters), and links into the GUI when `GuiBaseUrl` is set. The cursor advances over everything considered, so no event is ever reported twice to the same subscription; a backlog larger than 500 events continues in the next message.
4. **Send.** Composed messages sit in a durable outbox (`catalog.NotificationDelivery`). The send loop claims a row, hands it to the channel (email or Slack), and records the outcome. Transient failures retry with backoff (1, 5, 15, then 60 minutes; 5 attempts by default); permanent ones (an unknown Slack user, a recipient the relay rejects with a 5xx) are recorded as failed immediately. A message whose channel is not configured on this control plane is failed with a message saying exactly that.
5. **Housekeeping** (every 15 minutes). A delivery stuck in `sending` for more than 10 minutes (its node died mid-send) is requeued, and aged rows are pruned in batches of 5000.

## Pacing: right away, or as a digest

A subscription chooses one of two modes; both deliver ONE combined message per window, never one message per event:

- **`immediate`**: the first matching event after a quiet period alerts within one poll tick. After a message goes out, the subscription holds for its **cooldown** (5 minutes by default, 0 to 1440): everything that happens inside the cooldown is coalesced into the next message, never dropped. A failure storm therefore caps at one message per cooldown window per subscription.
- **`digest`**: one summary per fixed interval (six hours by default, 5 minutes to one week), covering everything since the previous digest. Empty windows send nothing, and windows missed while the host was down are folded into the next message rather than backfilled one by one.

## What a subscription selects

- **Channel**: `email` or `slack`, fixed for the subscription's lifetime (create several subscriptions to combine, for example an immediate Slack DM plus a daily email digest). A channel is offered only when the control plane has it configured.
- **Event kinds**: any of `run_failed`, `run_cancelled`, `run_skipped` (`NotificationEventKinds` in `src/SqlFlow.Catalog/CatalogEntities.cs`). New subscriptions default to `run_failed` alone; the cancelled and skipped echoes are opt-in by design.
- **Flow filter**: optional comma-separated wildcard patterns over flow names (`recall-*, wellbore-??-load`; `*` matches any run of characters, `?` one), matched case-insensitively, at most 400 characters. Empty means every flow.
- **Destination**: email subscriptions use the account email unless an override address is set (so a directory-driven address change follows automatically); Slack subscriptions post to an explicit channel id (`C0123ABCD`) or, when none is set, direct-message the user (their Slack account is resolved from their email). A subscription with no resolvable destination records its message as failed with the reason, instead of silently sending nothing.
- **Enabled**: a disabled subscription keeps its settings but sends nothing; re-enabling fast-forwards past everything that happened while it was off, so resuming never floods.

Opting in never replays history: a new subscription's cursor starts at the newest existing event. One account may hold at most 20 subscriptions.

## Estate digests

The scheduled digest is generated whether or not anybody subscribes or any channel is configured (`DigestEnabled`, on by default). Its boundaries are aligned to the Unix epoch rather than to process start, so the daily default lands at the same clock time every day (`DigestOffsetMinutes` moves it inside the period: 300 means 05:00 UTC), and a host that was down across several boundaries produces one digest covering everything it missed. The generator chains on its own event cursor, so an event detected late still lands in the next digest instead of falling between two windows; a window with more than 5000 events carries the remainder into the next one and is marked truncated. An empty window still produces a digest ("no failures"), because that is the answer the reader came for.

Anyone who can read runs can list and open digests, generate one on demand over a period they choose (5 minutes to 30 days, ending no later than now; a manual digest never touches the scheduled cursor), and send a digest through one of their own subscriptions, which supplies the channel and destination. The stored bodies go out as composed, so what was read in the GUI is what the recipient gets. Digests are kept for a year by default and hold their per-flow rows, so they still read in full after the events behind them have been pruned.

## Lifecycle gating: development flows never alert

Every flow document can declare a top-level `lifecycle:` in its envelope, next to `flowType` and `name` (read once for every kind by `src/SqlFlow.Yaml/YamlDocumentLoader.cs`; the accepted values are `production` and `development`). `production` (the default) alerts subscribers on failure; `development` runs, schedules, and records history identically but generates no notification events at all, so iterating on a half-built flow cannot page anyone:

```yaml
flowType: delivery
name: recall-welllog
lifecycle: development   # remove (or set to production) when the flow goes live

source:
  location: abfss://lake@account.dfs.core.windows.net/osdu-prepare/{logSource}
  manifest: manifest.json

render:
  mapping: WellLog@1.4.0

target:
  endpoint: ${env:PETRODB_URL}
  protocol: osduWellLog
```

The rest of the document is the delivery flow as described in [documents.md](../../delivery/documents.md) (`samples/recall-welllog/flows/recall-welllog.yaml` is the complete sample). The gate is applied at detection time against the synced catalog pipeline, so it takes effect on the next catalog sync after the YAML changes. A run whose pipeline has left the estate entirely still alerts: that failure is real and nobody declared it development.

## Configuration

Everything lives under `ControlPlane:Notifications` (appsettings or `ControlPlane__Notifications__...` environment variables; `NotificationOptions` in `src/SqlFlow.ControlPlane/Configuration/ControlPlaneOptions.cs`). The service always runs; the email channel is registered only when `Email:Provider` is not `none`, the Slack channel only when `Slack:BotTokenReference` is set, and the GUI hides what cannot send. Secrets are `${env:...}` / `${keyvault:...}` references resolved at send time (never literals), so rotating a password or token heals in-flight retries without a restart. The values below are the defaults, except the sample addresses and references.

```jsonc
"Notifications": {
  "Enabled": true,                  // master switch: off means nothing is detected, digested, or sent
  "PollSeconds": 30,                // tick cadence = the immediate-mode latency bound
  "DetectionOverlapMinutes": 30,    // re-scan window absorbing writer clock skew (dedup makes it free)
  "MaxDeliveryAttempts": 5,         // 1 to 10
  "EventRetentionDays": 30,         // detected events (audit) are pruned after this
  "DeliveryRetentionDays": 90,      // sent/failed messages are pruned after this; queued ones never
  "DigestEnabled": true,            // the scheduled estate digest
  "DigestIntervalMinutes": 1440,    // 5 to 10080; 1440 = one digest a day
  "DigestOffsetMinutes": 0,         // minutes past the aligned UTC boundary; less than the interval
  "DigestRetentionDays": 365,
  "GuiBaseUrl": "https://osdu-delivery.example.com",   // enables "Open run" / "Open flow" links in messages
  "Email": {
    "Provider": "none",             // none | smtp | graph
    "FromAddress": "osdu-delivery@example.com",   // required whenever a provider is set
    "FromDisplayName": "OSDU Delivery",
    "Smtp": {
      "Host": "smtp.example.com",   // required for smtp
      "Port": 587,
      "SslMode": "starttls",        // starttls | ssl | auto | none
      "UsernameReference": "${env:SQLFLOW_SMTP_USER}",       // set both or neither
      "PasswordReference": "${env:SQLFLOW_SMTP_PASSWORD}",
      "TimeoutSeconds": 30
    },
    "Graph": {
      "SenderId": "alerts@contoso.com",          // the sending mailbox (UPN or object id); required for graph
      "TenantId": null,                          // set all three for an explicit app registration,
      "ClientId": null,                          // or leave all three null to use the ambient Azure credential
      "ClientSecretReference": null,             // (managed identity in Azure)
      "BaseUrl": "https://graph.microsoft.com/v1.0",       // override only for sovereign clouds
      "Scope": "https://graph.microsoft.com/.default"      // must match BaseUrl's cloud
    }
  },
  "Slack": {
    "BotTokenReference": "${env:SQLFLOW_SLACK_BOT_TOKEN}",   // xoxb-..., scope chat:write
    "BaseUrl": "https://slack.com/api/"
  }
}
```

`Validate()` refuses to start on an out-of-range value, an unknown provider or SSL mode, a provider without `FromAddress`, an SMTP username without a password (or the reverse), a Graph configuration with only some of the three app-registration values, or a `GuiBaseUrl` that is not an absolute http(s) URL.

### Email via SMTP

Any relay works: set `Provider` to `smtp`, the host/port/TLS mode, and (for an authenticating relay) the credential references. Messages are multipart/alternative (plain text plus HTML), sent through MailKit (`src/SqlFlow.ControlPlane/Notifications/SmtpEmailSender.cs`) in one short connect, authenticate, send, disconnect conversation per message. A 5xx SMTP rejection is permanent; a 4xx, and any connect, TLS or authentication failure, is retried with the backoff, so a rotated password heals the retries once the secret is fixed.

### Email via Microsoft Graph

For Microsoft 365 estates, `Provider: graph` sends through `POST /users/{SenderId}/sendMail` over plain HTTP (`src/SqlFlow.ControlPlane/Notifications/GraphEmailSender.cs`): no SMTP relay, no mailbox password, nothing saved to Sent Items. The identity calling Graph needs the **application** permission `Mail.Send` (admin-consented), ideally bounded to the sending mailbox with an Exchange application access policy. Identity options:

- **Managed identity / ambient credential** (recommended in Azure): leave `TenantId`/`ClientId`/`ClientSecretReference` null; the control plane's own Azure credential (the same `SQLFLOW_AZURE_AUTH` intent the engine uses) calls Graph.
- **Explicit app registration**: set all three values; the client secret is a secret reference, re-resolved after a token failure so a rotated secret heals in-flight retries.

A 429 or 5xx from Graph is retried; any other 4xx (a bad recipient, a missing `Mail.Send` grant, an unknown sender) fails the message at once.

### Slack

Create (or reuse) a Slack app with a bot token (`src/SqlFlow.ControlPlane/Notifications/SlackApiClient.cs`). Required scopes: `chat:write` to post; `users:read.email` and `im:write` if subscribers will use direct messages (the user's DM conversation is resolved from their email and cached in-process for twelve hours); and invite the bot to any explicit channel it should post into. Messages are Block Kit with the plain text as the notification fallback. A rate limit or 5xx is retried; `channel_not_found`, `not_in_channel`, `is_archived`, an unknown user and the other destination errors fail the message at once.

## Using it

In the GUI, **Notifications** in the settings navigation (also **Notification settings** in the account menu, `/settings/notifications`) creates and edits subscriptions, shows the recent delivery history (including why a message failed), sends a test message per subscription, and lists the estate digests with their per-flow breakdown, with generate-on-demand and send actions. The same surface is available on the API for automation, under the `read` scope and always for the caller's own account:

| Endpoint | What it does |
| --- | --- |
| `GET /api/v1/me/notifications/options` | Channel availability (and the email provider), valid kinds and modes with their defaults, the estate digest settings, and your account email |
| `GET/POST /api/v1/me/notifications/subscriptions` | List / create subscriptions |
| `PUT/DELETE /api/v1/me/notifications/subscriptions/{id}` | Edit / remove one (the channel is not editable; an empty string clears the flow pattern or an address) |
| `POST /api/v1/me/notifications/subscriptions/{id}/test` | Queue a test message through the real outbox; answers 202 with the delivery id |
| `GET /api/v1/me/notifications/deliveries?take=50` | Recent messages: status, attempts, and the last error |
| `GET /api/v1/notifications/digests`, `GET /api/v1/notifications/digests/{id}` | List the estate digests; open one with its per-flow rows and composed bodies |
| `POST /api/v1/notifications/digests` | Generate a digest over `{ "fromUtc", "toUtc" }` |
| `POST /api/v1/notifications/digests/{id}/send` | Queue a digest through one of your subscriptions: `{ "subscriptionId" }` |

A test send exercises the exact production path (composition, destination resolution, credentials, transport), so a green test means real alerts will arrive.

## Operational notes

- **Multi-replica safe**: detection is serialized by an update lock on the single watermark row; the digest window, subscription windows and outbox sends are claimed by compare-and-swap. Running several control-plane replicas neither duplicates nor drops messages.
- **At-least-once**: a crash between composing and recording can repeat a window in rare cases; a message is never silently lost. The delivery history is the audit trail.
- **Retention**: events older than `EventRetentionDays`, sent or failed deliveries older than `DeliveryRetentionDays`, and digests older than `DigestRetentionDays` are pruned during housekeeping; queued and sending deliveries are never pruned.
- **Late-synced history never alerts**: a run artifact synced in days after the fact (CLI runs imported by `db sync`) keeps its original `WrittenUtc` and falls outside the detection window by design; notifications are about what just went wrong.
- **Disabling the service** (`Enabled: false`) also empties what a digest could report, so the on-demand digest endpoint refuses with an explanation rather than answering "no failures".

## See also

- [Deployment](deployment.md)
- [Control plane](../concepts/control-plane.md)
- [Flow and mapping documents](../../delivery/documents.md)
- [Environment variables and secrets](../../environment-variables.md)
