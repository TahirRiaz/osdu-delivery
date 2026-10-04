import { useState, type ReactNode } from "react";
import { Loader2 } from "lucide-react";
import {
  AlertDialog, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Label } from "@/components/ui/label";

/** How many records a deliver run plans again in one pass by key; it makes as many passes as there are (DeliveryExecutor.RequestedPerPass). */
export const PLANNED_PER_PASS = 5000;

/**
 * The confirmation of a release of many records, with the choice of a deliver run straight after: the one release dialog,
 * for a flow's blocked records and for a problem's. While the release runs it cannot be dismissed.
 */
export function ReleaseDialog({ open, title, message, confirmLabel, busy, children, testId, onConfirm, onClose }: {
  open: boolean;
  title: string;
  message: string;
  confirmLabel: string;
  busy: boolean;
  /** What else the dialog shows above the run choice (the problem's error, a warning). */
  children?: ReactNode;
  testId: string;
  onConfirm: (run: boolean) => void;
  onClose: () => void;
}) {
  const [run, setRun] = useState(true);
  return (
    <AlertDialog
      open={open}
      onOpenChange={(next) => {
        if (!next && !busy) {
          onClose();
        }
      }}
    >
      <AlertDialogContent data-testid={testId} className="max-w-lg">
        <AlertDialogHeader>
          <AlertDialogTitle>{title}</AlertDialogTitle>
          <AlertDialogDescription>{message}</AlertDialogDescription>
        </AlertDialogHeader>
        {children}
        <Label className="flex items-center gap-2 text-[13px] font-normal" title="The run plans every record released, a pass of 5,000 at a time, and sends what each release queued.">
          <Checkbox checked={run} onCheckedChange={(next) => setRun(next === true)} disabled={busy} data-testid={`${testId}-run`} />
          Queue a deliver run now
        </Label>
        <AlertDialogFooter>
          <Button variant="ghost" size="sm" onClick={onClose} disabled={busy} data-testid={`${testId}-cancel`}>
            Cancel
          </Button>
          <Button size="sm" onClick={() => onConfirm(run)} disabled={busy} data-testid={`${testId}-confirm`}>
            {busy && <Loader2 className="animate-spin" />}
            {confirmLabel}
          </Button>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}
