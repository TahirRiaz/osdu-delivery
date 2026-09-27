import { useEffect, useRef, useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { toast } from "sonner";
import { isApiError } from "@/api/client";
import { deliveryApi, type DeliveryRecordRef } from "../../api/delivery";
import { useComputeTask } from "./useComputeTask";

/**
 * A read of one record from OSDU through its flow's route and credentials, on a node: the task it queued, and a way to
 * queue another. Asked to read at once, it reads a single time, as soon as the record is known to have an id OSDU may
 * hold (`readable`). The record page's OSDU tab and the record's OSDU window both read through it, so they read alike.
 */
export function useRecordOsduRead(ref: DeliveryRecordRef, { readable, readAtOnce }: { readable: boolean; readAtOnce: boolean }) {
  const [taskId, setTaskId] = useState<string | null>(null);
  const task = useComputeTask(taskId);
  const { mutate, isPending } = useMutation({
    mutationFn: () => deliveryApi.read(ref),
    onSuccess: (accepted) => setTaskId(accepted.taskId),
    onError: (error) => toast.error(isApiError(error) ? error.detail ?? error.title : String(error)),
  });
  const readPending = useRef(readAtOnce);
  useEffect(() => {
    if (readPending.current && readable) {
      readPending.current = false;
      mutate();
    }
  }, [readable, mutate]);

  return { taskId, task, read: () => mutate(), queueing: isPending };
}

/** A record's read from OSDU as `useRecordOsduRead` keeps it. */
export type RecordOsduRead = ReturnType<typeof useRecordOsduRead>;
