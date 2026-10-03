import type { FilterOption } from "@/components/FilterCombobox";

/** A ledger identity a page's flow filter offers: the flow and interface that name it, and the partition it is kept under. */
export interface LedgerFlowChoice {
  flowId: string;
  flowName: string;
  interface: string | null;
  partition?: string | null;
  /** A word under its name, when the name alone does not say what it is (a dimension among delivery flows). */
  hint?: string;
}

/** How a choice reads in its filter: see {@link ledgerFlowOptions}. */
export function ledgerFlowLabel(choice: LedgerFlowChoice, active: string | null): string {
  const named = choice.interface ? `${choice.interface} · ${choice.flowName}` : choice.flowName;
  return choice.partition && active === null ? `${named}@${choice.partition}` : named;
}

/**
 * The choices of a flow filter over ledger identities: one per interface of a source and partition of a flow that names
 * its partitions. An interface's choice leads with the interface, since a source's interfaces share the flow's name and the
 * picker is too narrow to show both whole; a partition follows the flow as its ledger is named (flow@partition), when no
 * workbench partition is picked and the choices span them. Two identities named alike (a ledger an interface stopped
 * adopting, say) are told apart by the identity itself.
 *
 * A link can name a flow the list leaves out (`selected` is in none of them once the list has `loaded`); the filter still
 * applies, so the flow stays a choice that says so, with `unlisted` as its hint.
 */
export function ledgerFlowOptions(
  choices: readonly LedgerFlowChoice[] | undefined,
  active: string | null,
  selected: string,
  loaded: boolean,
  unlisted: string,
): FilterOption[] {
  const named = (choices ?? []).map((choice) => ({ value: choice.flowId, label: ledgerFlowLabel(choice, active), hint: choice.hint }));
  const shared = new Set(named.map((o) => o.label).filter((label, i, all) => all.indexOf(label) !== i));
  const options: FilterOption[] = named.map((o) => (shared.has(o.label) ? { ...o, hint: `ledger ${o.value}` } : o));
  return selected !== "" && loaded && !options.some((o) => o.value === selected)
    ? [{ value: selected, label: "Flow not listed", hint: unlisted }, ...options]
    : options;
}
