// The inventories of inventory flows (osdu/docs/reference/flow/inventory.md): every id an OSDU kind holds in a partition,
// set against every ledger of the partition, each id with its finding. Same conventions as delivery.ts: one function per
// endpoint, pages compose them with TanStack Query. An answer leaves out what holds no value, so every field that may hold
// none is optional here, and a page tests it with `=== undefined` or `??`, never `=== null`.

import { get, getText, post } from "@/api/client";
import type { ExplorerFilter } from "./explorer";

/**
 * What an inventory finds of an id: what OSDU serves of it set against what the ledgers hold of it. The first eight are the
 * findings a report raises; the rest say the two agree, or why an id needs no look.
 */
export type InventoryFinding =
  | "orphan" | "missing" | "undoing" | "forgotten" | "stale" | "unconfirmed" | "drifted" | "unlisted"
  | "foreign" | "superseded" | "tracked" | "gone" | "unreconciled";

/** Every finding, in the order a report lists them. */
export const INVENTORY_FINDINGS: readonly InventoryFinding[] = [
  "orphan", "missing", "undoing", "forgotten", "stale", "unconfirmed", "drifted", "unlisted", "foreign", "superseded", "tracked", "gone", "unreconciled",
];

/** The findings a report raises: where the ledgers and OSDU disagree. */
export const RAISED_FINDINGS: readonly InventoryFinding[] = ["orphan", "missing", "undoing", "forgotten", "stale", "unconfirmed", "drifted", "unlisted"];

/** The findings whose ids an inventory flow may remove from OSDU, when its document allows it. */
export const REMOVABLE_FINDINGS: readonly InventoryFinding[] = ["orphan", "stale", "forgotten"];

/**
 * The finding whose ids are removed from the inventory alone, whatever the flow declares: OSDU no longer serves them, so
 * nothing is asked of it.
 */
export const GONE_FINDING: InventoryFinding = "gone";

/**
 * How much of each record a removal takes: a soft delete (`record`, reversible) or a purge (`everything`) of an id OSDU
 * serves; the inventory's own row (`inventory`) of a gone id, and only of one.
 */
export type InventoryRemovalScope = "record" | "everything" | "inventory";

/** The finding whose ids a repair delivers again: a record a ledger delivered that OSDU no longer holds. */
export const MISSING_FINDING: InventoryFinding = "missing";

/**
 * A repair of an inventory's missing ids (osdu/docs/reference/flow/inventory.md, Repairing missing ids): the count the
 * operator was shown, the ids picked, and whether the records are delivered under new OSDU ids (which takes the partition
 * typed back).
 */
export interface InventoryRepairRequest {
  expected: number;
  ids?: string[];
  newIds?: boolean;
  confirm?: string;
}

/** What a repair did in one delivery ledger: the flow, how many records it asked for and marked, and the run it queued. */
export interface InventoryRepairFlow {
  ledgerId: string;
  flow: string;
  pipelineId: string;
  asked: number;
  marked: number;
  runId?: string;
}

/** Missing ids a repair left as they were, for one reason, with the first of them it can name. */
export interface InventoryRepairSkip {
  reason: string;
  count: number;
  ids: string[];
}

/** What a repair did: how many ids it was asked for, how many records it marked and how, per flow, and what it left. */
export interface InventoryRepairResult {
  partition: string;
  inventory: string;
  asked: number;
  marked: number;
  newIds: boolean;
  flows: InventoryRepairFlow[];
  skipped: InventoryRepairSkip[];
  activityId: number;
}

/** What a removal came to for one id. */
export type InventoryRemovalOutcome = "removed" | "gone" | "skipped" | "failed";

/** Every outcome, in the order a page lists them. */
export const INVENTORY_REMOVAL_OUTCOMES: readonly InventoryRemovalOutcome[] = ["removed", "gone", "skipped", "failed"];

/**
 * What a flow lets an operator remove of what its inventory found, the platform the removal goes to as the flow writes it, and
 * the storage paths a soft delete (500 ids a request) and a purge (one id at a time) call there.
 */
export interface InventoryRemovalPolicy {
  findings: string[];
  purge: boolean;
  endpoint: string;
  bulkDeletePath: string;
  purgePath: string;
}

/**
 * A removal an operator asks for: the finding, how much of each record, the count shown, the ids picked or the selection they
 * were taken from, and the partition typed back.
 */
