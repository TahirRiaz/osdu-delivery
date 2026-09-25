# Failure notifications

The control plane watches the run history and tells the users who asked when something goes wrong: a run failed,
was cancelled, or was skipped because an upstream member of its run group did not succeed. Subscriptions are
strictly opt-in and per user, they coalesce into one message per window rather than one per event, and they are
delivered by email or Slack.

All of that is SQLFlow's, unchanged, and documented in
[../../../../sqlflow/docs/reference/guides/notifications.md](../../../../sqlflow/docs/reference/guides/notifications.md):
the pipeline and its phases, immediate and digest pacing, what a subscription selects, the estate digest, the
channels and their configuration, and the API. This page covers only what it means for OSDU Delivery.

## What gets reported

A notification is about a **run**, not about a record. Every OSDU Delivery flow kind is an ordinary platform flow,
so a failed run of any of them alerts: a `delivery` flow, a `retrieval` flow, and a `cache` flow whose refresh
could not capture what it declares.

A run that succeeded reports nothing, even when it **held** records. Held records are a normal outcome of a
delivery run (a value the mapping could not resolve, a payload that is not where the record says it is), they are
recorded in the ledger with their reason, and the flow's Delivery tab and the record search are where they are
seen and acted on. Watching them through failure notifications would either page on healthy runs or say nothing
useful, so the ledger is the surface for them and the run counts on the run page are the summary.

The flow-name filter on a subscription is the useful narrowing here: an estate with one delivery chain per data
set gives each chain its own flow names, so a subscription can follow just the chain someone owns, pre and
ingestion flows included.

## Development flows never alert

Every flow document can declare a top-level `lifecycle:` in its envelope, next to `flowType` and `name`. The
accepted values are `production` (the default, which alerts subscribers on failure) and `development`, which runs,
schedules and records history identically but generates no notification events at all:

```yaml
flowType: delivery
name: recall-welllog-03-header-delivery
lifecycle: development   # remove (or set to production) when the flow goes live
```

That matters more here than in a plain ETL estate, because bringing up a delivery chain means iterating on a
mapping against a real OSDU platform, and a half-built mapping fails often and loudly. The gate is applied at
detection time against the synced catalog pipeline, so it takes effect on the next repository sync after the YAML
changes.

## Links in a message

Set `ControlPlane:Notifications:GuiBaseUrl` to the GUI origin and every message carries "Open run" and "Open flow"
links. For a delivery failure that is the fastest path to the run trace and, from it, to the records the run
touched.

## See also

- [Deployment](deployment.md)
- [The control plane](../concepts/control-plane.md)
- [Environment variables and secrets](../../environment-variables.md)
