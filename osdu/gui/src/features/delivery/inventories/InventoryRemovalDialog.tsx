import { useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { Loader2, Trash2 } from "lucide-react";
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
import { RadioGroup, RadioGroupItem } from "@/components/ui/radio-group";
import { CorrelationError } from "@/components/CorrelationError";
import { isApiError } from "@/api/client";
import { cn } from "@/lib/utils";
import {
  inventoryApi, type Inventory, type InventoryRemovalAccepted, type InventoryRemovalPolicy, type InventoryRemovalScope,
} from "../../../api/inventories";
import { ChoiceRow, PartHeading, Permanence } from "../RemovalDialog";
import { counted, findingVisual } from "./inventoryFormat";

/** How much of each record a removal takes, as the dialog offers it: what it does, the call it makes, and whether it can be undone. */
const SCOPES: { scope: InventoryRemovalScope; title: string; effect: string; call: string; reversible: boolean }[] = [
  {
    scope: "record",
    title: "Soft delete",
    effect: "Each stops resolving in OSDU, which keeps it and can bring it back. 500 ids go in one request.",
    call: "POST /records/delete",
    reversible: true,
  },
  {
    scope: "everything",
    title: "Purge",
    effect: "Each record and every version of it is destroyed for good, one request an id.",
    call: "DELETE /records/{id}",
    reversible: false,
  },
];

/** What is checked again of each id just before it goes, so a removal never takes what moved since the inventory found it. */
function rechecks(finding: string): string[] {
  const label = findingVisual(finding).label.toLowerCase();
  return [
    `The inventory still finds it ${label}, and the ledgers of the partition give it that finding now.`,
    "OSDU still serves it at the version the inventory listed.",
    ...(finding === "orphan" ? ["An identity this estate writes as still shows as its creator."] : []),
    "An id that fails a check is left in OSDU and kept with why; the rest are removed.",
  ];
}

/**
 * Removing from OSDU the ids of one finding an inventory found (docs/inventory-plan.md, Removing what an inventory found): the
 * ids picked, or every id of the finding, through the flow's own source. It says where they go from (the partition, beside the
 * title, and the platform), how much of each record it takes (a soft delete, or a purge where the flow allows it), and what is
 * checked again of every id before it goes. Nothing is queued until the partition is typed back; the removal is a run of the
 * flow, which holds the inventory to the count shown here.
 */
export function InventoryRemovalDialog({ open, onClose, inventory, finding, ids, count, policy, onQueued }: {
  open: boolean;
  onClose: () => void;
  inventory: Inventory;
  /** The finding whose ids are removed. */
  finding: string;
  /** The ids picked; null removes every id of the finding. */
  ids: readonly string[] | null;
  /** How many ids that is, as the operator was shown. */
  count: number;
  policy: InventoryRemovalPolicy;
  onQueued: (accepted: InventoryRemovalAccepted) => void;
}) {
  const [scope, setScope] = useState<InventoryRemovalScope>("record");
  const [typed, setTyped] = useState("");
  const [wasOpen, setWasOpen] = useState(open);

  // Every opening starts with a soft delete and nothing typed: a removal is never one click away from the last one.
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setScope("record");
      setTyped("");
    }
  }

  const remove = useMutation({
    mutationFn: () => inventoryApi.remove(inventory.partition, inventory.inventoryId, {
      finding, scope, expected: count, confirm: typed.trim(), ...(ids === null ? {} : { ids: [...ids] }),
    }),
    onSuccess: (accepted) => {
      onQueued(accepted);
      onClose();
    },
  });

  const busy = remove.isPending;
  const word = inventory.partition;
  const confirmed = typed.trim().toLowerCase() === word.toLowerCase();
  const label = findingVisual(finding).label.toLowerCase();
  const offered = SCOPES.filter((option) => option.scope === "record" || policy.purge);
  const chosen = SCOPES.find((option) => option.scope === scope) ?? SCOPES[0];

  return (
    <AlertDialog
      open={open}
      onOpenChange={(next) => {
        if (!next && !busy) {
          onClose();
        }
      }}
    >
      <AlertDialogContent data-testid="inventory-removal-dialog" className="max-h-[calc(100dvh-2rem)] max-w-lg gap-4 overflow-y-auto">
        <AlertDialogHeader className="gap-1 text-left">
          <div className="flex items-center gap-2">
            <AlertDialogTitle className="flex min-w-0 flex-1 items-center gap-2">
              <Trash2 className="size-4 shrink-0 text-destructive" />
              {`Remove ${counted(count, `${label} id`)} from OSDU`}
            </AlertDialogTitle>
            <Badge variant="secondary" className="font-mono" data-testid="inventory-removal-partition">{word}</Badge>
          </div>
          <AlertDialogDescription className="break-words" data-testid="inventory-removal-subject">
            {ids === null
              ? `Every ${label} id of ${inventory.name}, through ${inventory.flowName}'s source: `
              : `The ${counted(count, `picked ${label} id`)} of ${inventory.name}, through ${inventory.flowName}'s source: `}
            <span className="break-all font-mono text-[12px] text-foreground">{policy.endpoint}</span>
          </AlertDialogDescription>
        </AlertDialogHeader>

        {remove.isError && (isApiError(remove.error)
          ? <CorrelationError error={remove.error} />
          : <p className="text-[13px] text-destructive" data-testid="inventory-removal-error">{String(remove.error)}</p>)}

        <div className="flex flex-col gap-1.5">
          <PartHeading>In OSDU</PartHeading>
          <RadioGroup
            value={scope}
            onValueChange={(next) => {
              setScope(next as InventoryRemovalScope);
              setTyped("");
            }}
            disabled={busy}
            aria-label="How much of each record goes"
            className="gap-1.5"
          >
            {offered.map((option) => {
              const id = `inventory-removal-scope-${option.scope}`;
              return (
                <ChoiceRow key={option.scope} active={option.scope === scope} permanent={!option.reversible} unavailable={false}>
                  <label htmlFor={id} className="flex min-w-0 flex-1 cursor-pointer items-start gap-2.5 py-2">
                    <RadioGroupItem
                      id={id}
                      value={option.scope}
                      className={cn("mt-0.5", !option.reversible && "text-destructive [&_svg]:fill-destructive")}
                      data-testid={id}
                    />
                    <span className="flex min-w-0 flex-1 flex-col gap-0.5">
                      <span className="text-[13px] font-medium leading-tight">{option.title}</span>
                      <span className="text-[12px] leading-snug text-muted-foreground">{option.effect}</span>
                      <span className="font-mono text-[11px] text-muted-foreground/80">{option.call}</span>
                    </span>
                  </label>
                  <span className="flex items-center pt-2 pr-2">
                    <Permanence reversible={option.reversible} />
                  </span>
                </ChoiceRow>
              );
            })}
          </RadioGroup>
          {!policy.purge && (
            <p className="text-[11.5px] text-muted-foreground" data-testid="inventory-removal-no-purge">
              The flow allows soft deletes only; <span className="font-mono">removal.purge: true</span> in its document lets it purge.
            </p>
          )}
        </div>

        <div className="flex flex-col gap-1.5">
          <PartHeading>Before each id goes</PartHeading>
          <ul className="flex list-disc flex-col gap-0.5 pl-5 text-[12px] leading-snug text-muted-foreground" data-testid="inventory-removal-checks">
            {rechecks(finding).map((line) => <li key={line}>{line}</li>)}
          </ul>
          {ids === null && (
            <p className="text-[12px] leading-snug text-muted-foreground">
              {`The run starts only while the inventory holds exactly ${count.toLocaleString("en-US")} ${label} id${count === 1 ? "" : "s"}, as shown here; every id's outcome is kept with the removal.`}
            </p>
          )}
        </div>

        <div className="flex flex-col gap-1.5">
          <Label htmlFor="inventory-removal-confirm" className="text-[12px] font-normal text-muted-foreground">
            <span>
              {chosen.reversible ? "OSDU can bring a soft-deleted record back. " : "A purged record cannot be brought back. "}
              Type <span className="font-mono font-medium text-foreground">{word}</span> to confirm.
            </span>
          </Label>
          <Input
            id="inventory-removal-confirm"
            value={typed}
            onChange={(event) => setTyped(event.target.value)}
            disabled={busy}
            autoComplete="off"
            spellCheck={false}
            className="h-8 font-mono"
            data-testid="inventory-removal-confirm-input"
          />
        </div>

        <AlertDialogFooter>
          <Button variant="ghost" size="sm" onClick={onClose} disabled={busy} data-testid="inventory-removal-cancel">
            Cancel
          </Button>
          <Button
            variant="destructive"
            size="sm"
            onClick={() => remove.mutate()}
            disabled={busy || !confirmed}
            data-testid="inventory-removal-confirm"
          >
            {busy ? <Loader2 className="animate-spin" /> : <Trash2 />}
            {chosen.reversible ? `Remove ${count.toLocaleString("en-US")}` : `Purge ${count.toLocaleString("en-US")}`}
          </Button>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}
