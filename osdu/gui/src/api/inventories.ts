// The inventories of inventory flows (osdu/docs/inventory-plan.md): every id an OSDU kind holds in a partition, set against
// every ledger of the partition, each id with its finding. Same conventions as delivery.ts: one function per endpoint, pages
// compose them with TanStack Query. An answer leaves out what holds no value, so every field that may hold none is optional
// here, and a page tests it with `=== undefined` or `??`, never `=== null`.

import { get, getText, post } from "@/api/client";

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

/** How much of each record a removal takes: a soft delete (`record`, reversible) or a purge (`everything`). */
export type InventoryRemovalScope = "record" | "everything";

/** What a removal came to for one id. */
export type InventoryRemovalOutcome = "removed" | "gone" | "skipped" | "failed";

/** Every outcome, in the order a page lists them. */
export const INVENTORY_REMOVAL_OUTCOMES: readonly InventoryRemovalOutcome[] = ["removed", "gone", "skipped", "failed"];

/** What a flow lets an operator remove of what its inventory found, and the platform the removal goes to as the flow writes it. */
export interface InventoryRemovalPolicy {
  findings: string[];
  purge: boolean;
  endpoint: string;
}

/** A removal an operator asks for: the finding, how much of each record, the count shown, the ids picked, and the partition typed back. */
export interface InventoryRemovalRequest {
  finding: string;
  scope: InventoryRemovalScope;
  expected: number;
  ids?: string[];
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

const inventoryPath = (partition: string, inventoryId: number) =>
  `/api/v1/delivery/inventories/${encodeURIComponent(partition)}/${inventoryId}`;

export const inventoryApi = {
  /** The inventories of the workbench's partition, each with what its last reconcile raised and its newest run. */
  list: () => get<InventoryList>("/api/v1/delivery/inventories"),
  /** An inventory flow's inventories in the workbench's partition. */
  flow: (pipelineId: string) => get<InventoryFlow>(`/api/v1/delivery/flows/${pipelineId}/inventories`),
  /** One inventory with its counts by finding, owners and last runs. */
  inventory: (partition: string, inventoryId: number) => get<InventoryDetail>(inventoryPath(partition, inventoryId)),
  /** A page of an inventory's ids, of one finding or every one, after the id `after` names. */
  records: (partition: string, inventoryId: number, query: { finding?: string; after?: number; limit?: number } = {}) =>
    get<InventoryRecordPage>(`${inventoryPath(partition, inventoryId)}/records`, query),
  /** An inventory's builds and reconciles, newest first. */
  runs: (partition: string, inventoryId: number, limit?: number) =>
    get<InventoryRun[]>(`${inventoryPath(partition, inventoryId)}/runs`, limit === undefined ? {} : { limit }),
  /** What every inventory of the workbench's partition holds of one OSDU id. */
  lookup: (id: string) => get<InventoryLookup>("/api/v1/delivery/inventories/lookup", { id }),
  /** An inventory's ids of one finding (every id with none) as CSV text. */
  exportCsv: (partition: string, inventoryId: number, finding?: string) =>
    getText(`${inventoryPath(partition, inventoryId)}/export${finding === undefined ? "" : `?finding=${encodeURIComponent(finding)}`}`),
  /** Queues the run that removes from OSDU the ids of one finding of the inventory, as the caller. */
  remove: (partition: string, inventoryId: number, request: InventoryRemovalRequest) =>
    post<InventoryRemovalAccepted>(`${inventoryPath(partition, inventoryId)}/removals`, request),
  /** An inventory's removals, newest first. */
  removals: (partition: string, inventoryId: number, limit?: number) =>
    get<InventoryRemoval[]>(`${inventoryPath(partition, inventoryId)}/removals`, limit === undefined ? {} : { limit }),
  /** A page of what one removal did to its ids, of one outcome or every one, after the item `after` names. */
  removalItems: (partition: string, removalId: number, query: { outcome?: string; after?: number; limit?: number } = {}) =>
    get<InventoryRemovalItemPage>(`/api/v1/delivery/inventories/${encodeURIComponent(partition)}/removals/${removalId}/items`, query),
};
