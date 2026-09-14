import { Badge } from "@/components/ui/badge";
import type { Schedule } from "../../api/types";
import { Mono } from "../../components/Mono";
import type { Column } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";
import { ScheduleStateBadge } from "../../components/StatusBadge";
import { TriggerCell } from "./TriggerCell";

/**
 * The columns of a schedule list that sits beside something else: a pipeline's schedules tab, or the preview of the
 * YAML file that declares them. Name, when it fires (in words, the expression on hover), its state, where it came
 * from, and its next and last fire. The schedules page keeps its own set, which carries the operator actions.
 */
export const scheduleColumns: Column<Schedule>[] = [
  { id: "name", header: "Name", render: (row) => <Mono>{row.name}</Mono> },
  { id: "trigger", header: "Trigger", render: (row) => <TriggerCell schedule={row} /> },
  {
    id: "state",
    header: "State",
    render: (row) => (
      <span className="inline-flex items-center gap-1">
        <ScheduleStateBadge enabled={row.enabled} paused={row.paused} />
        {row.catchup && <Badge variant="outline">catchup</Badge>}
      </span>
    ),
  },
  { id: "operation", header: "Operation", render: (row) => row.operation },
  { id: "source", header: "Source", render: (row) => <Badge variant="outline">{row.source}</Badge> },
  { id: "nextFire", header: "Next fire", render: (row) => <RelativeTime value={row.nextFireUtc} /> },
  { id: "lastFire", header: "Last fire", render: (row) => <RelativeTime value={row.lastFireUtc} /> },
];
