// The explorer's calls (osdu/docs/explorer.md): how a partition of OSDU is reached, and the reads a node makes of it there,
// the kinds its records are of, a page of the records a search finds, the properties a kind's records hold, and one record.
// Every read is a compute task the control plane queues; the explorer waits on it as the record page waits on a read-back.

import { get, post } from "@/api/client";
import type { ComputeTaskAccepted } from "@/api/types";
import type { DeliveryOsduRead } from "./delivery";

/** How the explorer reaches a partition: through which flow's connection, or why nothing does. */
export interface ExplorerConnection {
  partition: string | null;
  available: boolean;
  /** The flow (and interface) whose OSDU connection the explorer reads through. */
  through: string | null;
  /** The endpoint that flow names, as its document writes it. */
  endpoint: string | null;
  route: string | null;
  reason: string | null;
}

/** How the platform indexes a property, which decides how a value of it is asked for. */
export type ExplorerIndex = "text" | "keyword" | "number" | "boolean" | "date";

export type ExplorerSort = "relevance" | "modified" | "created";

/** A property value a page narrows to. */
export interface ExplorerFilter {
  path: string;
  index: ExplorerIndex;
  value: string;
}

/** What the explorer asks of OSDU. */
export interface ExplorerSearchRequest {
  text?: string;
  lucene?: boolean;
  mentions?: string;
  kind?: string;
  filters?: ExplorerFilter[];
  sort?: ExplorerSort;
  offset?: number;
  limit?: number;
  facet?: { path: string; index: ExplorerIndex };
}

/** How the explorer read a search: everything, a Lucene query, an id, the start of one, text, or a record's mentions. */
export type ExplorerReading = "everything" | "lucene" | "id" | "idPrefix" | "text" | "mentions";

export interface ExplorerHit {
  id: string;
  kind: string | null;
  version: number | null;
  name: string | null;
  nameField: string | null;
  createTime: string | null;
  createUser: string | null;
  modifyTime: string | null;
  modifyUser: string | null;
}

export interface ExplorerBucket {
  value: string | null;
  count: number;
}

export interface ExplorerPage {
  reading: ExplorerReading;
  query: string | null;
  kind: string;
  total: number;
  offset: number;
  hits: ExplorerHit[];
  facetPath: string | null;
  facet: ExplorerBucket[] | null;
  notes: string[];
  refusal: string | null;
}

export interface ExplorerTypes {
  reading: ExplorerReading;
  query: string | null;
  total: number;
  kinds: { kind: string; count: number }[];
  /** What the kinds add up to: less than the total when the service named fewer groups than there are. */
  listed: number;
  notes: string[];
  refusal: string | null;
}

export interface ExplorerFieldInfo {
  path: string;
  index: ExplorerIndex;
}

export interface ExplorerFields {
  kind: string;
  sampleId: string | null;
  fields: ExplorerFieldInfo[];
}

/** One of the explorer's answers, with the connection and partition it was read through. */
export interface ExplorerAnswer<T> {
  connection: string;
  partition: string | null;
  correlationId: string;
  answeredUtc: string;
  answer: T;
}

/** The furthest a page of the search service reaches (Elasticsearch's result window). */
export const EXPLORER_WINDOW = 10_000;

/** The rows one page reads. */
export const EXPLORER_PAGE = 100;

/** The largest page the API takes. */
export const EXPLORER_MAX_PAGE = 200;

const partitionQuery = (partition: string | null) => (partition === null ? "" : `?partition=${encodeURIComponent(partition)}`);

export const explorerApi = {
  /** How the partition is reached; the workbench's when none is named. */
  connection: (partition: string | null) =>
    get<ExplorerConnection>("/api/v1/delivery/explorer/connection", partition === null ? {} : { partition }),
  /** Queues the count of the records a search finds, kind by kind. */
  types: (partition: string | null, request: ExplorerSearchRequest) =>
    post<ComputeTaskAccepted>(`/api/v1/delivery/explorer/types${partitionQuery(partition)}`, request),
  /** Queues one page of the records a search finds. */
  search: (partition: string | null, request: ExplorerSearchRequest) =>
    post<ComputeTaskAccepted>(`/api/v1/delivery/explorer/search${partitionQuery(partition)}`, request),
  /** Queues a read of the properties a kind's records hold. */
  fields: (partition: string | null, kind: string) =>
    post<ComputeTaskAccepted>(`/api/v1/delivery/explorer/fields${partitionQuery(partition)}`, { kind }),
  /** Queues a read of one record from the storage service, at its latest or at one version. */
  read: (partition: string | null, targetId: string, version?: number) =>
    post<ComputeTaskAccepted>(`/api/v1/delivery/explorer/read${partitionQuery(partition)}`, version === undefined ? { targetId } : { targetId, version }),
};

/** A record read by the explorer: what a record page's read-back answers, read from the storage service. */
export type ExplorerRead = DeliveryOsduRead;
