import type { DeliveryAttempt } from "../../api/delivery";
import { TruncatedText } from "../../components/TruncatedText";

/**
 * An attempt's error, or the note an attempt that did not fail carries, with the correlation id its OSDU requests
 * carried underneath: the id to hand to whoever reads the services' logs.
 */
export function AttemptDetail({ attempt }: { attempt: DeliveryAttempt }) {
  const text = attempt.error ?? attempt.result?.detail ?? null;
  const correlationId = attempt.result?.correlationId;
  return (
    <div className="flex flex-col gap-0.5">
      <TruncatedText text={text} maxWidth={360} className={attempt.error !== null ? "text-destructive" : undefined} />
      {correlationId !== undefined && (
        <TruncatedText text={correlationId} mono copy maxWidth={360} title="Correlation id" className="text-[11px] text-muted-foreground" />
      )}
    </div>
  );
}
