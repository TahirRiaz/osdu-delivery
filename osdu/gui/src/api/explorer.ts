// The explorer's calls (osdu/docs/explorer.md): how a partition of OSDU is reached, and the reads made of it there, the
// kinds its records are of, a page of the records a search finds, the properties a kind's records hold, and one record.
// Every read is answered by the control plane at once, through a connection it keeps open between reads.

import { get, post } from "@/api/client";
import type { DeliveryDimension, DeliveryDimensionAttribute, DeliveryDimensionYaml, DeliveryOsduRead, DimensionBlueprint } from "./delivery";

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
  /** The count of the records a search finds, kind by kind. */
  types: (partition: string | null, request: ExplorerSearchRequest) =>
    post<ExplorerAnswer<ExplorerTypes>>(`/api/v1/delivery/explorer/types${partitionQuery(partition)}`, request),
  /** One page of the records a search finds. */
  search: (partition: string | null, request: ExplorerSearchRequest) =>
    post<ExplorerAnswer<ExplorerPage>>(`/api/v1/delivery/explorer/search${partitionQuery(partition)}`, request),
  /** The properties a kind's records hold, read from one of them. */
  fields: (partition: string | null, kind: string) =>
    post<ExplorerAnswer<ExplorerFields>>(`/api/v1/delivery/explorer/fields${partitionQuery(partition)}`, { kind }),
  /** One record from the storage service, at its latest or at one version. */
  read: (partition: string | null, targetId: string, version?: number) =>
    post<DeliveryOsduRead>(`/api/v1/delivery/explorer/read${partitionQuery(partition)}`, version === undefined ? { targetId } : { targetId, version }),
};

/** A record read by the explorer: what a record page's read-back answers, read from the storage service. */
export type ExplorerRead = DeliveryOsduRead;

// ---- The query of an element (osdu/docs/explorer.md, The query of an element) ----

/** A query that finds records by an element of a record, and what it finds, in words. */
export interface ExplorerElementQuery {
  /** `exact` (exactly this value, or every value a section holds), `words` (the words of a text, their case aside) or `exists` (any value there). */
  purpose: "exact" | "words" | "exists";
  /** The Lucene query, as the search service's `query` takes it. */
  query: string;
  says: string;
}

/** How the index holds an element: its path, how it is kept (`text`, `keyword`, `number`, `boolean`, `date`, or `section`), and the nested or flattened list it sits in. */
export interface ExplorerElementField {
  path: string;
  index: string;
  nestedPath: string | null;
  flattenedPath: string | null;
}

/** The queries that find records by an element, the exact one first; how the index holds it, in words; the template read; why a query is a guess; and why none can be written, where none can. */
export interface ExplorerElementAnswer {
  kind: string;
  path: string;
  template: { kind: string; version: string } | null;
  field: ExplorerElementField | null;
  reading: string | null;
  queries: ExplorerElementQuery[];
  /** Why the queries are written from the values, where the template could not say how they are indexed; null otherwise. */
  guess: string | null;
  problem: string | null;
  notes: string[];
}

/** A value inside a section, by its path in the record, and what the record holds there. */
export interface ExplorerElementLeaf {
  path: string;
  value: string | number | boolean | null;
}

export interface ExplorerElementRequest {
  kind: string;
  /** The element's path as the record inspector names it: `data.GeoContexts[1].GeoTypeID`. */
  path: string;
  section: boolean;
  value?: string | number | boolean | null;
  /** For a section, the values it holds at any depth, at most 48. */
  values?: ExplorerElementLeaf[];
}

/** The Lucene queries that find records by an element of a record, read from the saved template of its kind. */
export function elementQueries(request: ExplorerElementRequest) {
  return post<ExplorerElementAnswer>("/api/v1/delivery/explorer/element-queries", request);
}

// ---- The dimension builder (osdu/docs/explorer.md, Building a dimension) ----

/** An attribute of a drafted dimension: read through the record a key names (`steps`), or collected from its own records (`collect`). */
export interface DimensionDraftAttribute {
  name: string;
  steps: string[] | null;
  collect: string | null;
}

/** A clean step of a drafted dimension: its name, and a `replace` step's pattern and replacement. */
export interface DimensionDraftCleanStep {
  step: string;
  pattern: string | null;
  with: string | null;
}

