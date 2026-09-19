# Control-plane verbs for OSDU flows

The `sqlflow` binary drives the same `/api/v1` API the GUI uses, so anything verified in the browser can be
verified from a terminal or a script. Signing in, the credential resolution order, the estate verbs (runs,
schedules, repos, pipelines, search, nodes) and the environment helpers are documented in
[../../../../sqlflow/docs/reference/cli/control-plane.md](../../../../sqlflow/docs/reference/cli/control-plane.md).
In an OSDU Delivery deployment the binary is `osdu/hosts/SqlFlow.Delivery.Cli.Host`, which adds the module's own
verbs ([delivery.md](delivery.md)).

This page covers the estate verbs as they are used against OSDU flows.

## Triggering a run

```bash
# the hourly deliver for one log source, now
sqlflow trigger --repo wells --flow wells-welllog --set logSource=STAT_COMP

# plan it instead: render and compare, change nothing, and follow the trace
sqlflow trigger --repo wells --flow wells-welllog --operation plan --set logSource=STAT_COMP --follow

# refresh a partition's OSDU cache
sqlflow trigger --repo wells --flow osdu-cache

# what would be enqueued, without enqueuing it
sqlflow trigger --repo wells --flow wells-welllog --preview
```

`trigger` enqueues one flow. `--repo` (a name or id) and `--flow` are required; `--pool` routes the run to a node
pool, `--commit` pins an exact git object id (otherwise the run is pinned to the repository's last synced commit),
and `--follow` attaches to the live trace and exits by the run's outcome.

The kind arguments are the same three flags a local `sqlflow run` takes, parsed and validated once for both:

| Flag | Meaning |
| --- | --- |
| `--operation <name>` | Which of the kind's operations the run performs. Omitted takes the kind's default. |
| `--set name=value` | A flow parameter value; repeatable. |
| `--payload <json>` or `--payload @<file>` | One JSON object whose shape the flow kind owns. |

What each operation does, and what the payload carries for a delivery run, is in [delivery.md](delivery.md). A
`--scope` other than `flow` is refused: a whole set of flows runs through its schedule, whose membership is what a
fire runs.

## Following what happened

```bash
sqlflow runs list --kind delivery --status failed --repo wells
sqlflow runs show <runId>              # the header: parameters, counts, error
sqlflow runs trace <runId> --follow    # the trace as text, live
sqlflow runs cancel <runId>
```

A delivery run's header carries the submission it produced and its record counts (planned, delivered, held,
failed, unchanged), so `runs show` answers "what did that run actually do to the data" without opening the GUI.
`--kind` filters by flow kind, and the kinds an OSDU Delivery estate has are `delivery`, `retrieval`, `cache`, and
the `pre` and `ing` flows feeding them.

## The estate

```bash
sqlflow pipelines list --repo wells --kind delivery
sqlflow pipelines show <id> --yaml
sqlflow schedules create --repo wells --flow wells-welllog --cron "0 * * * *" --operation deliver
sqlflow schedules create --repo wells --flow wells-welllog --cron "0 3 * * *" --operation verify
sqlflow repos sync recall
sqlflow search <term>
```

Two things are worth knowing here for OSDU flows:

- **A drift pass is a schedule of its own.** `--operation` is carried by every fire of a schedule, so the nightly
  `verify` sits next to the hourly `deliver` as a second schedule over the same flow rather than as a flag
  somebody has to remember.
- **Search answers from the ledger too.** A delivery key lands on each flow's record of that row; an OSDU id, a source key or a label
  prefix lists the records that start with it, across every flow, from indexed columns.

## See also

- [delivery.md](delivery.md): the OSDU verbs and the run options in full.
- [../concepts/control-plane.md](../concepts/control-plane.md): the API these verbs call.
- [../../environment-variables.md](../../environment-variables.md): `SQLFLOW_URL`, `SQLFLOW_TOKEN` and the
  credentials file.
