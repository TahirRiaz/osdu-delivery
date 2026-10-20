// The OSDU order's API (osdu/docs/reference/concepts/osdu-order.md): OSDU's lineage by release, the order of each
// partition with every repository's deliveries, and which schedules run best before which. Same conventions as
// delivery.ts: one function per endpoint, pages compose them with TanStack Query.

import { get, post } from "@/api/client";

/** A kind of a release that could not be read, and why. */
export interface OrderReleaseFailure {
  kind: string;
  reason: string;
}

/** A built release of OSDU's lineage: where it was read from, who built it and when, and what it holds. */
export interface OrderRelease {
  release: string;
  commit: string;
  publishedUtc: string | null;
  builtUtc: string;
  builtBy: string;
  kinds: number;
  relationships: number;
  failures: OrderReleaseFailure[];
}

/** A kind of an entity type in a release, with its status there. */
export interface OrderKind {
  kind: string;
  version: string;
  status: string | null;
}

/** An entity type of a release, or a group a relationship names alone (no kinds): how many types it refers to and is referred to by. */
export interface OrderEntity {
  entityType: string;
  group: string;
  kinds: OrderKind[];
  refersTo: number;
  referredBy: number;
}

export interface OrderEntities {
  release: OrderRelease;
  entities: OrderEntity[];
}

/** Records of `from` refer to records of `to` through each of `properties`. */
export interface OrderGraphEdge {
  from: string;
  to: string;
  properties: string[];
}

export type OrderDirection = "both" | "out" | "in";

/** The part of a release's lineage around one entity type. */
export interface OrderGraph {
  release: OrderRelease;
  entity: string;
  depth: number;
  direction: OrderDirection;
  nodes: OrderEntity[];
  edges: OrderGraphEdge[];
  truncated: boolean;
}

/** A delivery of a partition's order. `level` is the level of the entity type it writes; `known` is false when its kind's relationships are not. */
export interface OrderDelivery {
  repository: string | null;
  flow: string;
  interface: string | null;
  kind: string;
  entityType: string;
  level: number;
  known: boolean;
}

export interface OrderReason {
  repository: string | null;
  flow: string;
  interface: string | null;
  property: string;
  searched: boolean;
}

/** A reference the order keeps: records of `from` wait for records of `to`. */
export interface OrderEdge {
  from: string;
  to: string;
  because: OrderReason[];
}

export type OrderCutReason = "BackReference" | "GivesWayToSearch" | "Circular";

export interface OrderCut {
  from: string;
  to: string;
  reason: OrderCutReason;
  why: string;
}

/** The order of one partition: entity types by level, the deliveries, the references kept and cut, and the warnings. */
export interface OrderPartition {
  partition: string;
  levels: string[][];
  deliveries: OrderDelivery[];
  edges: OrderEdge[];
  cuts: OrderCut[];
  selfReferences: string[];
  warnings: string[];
}

export interface OrderPartitions {
  partitions: OrderPartition[];
  problems: string[];
}

/** A schedule, and what decides when it fires: a cron, an interval, or neither when it fires after other schedules. */
export interface OrderSchedule {
  id: string;
  repoId: string;
  repository: string;
  name: string;
  cron: string | null;
  intervalSeconds: number | null;
  timezone: string;
  fires: boolean;
  lastFireUtc: string | null;
  nextFireUtc: string | null;
}

export interface OrderScheduleReason {
  flow: string;
  interface: string | null;
  entityType: string;
  refersTo: string;
  providerRepository: string;
  providerFlow: string;
  providerInterface: string | null;
  partitions: string[];
}

/**
 * How the fires of a schedule and of one that runs best before it fall: `chained`, it fires after it (`after:`);
 * `firesFirst`, the earlier one fires next; `firesAfter`, it fires next, before the earlier one; `notFiring`, the earlier
 * one is disabled or paused; `unknown`, one of them has no next fire to compare.
 */
export type OrderTiming = "chained" | "firesFirst" | "firesAfter" | "notFiring" | "unknown";

export interface OrderScheduleFirst {
  schedule: OrderSchedule;
  sameRepository: boolean;
  timing: OrderTiming;
  because: OrderScheduleReason[];
}

export interface OrderScheduleOrder {
  schedule: OrderSchedule;
  runFirst: OrderScheduleFirst[];
  unscheduled: OrderScheduleReason[];
  outOfOrder: boolean;
}

export interface OrderSchedules {
  schedules: OrderScheduleOrder[];
  circles: OrderSchedule[][];
}

const path = (release: string) => `/api/v1/delivery/order/releases/${encodeURIComponent(release)}`;

export const orderApi = {
  releases: () => get<OrderRelease[]>("/api/v1/delivery/order/releases"),
  build: (release: string | null) => post<OrderRelease>("/api/v1/delivery/order/releases", { release }),
  entities: (release: string) => get<OrderEntities>(`${path(release)}/entities`),
  graph: (release: string, entity: string, depth: number, direction: OrderDirection) =>
    get<OrderGraph>(`${path(release)}/graph`, { entity, depth, direction }),
  partitions: () => get<OrderPartitions>("/api/v1/delivery/order/partitions"),
  schedules: () => get<OrderSchedules>("/api/v1/delivery/order/schedules"),
};