/** A dimension as the builder holds it while a person picks it: every part of a `dimensions` item. */
export interface DimensionDraft {
  name: string;
  description: string | null;
  kind: string;
  query: string | null;
  path: string | null;
  label: string[];
  unlabelled: string | null;
  attributes: DimensionDraftAttribute[];
  clean: DimensionDraftCleanStep[];
  keyColumn: string | null;
  valueColumn: string | null;
  countRecords: boolean;
  maxValues: number | null;
}

/** What the builder found about a draft: an error keeps it from loading or building, a warning does not. */
export interface DimensionDraftIssue {
  severity: "error" | "warning";
  message: string;
  /** The part it is about, as the YAML's spans name them (`path`, `attributes.Country`); null for the whole. */
  target: string | null;
  /** `template` (a saved template is missing) or `name` (another flow's dimension writes the table); null otherwise. */
  code: string | null;
}

/** How the platform indexes a path. */
export interface DimensionFieldWire {
  path: string;
  index: string;
  nestedPath: string | null;
}

/** A key of the dimension with how many of its records hold it, as the search counts them. */
export interface DimensionKeyCount {
  key: string;
  count: number;
}

/** The commonest keys a drafted dimension reads, which its example steps through. */
export interface DimensionKeys {
  /** The records the kind (and the query) holds. */
  total: number;
  keys: DimensionKeyCount[];
  moreKeys: boolean;
  keyField: DimensionFieldWire | null;
  keyFieldGuessed: boolean;
  notes: string[];
  /** The search service's own words when it refused the query. */
  refusal: string | null;
}

export interface DimensionKeysRequest {
  kind: string;
  query?: string | null;
  path: string;
}

/** A property a kind's template suggests as a dimension's key: a value naming another record. */
export interface DimensionKeyCandidate {
  /** The path as a dimension writes it, a list stepped into by its name. */
  path: string;
  /** The entity types the value names (`master-data--Wellbore`), or a group alone. */
  names: string[];
  /** Whether a record holds several values there: a list of ids, or a property of the items of a list. */
  repeated: boolean;
  title: string | null;
  description: string | null;
}

/** The keys a kind's saved template suggests, the likeliest first, or why there are none. */
export interface DimensionKeySuggestions {
  kind: string;
  template: { kind: string; version: string } | null;
  keys: DimensionKeyCandidate[];
  missing: string | null;
}

/** One key of a draft made into its row as a build makes it. */
export interface DimensionExample {
  key: string;
  label: string | null;
  labelFrom: string | null;
  problem: string | null;
  value: string | null;
  leftOut: string | null;
  note: string | null;
  attributes: DeliveryDimensionAttribute[];
  records: number | null;
  filter: string | null;
  notes: string[];
}

/** A drafted dimension written as YAML and checked, with its example row. */
export interface DimensionCompose {
  /** The item a dimension flow lists under `dimensions`, as it is copied. */
  yaml: string;
  item: DeliveryDimensionYaml;
  dimension: DeliveryDimension | null;
  blueprint: DimensionBlueprint | null;
  table: string | null;
  issues: DimensionDraftIssue[];
  valid: boolean;
  example: DimensionExample | null;
  exampleProblem: string | null;
}

/** The keys the saved template of `kind` suggests for a dimension, the likeliest first; read from the templates alone. */
export function dimensionKeyCandidates(kind: string) {
  return get<DimensionKeySuggestions>(`/api/v1/delivery/explorer/dimension/candidates?kind=${encodeURIComponent(kind)}`);
}

/** The commonest keys of a drafted dimension's path, read through the partition's connection. */
export function dimensionKeys(partition: string | null, request: DimensionKeysRequest) {
  return post<ExplorerAnswer<DimensionKeys>>(`/api/v1/delivery/explorer/dimension/keys${partitionQuery(partition)}`, request);
}

/** A drafted dimension written as YAML and checked, with `example` made into its row. */
export function composeDimension(partition: string | null, draft: DimensionDraft, example: string | null) {
  return post<DimensionCompose>(`/api/v1/delivery/explorer/dimension/compose${partitionQuery(partition)}`, { draft, example });
}