export interface InventoryRemovalRequest {
  finding: string;
  scope: InventoryRemovalScope;
  expected: number;
  ids?: string[];
  /** The selection whose ids of the finding are removed: those that held what its conditions asked in OSDU. */
  selection?: number;
  confirm: string;
}

/** A removal queued as a run of the flow. */
export interface InventoryRemovalAccepted {
  runId: string;
  status: string;
  partition: string;
  inventory: string;
  finding: string;
  scope: InventoryRemovalScope;
  expected: number;
  selection?: number;
}

/** One removal of an inventory's ids: its run, who asked, what and how much, where it stands, and what it came to. */
export interface InventoryRemoval {
  inventoryRemovalId: number;
  inventoryId: number;
  runId?: string;
  actor: string;
  finding: string;
  scope: InventoryRemovalScope | string;
  namesIds: boolean;
  requested: number;
  status: InventoryRunStatus | string;
  startedUtc: string;
  completedUtc?: string;
  removed: number;
  gone: number;
  skipped: number;
  failed: number;
  error?: string;
  activityId?: number;
  /** The selection the removal took its ids from, with the conditions that made it; left out for one that took none. */
  selection?: InventorySelectionSummary;
}

/** What one removal did to one id, and why. */
export interface InventoryRemovalItem {
  inventoryRemovalItemId: number;
  inventoryRemovalId: number;
  inventoryRecordId: number;
  targetId: string;
  version?: number;
  finding: string;
  outcome: InventoryRemovalOutcome | string;
  reason?: string;
  ledgerFlowId?: string;
  deliveryKey?: string;
  recordedUtc: string;
}

/** A page of what a removal did to its ids, with the id the next page starts after; left out on the last page. */
export interface InventoryRemovalItemPage {
  items: InventoryRemovalItem[];
  next?: number;
}

/** A finding with how many ids have it, and whether a report raises it. */
export interface InventoryCount {
  finding: string;
  count: number;
  raised: boolean;
}

/** An identity that created ids of an inventory, with how many of the ids a ledger claims it created. */
export interface InventoryOwner {
  identity: string;
  records: number;
}

/** The owners a reconcile used, and how it knew them: `declared` by the flow, `inferred` from what a ledger claims, or `none`. */
export interface InventoryOwners {
  source?: string;
  identities: InventoryOwner[];
}

/** A build or reconcile: running while it reads, then completed or failed. */
export type InventoryRunStatus = "running" | "completed" | "failed";

/** One build or reconcile of an inventory: how it read, what it changed, and what it found when it reconciled. */
export interface InventoryRun {
  inventoryRunId: number;
  /** The platform run it was part of. */
  runId?: string;
  operation: "build" | "reconcile" | string;
  actor: string;
  status: InventoryRunStatus | string;
  startedUtc: string;
  completedUtc?: string;
  /** How it read OSDU: `search` or `storage`. */
  read: string;
  listed: number;
  pages: number;
  requests: number;
  added: number;
  changed: number;
  gone: number;
  returned: number;
  /** The ids a ledger expects that it read from storage, to tell missing from unlisted. */
  missingChecked: number;
  /** The findings it counted, those it found any of (on a run's own answers; a listing's newest run leaves them out). */
  findings?: InventoryCount[];
  raised?: number;
  owners?: InventoryOwners;
  error?: string;
}

/** An inventory as listings show it. */
export interface Inventory {
  inventoryId: number;
  partition: string;
  /** The inventory flow's ledger identity in the partition. */
  ledgerId: string;
  flowName: string;
  name: string;
  kind: string;
  query?: string;
  read: string;
  /** `latest` or `all`. */
  versions: string;
  createdUtc: string;
  updatedUtc: string;
  lastBuildRunId?: number;
  lastBuiltUtc?: string;
  lastReconcileRunId?: number;
  lastReconciledUtc?: string;
  /** The counts by finding the last reconcile wrote, every finding with its zeros. */
  reconciled?: InventoryCount[];
  raised?: number;
  /** The newest run: one under way, or one that failed after the last that completed. */
  latest?: InventoryRun;
}

/** The inventories of the workbench's partition, or of every partition with none picked. */
export interface InventoryList {
  partition?: string;
  inventories: Inventory[];
}

