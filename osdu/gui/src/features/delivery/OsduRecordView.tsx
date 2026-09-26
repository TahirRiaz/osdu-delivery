import { useState } from "react";
import { useMutation, useQueries } from "@tanstack/react-query";
import { toast } from "sonner";
import { isApiError } from "@/api/client";
import type { ComputeTask } from "@/api/types";
import { deliveryApi } from "../../api/delivery";
import { OsduRecordInspector, type InspectorEntry } from "./OsduRecordInspector";
import { computeTaskQuery } from "./useComputeTask";

/** How many records can be opened one from the other before the trail refuses to grow. */
const MAX_TRAIL = 8;

/**
 * A record read from OSDU, and the records opened from links in it, read in turn through the same flow's route and
 * credentials on a node. They form a trail (the page's record, then each record opened from the one before) shown in
 * one inspector, the last one in view; stepping back along the trail closes what was opened after that point, and a
 * version read of any record in the trail takes that record's place.
 */
export function OsduRecordPanel({ pipelineId, interfaceName, task, targetId, onReadVersion, ledgerVersion }: {
  pipelineId: string | null;
  interfaceName: string | null;
  /** The page's own read, as it stands. */
  task: ComputeTask | undefined;
  /** The id the page's read is for, so the trail names it before the read has answered. */
  targetId: string;
  /** Reads the page's own record again at one of its versions; absent where it cannot be asked for. */
  onReadVersion?: (version: number) => void;
  /** The version the ledger holds as delivered by this flow, marked in the page record's version list. */
  ledgerVersion?: number | null;
}) {
  const [trail, setTrail] = useState<{ id: string; taskId: string }[]>([]);
  const [opening, setOpening] = useState<string | null>(null);
  const reads = useQueries({ queries: trail.map((link) => computeTaskQuery(link.taskId)) });
  const open = useMutation({
    mutationFn: ({ id, version }: { id: string; level: number; version?: number }) => deliveryApi.readOsdu(pipelineId!, id, interfaceName, version),
    onMutate: (asked) => setOpening(asked.id),
    // A record opened from level N takes place N+1 and closes everything after it; a version read at level N takes place N.
    onSuccess: (accepted, asked) => setTrail((was) => [...was.slice(0, asked.level), { id: asked.id, taskId: accepted.taskId }].slice(-MAX_TRAIL)),
    onError: (error) => toast.error(isApiError(error) ? error.detail ?? error.title : String(error)),
    onSettled: () => setOpening(null),
  });

  const entries: InspectorEntry[] = [
    { id: targetId, task },
    ...trail.map((link, index) => ({ id: link.id, task: reads[index]?.data, error: reads[index]?.error ?? undefined })),
  ];
  const level = entries.length - 1;
  const canOpen = pipelineId !== null;

  return (
    <OsduRecordInspector
      entries={entries}
      ledgerVersion={ledgerVersion}
      opening={opening}
      onOpenLink={canOpen ? (id) => open.mutate({ id, level }) : undefined}
      onReadVersion={level === 0
        ? onReadVersion
        : canOpen ? (version) => open.mutate({ id: entries[level].id, level: level - 1, version }) : undefined}
      onBack={(to) => setTrail((was) => was.slice(0, to))}
    />
  );
}
