import { useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { Loader2, Trash2 } from "lucide-react";
import { toast } from "sonner";
import {
  AlertDialog,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { isApiError } from "@/api/client";
import { deliveryApi, type DeliveryFlowStats, type DeliveryInterface, type DeliveryLedgerDeleteAccepted } from "../../api/delivery";
import { ChoiceRow, PartHeading, Permanence } from "./RemovalDialog";

/** The interfaces in the order deleting the ledger takes them: the waves backwards, so what refers to a record goes first. */
function deletionOrder(rows: DeliveryInterface[]): string[] {
  return [...rows]
    .map((row, index) => ({ row, index }))
    .sort((a, b) => b.row.wave - a.row.wave || a.index - b.index)
    .map(({ row }) => row.interface)
    .filter((name): name is string => name !== null);
}

/** One step of what deleting the ledger does, in the frame the removal dialog gives each of its choices. */
function Step({ title, effect, reversible, testId }: { title: string; effect: string; reversible: boolean; testId: string }) {
  return (
    <ChoiceRow active permanent={!reversible} unavailable={false}>
      <span className="flex min-w-0 flex-1 flex-col gap-0.5 py-2" data-testid={testId}>
        <span className="text-[13px] font-medium leading-tight">{title}</span>
        <span className="text-[12px] leading-snug text-muted-foreground">{effect}</span>
      </span>
      <span className="flex items-center pt-2 pr-2">
        <Permanence reversible={reversible} />
      </span>
    </ChoiceRow>
  );
}

/**
 * Deleting a flow's whole ledger, every interface's, in the partition in view (docs/ledger.md, Deleting the ledger). It
 * says what the run does, in the two places it acts: every record OSDU holds is removed from it first, reversibly, and then
 * everything the ledger keeps goes for good, so the next run reads every row and delivers each as a new record. A source's
 * interfaces are listed in the order the run takes them. Nothing is queued until the partition is typed back, and the run
 * checks it again before it removes or deletes anything.
 */
export function DeleteLedgerDialog({
  open, onClose, pipelineId, flowName, partition, word, stats, interfaces, onQueued,
}: {
  open: boolean;
  onClose: () => void;
  pipelineId: string;
  flowName: string;
  /** The partition the request names: the one in view, or null for a flow whose partition is its header. */
  partition: string | null;
  /** The partition the ledger is kept in, which has to be typed back. */
  word: string;
  stats: DeliveryFlowStats;
  /** A source's interfaces with their counts; null for a flow in the single form. */
  interfaces: DeliveryInterface[] | null;
  onQueued: (accepted: DeliveryLedgerDeleteAccepted) => void;
}) {
  const [typed, setTyped] = useState("");
  const [wasOpen, setWasOpen] = useState(open);

  // Every opening starts with nothing typed: a deletion is never one click away from the last one.
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setTyped("");
    }
  }

  const remove = useMutation({
    mutationFn: () => deliveryApi.deleteLedger(pipelineId, { confirm: typed.trim() }, partition),
    onSuccess: (accepted) => {
      onQueued(accepted);
      onClose();
    },
    onError: (error) => toast.error(isApiError(error) ? error.detail ?? error.title : String(error)),
  });

  const busy = remove.isPending;
  const records = stats.total;
  const order = interfaces === null ? [] : deletionOrder(interfaces);
  const confirmed = typed.trim() === word;

  return (
    <AlertDialog
      open={open}
      onOpenChange={(next) => {
        if (!next && !busy) {
          onClose();
        }
      }}
    >
      <AlertDialogContent data-testid="delete-ledger-dialog" className="max-h-[calc(100dvh-2rem)] max-w-lg gap-4 overflow-y-auto">
        <AlertDialogHeader className="gap-1 text-left">
          <div className="flex items-center gap-2">
            <AlertDialogTitle className="flex min-w-0 flex-1 items-center gap-2">
              <Trash2 className="size-4 shrink-0 text-destructive" />
              Delete ledger
            </AlertDialogTitle>
            <Badge variant="secondary" className="font-mono" data-testid="delete-ledger-partition">{word}</Badge>
          </div>
          <AlertDialogDescription className="break-words" data-testid="delete-ledger-subject">
            {`${flowName}: ${records.toLocaleString()} record${records === 1 ? "" : "s"}`}
            {order.length > 1 && <span className="text-muted-foreground/80">{` in ${order.length} interfaces`}</span>}
          </AlertDialogDescription>
        </AlertDialogHeader>

        <div className="flex flex-col gap-1.5">
          <PartHeading>In OSDU</PartHeading>
          <Step
            title="Remove every record OSDU holds"
            effect="Each stops resolving there, the soft delete OSDU can undo. If OSDU refuses any of them, the ledger is kept as it was."
            reversible
            testId="delete-ledger-osdu"
          />
        </div>

        <div className="flex flex-col gap-1.5">
          <PartHeading>In the ledger</PartHeading>
          <Step
            title="Delete everything"
            effect="Records, attempts, submissions, watermarks and reversals go. The audit trail and one line of each record stay. The next run reads every row and delivers each as a new record, under the same OSDU ids."
            reversible={false}
            testId="delete-ledger-ledger"
          />
        </div>

        {order.length > 1 && (
          <p className="text-[12px] text-muted-foreground" data-testid="delete-ledger-order">
            {"Interfaces go in reverse delivery order, so nothing that refers to a record outlives it: "}
            <span className="font-mono text-foreground">{order.join(", then ")}</span>.
          </p>
        )}

        <div className="flex flex-col gap-1.5">
          <Label htmlFor="delete-ledger-confirm" className="text-[12px] font-normal text-muted-foreground">
            <span>
              The ledger cannot be brought back. Type <span className="font-mono font-medium text-foreground">{word}</span> to confirm.
            </span>
          </Label>
          <Input
            id="delete-ledger-confirm"
            value={typed}
            onChange={(event) => setTyped(event.target.value)}
            disabled={busy}
            autoComplete="off"
            spellCheck={false}
            className="h-8 font-mono"
            data-testid="delete-ledger-confirm-input"
          />
        </div>

        <AlertDialogFooter>
          <Button variant="ghost" size="sm" onClick={onClose} disabled={busy} data-testid="delete-ledger-cancel">
            Cancel
          </Button>
          <Button
            variant="destructive"
            size="sm"
            onClick={() => remove.mutate()}
            disabled={busy || !confirmed}
            data-testid="delete-ledger-confirm"
          >
            {busy ? <Loader2 className="animate-spin" /> : <Trash2 />}
            Delete ledger
          </Button>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}