/** One inventory with its report: its counts by finding as its rows hold them now, its owners and its last runs. */
export interface InventoryDetail {
  inventory: Inventory;
  /** The pipeline of the flow declaring it and its repository, for its link and a run of it. */
  pipelineId?: string;
  repoId?: string;
  description?: string;
  /** Whether the flow still declares it; left out when the flow cannot be read now. */
  declared?: boolean;
  counts: InventoryCount[];
  ids: number;
  raised: number;
  owners?: InventoryOwners;
  lastBuild?: InventoryRun;
  lastReconcile?: InventoryRun;
  /** What the flow lets an operator remove of what the inventory found; left out when it allows none. */
  removal?: InventoryRemovalPolicy;
}

/** One id of an inventory: what OSDU serves of it, its finding and why, and what the ledgers hold of it. */
export interface InventoryRecord {
  inventoryRecordId: number;
  inventoryId: number;
  targetId: string;
  kind?: string;
  version?: number;
  createUser?: string;
  createTime?: string;
  modifyUser?: string;
  modifyTime?: string;
  firstSeenUtc?: string;
  changedUtc?: string;
  goneUtc?: string;
  finding: string;
  findingUtc: string;
  /** The ledger that holds the id, by its identity and its name. */
  ledgerFlowId?: string;
  ledger?: string;
  deliveryKey?: string;
  ledgerStatus?: string;
  ledgerVersion?: number;
  artifactId?: number;
  artifactState?: string;
  /** Why the id has its finding. */
  detail?: string;
  /** For an id of a selection: the version the search index held when the match read it. */
  matchedVersion?: number;
  /** For an id of a selection: what it held at each property the selection's conditions ask, property to values. */
  values?: Record<string, string[]>;
}

/** A page of ids, with the id the next page starts after; left out on the last page. */
export interface InventoryRecordPage {
  items: InventoryRecord[];
  next?: number;
}

/** What every inventory of a partition holds of one OSDU id, and what removals did to it, the newest first. */
export interface InventoryLookup {
  partition: string;
  targetId: string;
  hits: { inventory: Inventory; record: InventoryRecord }[];
  removals: InventoryRemovalItem[];
}

/** A parameter a run of an inventory flow takes. */
export interface InventoryParameter {
  name: string;
  required: boolean;
  default?: string;
  description?: string;
}

/** An inventory a flow declares or keeps, with the inventory as the partition keeps it once a build registered it. */
export interface InventoryEntry {
  name: string;
  description?: string;
  kind: string;
  query?: string;
  versions: string;
  declared: boolean;
  inventory?: Inventory;
}

/** One inventory flow in the workbench's partition: whether it reads it, how, and its inventories. */
export interface InventoryFlow {
  pipelineId: string;
  repoId: string;
  name: string;
  description?: string;
  batch?: string;
  partition?: string;
  readsPartition: boolean;
  partitions: string[];
  ledgerId?: string;
  read?: string;
  owners: string[];
  parameters: InventoryParameter[];
  problem?: string;
  inventories: InventoryEntry[];
}

/**
 * Where a selection's match stands: queued as a run of the flow, reading OSDU, completed, failed (it holds no id), or expired
 * (older than a week, kept for the removal that took its ids).
 */
export type InventorySelectionStatus = "queued" | "running" | "completed" | "failed" | "expired";

/** How a match read OSDU: every record the conditions find, joined with the inventory's ids; or the inventory's ids a chunk at a time. */
export type InventorySelectionDirection = "conditions" | "ids";

/**
 * The ids of an inventory that hold what an operator's conditions ask in OSDU (osdu/docs/reference/flow/inventory.md,
 * Selecting ids by what OSDU holds): the conditions as written, where the match stands and whether the inventory was built again
 * since (stale), the query and how it read OSDU, how far it got and what it found, and its ids counted by finding once it completed.
 */
export interface InventorySelection {
  selectionId: number;
  inventoryId: number;
  partition: string;
  status: InventorySelectionStatus | string;
  stale: boolean;
  conditions: ExplorerFilter[];
  query?: string;
  direction?: InventorySelectionDirection | string;
  notes: string[];
  /** The properties each id's row carries the values of: those the conditions ask. */
  columns: string[];
  /** The ids the inventory served that the match judged. */
  candidates: number;
  /** How much the match has to read, and has read: the records the conditions find, or the inventory's ids. */
  toCheck: number;
  checked: number;
  matched: number;
  /** Records the conditions found that the inventory holds no row of: written since its build, or outside its query. */
  outside: number;
  /** Ids matched at another version than the inventory listed, which a removal leaves for the next build. */
  changed: number;
  requests: number;
  /** Its ids by the finding the inventory gives each now, every finding with its zeros; once the match completed. */
  counts?: InventoryCount[];
  runId?: string;
  actor: string;
  createdUtc: string;
  startedUtc?: string;
  completedUtc?: string;
  error?: string;
}

