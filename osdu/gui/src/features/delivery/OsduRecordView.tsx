import { useState, type ReactNode } from "react";
import { useMutation, useQueries } from "@tanstack/react-query";
import { toast } from "sonner";
import { isApiError } from "@/api/client";
import type { ComputeTask, ComputeTaskAccepted } from "@/api/types";
import { OsduRecordInspector, type InspectorEntry, type InspectorExtras } from "./OsduRecordInspector";
import { computeTaskQuery } from "./useComputeTask";

/** How many records can be opened one from the other before the trail refuses to grow. */
const MAX_TRAIL = 8;

/** How a record other than the page's own is read: by its id, at its latest or at one version, as a node task. */
export type ReadRecord = (id: string, version?: number) => Promise<ComputeTaskAccepted>;

/**
 * A record read from OSDU, and the records opened from links in it, read in turn the way the page reads records (a record
 * page through its flow's route and credentials, the explorer through its partition's connection), each on a node. They
 * form a trail (the page's record, then each record opened from the one before) shown in one inspector, the last one in
 * view; stepping back along the trail closes what was opened after that point. A version of any record on the trail is
 * read into the inspector in place, without disturbing the trail.
 */
export function OsduRecordPanel({ readLinked, task, targetId, readRootVersion, ledgerVersion, actions, extras, fill = false }: {
  /** Reads a record opened from the page's, and a version of one; null where nothing more can be read. */
  readLinked: ReadRecord | null;
  /** The page's own read, as it stands. */
  task: ComputeTask | undefined;
  /** The id the page's read is for, so the trail names it before the read has answered. */
  targetId: string;
  /** Queues a read of the page's own record at one of its versions; absent where it cannot be asked for. */
  readRootVersion?: (version: number) => Promise<ComputeTaskAccepted>;
  /** The version the ledger holds as delivered by this flow, marked in the page record's version list. */
  ledgerVersion?: number | null;
  /** The page's own controls over the read, shown on the inspector's header row. */
  actions?: ReactNode;
  /** What the page adds to the inspector: the place in front of the record, its mentions, what to offer for an id not found. */
  extras?: InspectorExtras;
  /** Whether the inspector fills its flex column parent (a window of its own) rather than a share of the viewport. */
  fill?: boolean;
}) {
  const [trail, setTrail] = useState<{ id: string; taskId: string; from: string | null }[]>([]);
  const [opening, setOpening] = useState<string | null>(null);
  const reads = useQueries({ queries: trail.map((link) => computeTaskQuery(link.taskId)) });
  const open = useMutation({
    mutationFn: ({ id }: { id: string; level: number; from: string | null }) => readLinked!(id),
    onMutate: (asked) => setOpening(asked.id),
    // A record opened from the one at level N takes place N+1 and closes everything that was after it. The trail is
    // bounded: past its length the request is refused rather than the first records quietly dropped.
    onSuccess: (accepted, asked) => setTrail((was) => [...was.slice(0, asked.level), { id: asked.id, taskId: accepted.taskId, from: asked.from }]),
    onError: (error) => toast.error(isApiError(error) ? error.detail ?? error.title : String(error)),
    onSettled: () => setOpening(null),
  });

  const entries: InspectorEntry[] = [
    { id: targetId, task },
    ...trail.map((link, index) => ({ id: link.id, task: reads[index]?.data, error: reads[index]?.error ?? undefined, from: link.from })),
  ];
  const canOpen = readLinked !== null;

  return (
    <OsduRecordInspector
      entries={entries}
      ledgerVersion={ledgerVersion}
      opening={opening}
      onOpenLink={canOpen
        ? (level, id, from) => {
          if (level + 1 >= MAX_TRAIL) {
            toast.error(`Up to ${MAX_TRAIL} records open one from the other; step back along the trail to follow this one.`);
            return;
          }

          open.mutate({ id, level, from });
        }
        : undefined}
      readVersionAt={(level) => (level === 0
        ? readRootVersion
        : canOpen ? (version) => readLinked(entries[level].id, version) : undefined)}
      onBack={(to) => setTrail((was) => was.slice(0, to))}
      actions={actions}
      extras={extras}
      fill={fill}
    />
  );
}
