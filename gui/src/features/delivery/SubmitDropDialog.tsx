import { useEffect, useId, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useMutation } from "@tanstack/react-query";
import { Loader2 } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  Sheet, SheetContent, SheetDescription, SheetFooter, SheetHeader, SheetTitle,
} from "@/components/ui/sheet";
import { Switch } from "@/components/ui/switch";
import { Textarea } from "@/components/ui/textarea";
import { isApiError } from "../../api/client";
import { deliveryApi } from "../../api/delivery";
import { CorrelationError } from "../../components/CorrelationError";

const IDENTIFIER = /^[A-Za-z_][A-Za-z0-9_]*$/;

/** Parses "name=value" lines into the flow's parameter values; the first malformed line is the error. */
function parseValues(text: string): { values: Record<string, string>; error: string | null } {
  const values: Record<string, string> = {};
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim();
    if (line === "") {
      continue;
    }

    const at = line.indexOf("=");
    const name = at > 0 ? line.slice(0, at).trim() : "";
    if (at <= 0 || !IDENTIFIER.test(name)) {
      return { values, error: `Parameter '${line}' must be written as name=value (the name an identifier).` };
    }

    values[name] = line.slice(at + 1);
  }

  return { values, error: null };
}

export interface SubmitDropDialogProps {
  open: boolean;
  onClose: () => void;
  pipelineId: string;
  flowName: string;
}

/**
 * The manifest notification by hand: name the drop a preparing job has finished and the flow parameters it was
 * prepared with, and the control plane queues the deliver run. The same call an automated pipeline makes on its
 * own after writing a drop.
 */
export function SubmitDropDialog({ open, onClose, pipelineId, flowName }: SubmitDropDialogProps) {
  const navigate = useNavigate();
  const idPrefix = useId();
  const [drop, setDrop] = useState("");
  const [valuesText, setValuesText] = useState("");
  const [force, setForce] = useState(false);

  useEffect(() => {
    if (open) {
      setDrop("");
      setValuesText("");
      setForce(false);
    }
  }, [open]);

  const submit = useMutation({
    mutationFn: deliveryApi.submit,
    onSuccess: (accepted) => {
      onClose();
      toast.success(`Submission queued for ${accepted.flowName}.`);
      navigate(`/runs/${accepted.runId}`);
    },
    onError: (error) => {
      toast.error(isApiError(error) ? error.detail ?? error.title : String(error));
    },
  });

  const parsed = parseValues(valuesText);
  const error = drop.trim() === "" ? null : parsed.error;
  const canSubmit = drop.trim() !== "" && parsed.error === null && !submit.isPending;

  return (
    <Sheet open={open} onOpenChange={(next) => { if (!next && !submit.isPending) { onClose(); } }}>
      <SheetContent className="w-full gap-0 sm:max-w-lg" data-testid="submit-drop-dialog">
        <SheetHeader>
          <SheetTitle>Submit a drop</SheetTitle>
          <SheetDescription>
            Tell {flowName} that a drop is ready. The control plane queues the deliver run; the ledger registers the
            submission under the manifest&apos;s id.
          </SheetDescription>
        </SheetHeader>
        <div className="flex flex-1 flex-col gap-4 overflow-y-auto px-4 pb-4">
          {submit.isError && isApiError(submit.error) && <CorrelationError error={submit.error} />}
          <div className="flex flex-col gap-1.5">
            <Label htmlFor={`${idPrefix}-drop`}>Drop location</Label>
            <Input
              id={`${idPrefix}-drop`}
              className="h-8 font-mono"
              placeholder="abfss://drops@lake.dfs.core.windows.net/recall/STAT_COMP"
              value={drop}
              onChange={(event) => setDrop(event.target.value)}
              data-testid="submit-drop-location"
            />
            <p className="text-xs text-muted-foreground">The drop root holding the manifest and the record files.</p>
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor={`${idPrefix}-values`}>Flow parameters</Label>
            <Textarea
              id={`${idPrefix}-values`}
              className="min-h-16 font-mono text-[12px]"
              placeholder={"logSource=STAT_COMP"}
              value={valuesText}
              onChange={(event) => setValuesText(event.target.value)}
              data-testid="submit-drop-values"
            />
            <p className="text-xs text-muted-foreground">
              Values for the parameters the flow declares, one name=value per line; they are recorded on the submission.
            </p>
          </div>
          <Label className="flex items-center gap-2 text-[13px] font-normal">
            <Switch checked={force} onCheckedChange={setForce} data-testid="submit-drop-force" />
            Force: plan every record even when no source table advanced
          </Label>
          {error !== null && <p className="text-xs font-medium text-destructive" data-testid="submit-drop-error">{error}</p>}
        </div>
        <SheetFooter className="flex-row justify-end gap-2 border-t border-border">
          <Button variant="ghost" size="sm" onClick={onClose} disabled={submit.isPending}>Cancel</Button>
          <Button
            size="sm"
            disabled={!canSubmit}
            onClick={() => submit.mutate({
              pipelineId,
              drop: drop.trim(),
              parameters: Object.keys(parsed.values).length > 0 ? parsed.values : null,
              force,
            })}
            data-testid="submit-drop-submit"
          >
            {submit.isPending && <Loader2 className="animate-spin" />}
            Submit
          </Button>
        </SheetFooter>
      </SheetContent>
    </Sheet>
  );
}