/** The selection a removal took its ids from, as the removal names it. */
export interface InventorySelectionSummary {
  selectionId: number;
  status: string;
  conditions: ExplorerFilter[];
  query?: string;
  matched: number;
  matchedUtc?: string;
}

const inventoryPath = (partition: string, inventoryId: number) =>
  `/api/v1/delivery/inventories/${encodeURIComponent(partition)}/${inventoryId}`;

export const inventoryApi = {
  /** The inventories of the workbench's partition, each with what its last reconcile raised and its newest run. */
  list: () => get<InventoryList>("/api/v1/delivery/inventories"),
  /** An inventory flow's inventories in the workbench's partition. */
  flow: (pipelineId: string) => get<InventoryFlow>(`/api/v1/delivery/flows/${pipelineId}/inventories`),
  /** One inventory with its counts by finding, owners and last runs. */
  inventory: (partition: string, inventoryId: number) => get<InventoryDetail>(inventoryPath(partition, inventoryId)),
  /** A page of an inventory's ids, of one finding or every one (only those a selection holds, when one is named), after the id `after` names. */
  records: (partition: string, inventoryId: number, query: { finding?: string; after?: number; limit?: number; selection?: number } = {}) =>
    get<InventoryRecordPage>(`${inventoryPath(partition, inventoryId)}/records`, query),
  /** An inventory's builds and reconciles, newest first. */
  runs: (partition: string, inventoryId: number, limit?: number) =>
    get<InventoryRun[]>(`${inventoryPath(partition, inventoryId)}/runs`, limit === undefined ? {} : { limit }),
  /** What every inventory of the workbench's partition holds of one OSDU id. */
  lookup: (id: string) => get<InventoryLookup>("/api/v1/delivery/inventories/lookup", { id }),
  /** An inventory's ids of one finding (every id with none) as CSV text; only those a selection holds, when one is named. */
  exportCsv: (partition: string, inventoryId: number, finding?: string, selection?: number) => {
    const query = new URLSearchParams();
    if (finding !== undefined) {
      query.set("finding", finding);
    }

    if (selection !== undefined) {
      query.set("selection", String(selection));
    }

    const text = query.toString();
    return getText(`${inventoryPath(partition, inventoryId)}/export${text === "" ? "" : `?${text}`}`);
  },
  /**
   * The ids of the inventory that hold what the conditions ask in OSDU: the match made of the same conditions against the same
   * build when there is one, else a new match queued as a run of the flow (`refresh` always queues one).
   */
  select: (partition: string, inventoryId: number, request: { filters: ExplorerFilter[]; refresh?: boolean }) =>
    post<InventorySelection>(`${inventoryPath(partition, inventoryId)}/selections`, request),
  /** One selection of the inventory: where its match stands, and its ids counted by finding once it completed. */
  selection: (partition: string, inventoryId: number, selectionId: number) =>
    get<InventorySelection>(`${inventoryPath(partition, inventoryId)}/selections/${selectionId}`),
  /** Queues the run that removes from OSDU the ids of one finding of the inventory, as the caller. */
  remove: (partition: string, inventoryId: number, request: InventoryRemovalRequest) =>
    post<InventoryRemovalAccepted>(`${inventoryPath(partition, inventoryId)}/removals`, request),
  /** Delivers again, through their flows, the records behind missing ids of the inventory, as the caller. */
  repair: (partition: string, inventoryId: number, request: InventoryRepairRequest) =>
    post<InventoryRepairResult>(`${inventoryPath(partition, inventoryId)}/repairs`, request),
  /** An inventory's removals, newest first. */
  removals: (partition: string, inventoryId: number, limit?: number) =>
    get<InventoryRemoval[]>(`${inventoryPath(partition, inventoryId)}/removals`, limit === undefined ? {} : { limit }),
  /** A page of what one removal did to its ids, of one outcome or every one, after the item `after` names. */
  removalItems: (partition: string, removalId: number, query: { outcome?: string; after?: number; limit?: number } = {}) =>
    get<InventoryRemovalItemPage>(`/api/v1/delivery/inventories/${encodeURIComponent(partition)}/removals/${removalId}/items`, query),
};
