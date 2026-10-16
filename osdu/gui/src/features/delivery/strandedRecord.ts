import type { DeliveryRecord } from "../../api/delivery";

/**
 * A held or failed record nothing will move: not blocked, not asked to be planned again, and keeping no document to send.
 * What a release answered by a plan that left the record parked used to leave; a release reaches it.
 */
export function stranded(record: DeliveryRecord): boolean {
  return (record.status === "held" || record.status === "failed") && !record.blocked && !record.hasPendingDocument && record.planRequestedUtc === null;
}
