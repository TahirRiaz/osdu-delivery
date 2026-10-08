import { useNavigate } from "react-router-dom";
import { ClipboardList } from "lucide-react";
import { Button } from "@/components/ui/button";
import type { RunDetail } from "@/api/types";
import { DetailPair } from "@/components/DetailPair";
import { RAISED_FINDINGS } from "../../../api/inventories";
import { runResult } from "../runOutcome";

/** A count an inventory run reported, or a muted dash when it reported none (a run that stopped before its inventories). */
function Count({ value, testId }: { value: number | null; testId: string }) {
  return value !== null
    ? <span className="font-mono tabular-nums" data-testid={testId}>{value.toLocaleString("en-US")}</span>
    : <span className="text-muted-foreground" data-testid={testId}>-</span>;
}

function numberOf(result: Record<string, unknown> | null, field: string): number | null {
  const value = result?.[field];
  return typeof value === "number" && Number.isFinite(value) ? value : null;
}

/** The inventories a run's result names, each as the object it reported; none for a result that names none. */
function inventoriesOf(result: Record<string, unknown> | null): Record<string, unknown>[] {
  const inventories = result?.inventories;
  return Array.isArray(inventories)
    ? inventories.filter((entry): entry is Record<string, unknown> => entry !== null && typeof entry === "object" && !Array.isArray(entry))
    : [];
}

/** A field summed over the run's inventories; null for a run that reported no inventory, or an operation that counts no such thing. */
function sumOf(result: Record<string, unknown> | null, field: string, operations: readonly string[]): number | null {
  const operation = result?.operation;
  const inventories = inventoriesOf(result);
  if (typeof operation !== "string" || !operations.includes(operation) || inventories.length === 0) {
    return null;
  }

  return inventories.reduce<number>((sum, entry) => {
    const value = entry[field];
    return sum + (typeof value === "number" && Number.isFinite(value) ? value : 0);
  }, 0);
}

/** The ids the run's reconciles raised, summed over its inventories; null for a run that reconciled none. */
function raisedOf(result: Record<string, unknown> | null): number | null {
  const counted = inventoriesOf(result)
    .map((entry) => entry.findings)
    .filter((findings): findings is Record<string, unknown> => findings !== null && typeof findings === "object" && !Array.isArray(findings));
  if (counted.length === 0) {
    return null;
  }

  return counted.reduce<number>((sum, findings) => sum + RAISED_FINDINGS.reduce<number>((own, finding) => {
    const value = findings[finding];
    return own + (typeof value === "number" && Number.isFinite(value) ? value : 0);
  }, 0), 0);
}

/** An inventory run's way to the inventories it built, on the page every partition's inventories are read on. */
export function InventoryRunActions() {
  const navigate = useNavigate();
  return (
    <Button variant="outline" size="sm" onClick={() => navigate("/delivery/inventories")} data-testid="run-open-inventories">
      <ClipboardList />
      Open inventories
    </Button>
  );
}

/** How the run's inventories came out, in place of the row counts SQLFlow's own runs carry. */
export function InventoryRunCounts({ run }: { run: RunDetail }) {
  const result = runResult(run);
  const plan = result?.operation === "plan";
  return plan
    ? (
      <>
        <DetailPair label="Inventories"><Count value={inventoriesOf(result).length} testId="run-inventories-planned" /></DetailPair>
        <DetailPair label="Would read"><Count value={sumOf(result, "records", ["plan"])} testId="run-inventories-records" /></DetailPair>
      </>
    )
    : (
      <>
        <DetailPair label="Completed"><Count value={numberOf(result, "completed")} testId="run-inventories-completed" /></DetailPair>
        <DetailPair label="Failed"><Count value={numberOf(result, "failed")} testId="run-inventories-failed" /></DetailPair>
        <DetailPair label="Listed"><Count value={sumOf(result, "listed", ["build"])} testId="run-inventories-listed" /></DetailPair>
        <DetailPair label="Raised"><Count value={raisedOf(result)} testId="run-inventories-raised" /></DetailPair>
      </>
    );
}
