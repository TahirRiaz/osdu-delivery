import type { Schedule } from "../../api/types";

/** The schedules a chained link waits for, as one label, or null for a clock-driven schedule. */
export function chainedAfter(schedule: Schedule): string | null {
  return schedule.afterSchedules && schedule.afterSchedules.length > 0 ? schedule.afterSchedules.join(", ") : null;
}
