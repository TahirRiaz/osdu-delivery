import { useEffect, useRef } from "react";
import { useMutation } from "@tanstack/react-query";
import { toast } from "sonner";
import { deliveryApi, type DeliveryRecordRef } from "../../api/delivery";
import { failureText } from "./answers";

/**
 * A read of one record from OSDU through its flow's route and credentials, answered by the control plane at once: what it
 * answered, and a way to read again. Asked to read at once, it reads a single time, as soon as the record is known to have
 * an id OSDU may hold (`readable`). The record page's OSDU tab and the record's OSDU window both read through it, so they
 * read alike.
 */
export function useRecordOsduRead(ref: DeliveryRecordRef, { readable, readAtOnce }: { readable: boolean; readAtOnce: boolean }) {
  const { mutate, data, error, isPending, isIdle, submittedAt } = useMutation({
    mutationFn: () => deliveryApi.read(ref),
    onError: (failed) => toast.error(failureText(failed)),
  });
  const readPending = useRef(readAtOnce);
  useEffect(() => {
    if (readPending.current && readable) {
      readPending.current = false;
      mutate();
    }
  }, [readable, mutate]);

  return {
    /** Whether a read was asked for at all. */
    asked: !isIdle,
    /** When the read in view was asked for: a new read is a new view of the record. */
    askedAt: submittedAt,
    read: data,
    error: error ?? undefined,
    pending: isPending,
    readAgain: () => mutate(),
  };
}

/** A record's read from OSDU as `useRecordOsduRead` keeps it. */
export type RecordOsduRead = ReturnType<typeof useRecordOsduRead>;
