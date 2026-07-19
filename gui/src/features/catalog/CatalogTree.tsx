import { useEffect, useMemo, useState, type ReactNode } from "react";
import { useInfiniteQuery, useQuery } from "@tanstack/react-query";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Chip from "@mui/material/Chip";
import CircularProgress from "@mui/material/CircularProgress";
import List from "@mui/material/List";
import ListItemButton from "@mui/material/ListItemButton";
import ListItemIcon from "@mui/material/ListItemIcon";
import ListItemText from "@mui/material/ListItemText";
import Skeleton from "@mui/material/Skeleton";
import Stack from "@mui/material/Stack";
import TextField from "@mui/material/TextField";
import Typography from "@mui/material/Typography";
import AccountTreeIcon from "@mui/icons-material/AccountTree";
import CloudOutlinedIcon from "@mui/icons-material/CloudOutlined";
import ComputerOutlinedIcon from "@mui/icons-material/ComputerOutlined";
import DnsOutlinedIcon from "@mui/icons-material/DnsOutlined";
import FolderOutlinedIcon from "@mui/icons-material/FolderOutlined";
import FolderSharedOutlinedIcon from "@mui/icons-material/FolderSharedOutlined";
import Inventory2OutlinedIcon from "@mui/icons-material/Inventory2Outlined";
import InsertDriveFileOutlinedIcon from "@mui/icons-material/InsertDriveFileOutlined";
import LayersOutlinedIcon from "@mui/icons-material/LayersOutlined";
import PublicOutlinedIcon from "@mui/icons-material/PublicOutlined";
import SchemaOutlinedIcon from "@mui/icons-material/SchemaOutlined";
import SourceOutlinedIcon from "@mui/icons-material/SourceOutlined";
import StorageOutlinedIcon from "@mui/icons-material/StorageOutlined";
import { SimpleTreeView } from "@mui/x-tree-view/SimpleTreeView";
import { TreeItem } from "@mui/x-tree-view/TreeItem";
import { lineageApi, pipelineApi, repoApi } from "../../api/endpoints";
import type { FileNode, FileOriginKind, LineageObject, PagedResult, PipelineSummary, Repo, SchemaKindCount } from "../../api/types";
import { compareKinds, metaForKind } from "./kindMeta";
import { encodeNodeId, UNRESOLVED_LABEL, decodeNodeId, type CatalogNode } from "./nodeIds";

const LEAF_PAGE_SIZE = 200;

/** A tree label row: icon, name, and a right-aligned count badge. */
function NodeLabel({ icon, text, count }: { icon?: ReactNode; text: string; count?: number }) {
  return (
    <Stack direction="row" spacing={1} alignItems="center" sx={{ minWidth: 0, py: 0.25 }}>
      {icon !== undefined && (
        <Box sx={{ display: "inline-flex", color: "text.secondary", flexShrink: 0 }}>{icon}</Box>
      )}
      <Typography variant="body2" noWrap sx={{ minWidth: 0, flexGrow: 1 }}>{text}</Typography>
      {count !== undefined && (
        <Chip label={count} size="small" variant="outlined" sx={{ height: 18, fontSize: 11, flexShrink: 0 }} />
      )}
    </Stack>
  );
}

const lower = (value: string) => value.toLowerCase();
const byNameCi = (a: string, b: string) => a.localeCompare(b, undefined, { sensitivity: "base" });

// ---- Databases branch (server/db > schema > kind > object) -----------------------------------------------

interface DatabaseBranch { database: string | null; objectCount: number; schemas: SchemaBranch[] }
interface SchemaBranch { schema: string | null; objectCount: number; kinds: KindCount[] }
interface KindCount { kind: string; objectCount: number }

/** Folds the flat per-kind rows into database > schema > kind, excluding files (they live under Sources) and
 * merging connection-reference aliases so one database reached via two refs is one node. */
function foldDatabases(rows: SchemaKindCount[]): DatabaseBranch[] {
  const databases = new Map<string | null, DatabaseBranch>();
  for (const row of rows) {
    if (row.kind === "File") {
      continue;
    }
    let database = databases.get(row.database);
    if (database === undefined) {
      database = { database: row.database, objectCount: 0, schemas: [] };
      databases.set(row.database, database);
    }
    database.objectCount += row.objectCount;
    let schema = database.schemas.find((s) => s.schema === row.schema);
    if (schema === undefined) {
      schema = { schema: row.schema, objectCount: 0, kinds: [] };
      database.schemas.push(schema);
    }
    schema.objectCount += row.objectCount;
    const kind = schema.kinds.find((k) => k.kind === row.kind);
    if (kind === undefined) {
      schema.kinds.push({ kind: row.kind, objectCount: row.objectCount });
    } else {
      kind.objectCount += row.objectCount;
    }
  }

  const branches = [...databases.values()];
  for (const database of branches) {
    database.schemas.sort((a, b) => byNameCi(a.schema ?? "", b.schema ?? ""));
    for (const schema of database.schemas) {
      schema.kinds.sort((a, b) => compareKinds(a.kind, b.kind));
    }
  }
  branches.sort((a, b) => byNameCi(a.database ?? "", b.database ?? ""));
  return branches;
}

/** Paged object leaves under a database kind folder, lazy-loaded on expand. */
function PagedObjectLeaves({
  parentId, queryKey, fetchPage,
}: {
  parentId: string;
  queryKey: readonly unknown[];
  fetchPage: (page: number) => Promise<PagedResult<LineageObject>>;
}) {
  const objects = useInfiniteQuery({
    queryKey,
    queryFn: ({ pageParam }) => fetchPage(pageParam),
    initialPageParam: 1,
    getNextPageParam: (last) => (last.page * last.pageSize < last.total ? last.page + 1 : undefined),
  });

  if (objects.isPending) {
    return <TreeItem itemId={`${parentId}#loading`} disabled label={<NodeLabel text="Loading…" />} />;
  }
  if (objects.isError) {
    return (
      <TreeItem itemId={`${parentId}#error`} disabled
        label={<NodeLabel text={`Could not load objects: ${String(objects.error)}`} />} />
    );
  }

  const rows = objects.data.pages.flatMap((page) => page.items);
  const total = objects.data.pages[0]?.total ?? 0;
  const remaining = total - rows.length;
  return (
    <>
      {rows.length === 0 && <TreeItem itemId={`${parentId}#empty`} disabled label={<NodeLabel text="No objects" />} />}
      {rows.map((row) => (
        <TreeItem
          key={row.key}
          itemId={encodeNodeId({ type: "object", objectKey: row.key })}
          label={<NodeLabel icon={metaForKind(row.kind).icon} text={row.name} />}
        />
      ))}
      {remaining > 0 && <LoadMore parentId={parentId} remaining={remaining} loading={objects.isFetchingNextPage} onMore={() => void objects.fetchNextPage()} />}
    </>
  );
}

function LoadMore({ parentId, remaining, loading, onMore }: { parentId: string; remaining: number; loading: boolean; onMore: () => void }) {
  return (
    <TreeItem itemId={`${parentId}#more`} label={(
      <Button size="small" disabled={loading} startIcon={loading ? <CircularProgress size={14} /> : undefined}
        onClick={(event) => { event.stopPropagation(); onMore(); }}>
        {`Load more (${remaining} remaining)`}
      </Button>
    )} />
  );
}

// ---- Sources branch (file origins: origin > container > folder > file) -----------------------------------

interface FolderTrie {
  folders: Map<string, FolderTrie>;
  files: { key: string; name: string }[];
  count: number;
}

interface OriginGroup {
  origin: string;
  count: number;
  containers: Map<string, FolderTrie>;
  root: FolderTrie;
}

interface ProviderGroup {
  kind: FileOriginKind;
  count: number;
  origins: OriginGroup[];
}

const newTrie = (): FolderTrie => ({ folders: new Map(), files: [], count: 0 });

/** The provider (the logical parent above a storage account): its label, icon, order, and whether it has an
 * origin level. Local files have no distinct origin (one filesystem), so they render folders directly under
 * the provider; every cloud/server provider groups its accounts/buckets/hosts as origins first. */
interface ProviderMeta { label: string; icon: ReactNode; order: number; hasOrigin: boolean }

const PROVIDER_META: Record<FileOriginKind, ProviderMeta> = {
  AzureStorage: { label: "Microsoft Azure", icon: <CloudOutlinedIcon fontSize="small" />, order: 0, hasOrigin: true },
  AmazonS3: { label: "Amazon S3", icon: <CloudOutlinedIcon fontSize="small" />, order: 1, hasOrigin: true },
  GoogleCloud: { label: "Google Cloud", icon: <CloudOutlinedIcon fontSize="small" />, order: 2, hasOrigin: true },
  Sftp: { label: "SFTP / FTP servers", icon: <DnsOutlinedIcon fontSize="small" />, order: 3, hasOrigin: true },
  NetworkShare: { label: "Network shares", icon: <FolderSharedOutlinedIcon fontSize="small" />, order: 4, hasOrigin: true },
  Local: { label: "Local files", icon: <ComputerOutlinedIcon fontSize="small" />, order: 5, hasOrigin: false },
  Other: { label: "Other sources", icon: <PublicOutlinedIcon fontSize="small" />, order: 6, hasOrigin: true },
};

/** Folds the flat file-node list into provider > origin > (container) > folder trie > file, with recursive
 * counts at every level. */
function foldFileProviders(files: FileNode[]): ProviderGroup[] {
  const providers = new Map<FileOriginKind, Map<string, OriginGroup>>();
  for (const file of files) {
    let origins = providers.get(file.originKind);
    if (origins === undefined) {
      origins = new Map();
      providers.set(file.originKind, origins);
    }
    let group = origins.get(file.origin);
    if (group === undefined) {
      group = { origin: file.origin, count: 0, containers: new Map(), root: newTrie() };
      origins.set(file.origin, group);
    }
    group.count++;

    let trie: FolderTrie;
    if (file.container !== null) {
      trie = group.containers.get(file.container) ?? newTrie();
      group.containers.set(file.container, trie);
    } else {
      trie = group.root;
    }

    trie.count++;
    for (const segment of file.path ? file.path.split("/") : []) {
      let child = trie.folders.get(segment);
      if (child === undefined) {
        child = newTrie();
        trie.folders.set(segment, child);
      }
      trie = child;
      trie.count++;
    }
    trie.files.push({ key: file.key, name: file.name });
  }

  return [...providers.entries()]
    .map(([kind, origins]) => {
      const originList = [...origins.values()].sort((a, b) => byNameCi(a.origin, b.origin));
      return { kind, count: originList.reduce((sum, o) => sum + o.count, 0), origins: originList };
    })
    .sort((a, b) => PROVIDER_META[a.kind].order - PROVIDER_META[b.kind].order);
}

/** Renders a folder trie's subfolders (recursively) then its files, under the given provider/origin/container. */
function renderTrie(provider: string, origin: string, container: string | null, parentPath: string, trie: FolderTrie): ReactNode[] {
  const folders = [...trie.folders.entries()]
    .sort(([a], [b]) => byNameCi(a, b))
    .map(([name, child]) => {
      const path = parentPath ? `${parentPath}/${name}` : name;
      const id = encodeNodeId({ type: "folder", provider, origin, container, path });
      return (
        <TreeItem key={id} itemId={id} label={<NodeLabel icon={<FolderOutlinedIcon fontSize="small" />} text={name} count={child.count} />}>
          {renderTrie(provider, origin, container, path, child)}
        </TreeItem>
      );
    });
  const files = [...trie.files]
    .sort((a, b) => byNameCi(a.name, b.name))
    .map((file) => (
      <TreeItem
        key={file.key}
        itemId={encodeNodeId({ type: "object", objectKey: file.key })}
        label={<NodeLabel icon={<InsertDriveFileOutlinedIcon fontSize="small" />} text={file.name} />}
      />
    ));
  return [...folders, ...files];
}

function OriginNode({ provider, group }: { provider: FileOriginKind; group: OriginGroup }) {
  const originId = encodeNodeId({ type: "origin", provider, origin: group.origin });
  const containers = [...group.containers.entries()].sort(([a], [b]) => byNameCi(a, b));
  return (
    <TreeItem itemId={originId} label={<NodeLabel icon={<StorageOutlinedIcon fontSize="small" />} text={group.origin} count={group.count} />}>
      {containers.map(([name, trie]) => {
        const id = encodeNodeId({ type: "container", provider, origin: group.origin, container: name });
        return (
          <TreeItem key={id} itemId={id} label={<NodeLabel icon={<Inventory2OutlinedIcon fontSize="small" />} text={name} count={trie.count} />}>
            {renderTrie(provider, group.origin, name, "", trie)}
          </TreeItem>
        );
      })}
      {renderTrie(provider, group.origin, null, "", group.root)}
    </TreeItem>
  );
}

function ProviderNode({ group }: { group: ProviderGroup }) {
  const meta = PROVIDER_META[group.kind];
  const providerId = encodeNodeId({ type: "provider", provider: group.kind });
  return (
    <TreeItem itemId={providerId} label={<NodeLabel icon={meta.icon} text={meta.label} count={group.count} />}>
      {meta.hasOrigin
        ? group.origins.map((o) => <OriginNode key={encodeNodeId({ type: "origin", provider: group.kind, origin: o.origin })} provider={group.kind} group={o} />)
        // Local files have no origin level: render the filesystem's folder tree directly under the provider.
        : group.origins.flatMap((o) => renderTrie(group.kind, o.origin, null, "", o.root))}
    </TreeItem>
  );
}

/** The expand chain that reveals a file leaf: its provider, origin, container, and every folder down to it. */
function fileAncestorIds(file: FileNode): string[] {
  const provider = file.originKind;
  const ids = [
    encodeNodeId({ type: "sourcesRoot" }),
    encodeNodeId({ type: "provider", provider }),
  ];
  if (PROVIDER_META[provider].hasOrigin) {
    ids.push(encodeNodeId({ type: "origin", provider, origin: file.origin }));
  }
  if (file.container !== null) {
    ids.push(encodeNodeId({ type: "container", provider, origin: file.origin, container: file.container }));
  }
  if (file.path) {
    let acc = "";
    for (const segment of file.path.split("/")) {
      acc = acc ? `${acc}/${segment}` : segment;
      ids.push(encodeNodeId({ type: "folder", provider, origin: file.origin, container: file.container, path: acc }));
    }
  }
  return ids;
}

// ---- Flows branch (repo > repository folder > batch > flow) ----------------------------------------------

interface FlowLeaf { id: string; name: string; kind: string; repoId: string }

/** A folder in a repo's directory structure: its subfolders, the batches of the flows sitting directly in it
 * (batch is the flow's declared YAML attribute), and a recursive flow count. */
interface FlowFolder {
  folders: Map<string, FlowFolder>;
  batches: Map<string, FlowLeaf[]>;
  count: number;
}

const newFlowFolder = (): FlowFolder => ({ folders: new Map(), batches: new Map(), count: 0 });

/** The directory segments of a flow's repo-relative path (the file name dropped): the repository folder
 * structure that organizes the flows. */
function flowFolderSegments(relativePath: string): string[] {
  const normalized = relativePath.replace(/\\/g, "/");
  const slash = normalized.lastIndexOf("/");
  const dir = slash < 0 ? "" : normalized.slice(0, slash);
  return dir.split("/").filter((segment) => segment.length > 0);
}

/** Folds every flow into repo > folder tree > batch > flow, with recursive flow counts at each folder. */
function foldFlows(pipelines: PipelineSummary[]): Map<string, FlowFolder> {
  const repos = new Map<string, FlowFolder>();
  for (const flow of pipelines) {
    let root = repos.get(flow.repoId);
    if (root === undefined) {
      root = newFlowFolder();
      repos.set(flow.repoId, root);
    }
    let folder = root;
    folder.count++;
    for (const segment of flowFolderSegments(flow.relativePath)) {
      let child = folder.folders.get(segment);
      if (child === undefined) {
        child = newFlowFolder();
        folder.folders.set(segment, child);
      }
      folder = child;
      folder.count++;
    }
    const batch = flow.batch ?? "default";
    const list = folder.batches.get(batch) ?? [];
    list.push({ id: flow.id, name: flow.name, kind: flow.kind, repoId: flow.repoId });
    folder.batches.set(batch, list);
  }
  return repos;
}

/** Renders a repo folder's subfolders (recursively) then its batch groups (each with its flow leaves). */
function renderFlowFolder(repoId: string, path: string, folder: FlowFolder): ReactNode[] {
  const folders = [...folder.folders.entries()]
    .sort(([a], [b]) => byNameCi(a, b))
    .map(([name, child]) => {
      const childPath = path ? `${path}/${name}` : name;
      const id = encodeNodeId({ type: "flowFolder", repoId, path: childPath });
      return (
        <TreeItem key={id} itemId={id} label={<NodeLabel icon={<FolderOutlinedIcon fontSize="small" />} text={name} count={child.count} />}>
          {renderFlowFolder(repoId, childPath, child)}
        </TreeItem>
      );
    });
  const batches = [...folder.batches.entries()]
    .sort(([a], [b]) => byNameCi(a, b))
    .map(([batch, flows]) => {
      const id = encodeNodeId({ type: "batch", repoId, path, batch });
      return (
        <TreeItem key={id} itemId={id} label={<NodeLabel icon={<LayersOutlinedIcon fontSize="small" />} text={batch} count={flows.length} />}>
          {[...flows].sort((a, b) => byNameCi(a.name, b.name)).map((flow) => (
            <TreeItem
              key={flow.id}
              itemId={encodeNodeId({ type: "flow", repoId: flow.repoId, pipelineId: flow.id })}
              label={(
                <Stack direction="row" spacing={1} alignItems="center" sx={{ minWidth: 0, py: 0.25 }}>
                  <AccountTreeIcon fontSize="small" sx={{ color: "text.secondary", flexShrink: 0 }} />
                  <Typography variant="body2" noWrap sx={{ minWidth: 0, flexGrow: 1 }}>{flow.name}</Typography>
                  <Chip label={flow.kind} size="small" variant="outlined" sx={{ height: 18, fontSize: 11, flexShrink: 0 }} />
                </Stack>
              )}
            />
          ))}
        </TreeItem>
      );
    });
  return [...folders, ...batches];
}

/** Every expandable id under a repo's folder tree (folders + batches), for revealing matches while filtering. */
function collectFlowIds(repoId: string, path: string, folder: FlowFolder): string[] {
  const ids: string[] = [];
  for (const [name, child] of folder.folders) {
    const childPath = path ? `${path}/${name}` : name;
    ids.push(encodeNodeId({ type: "flowFolder", repoId, path: childPath }), ...collectFlowIds(repoId, childPath, child));
  }
  for (const batch of folder.batches.keys()) {
    ids.push(encodeNodeId({ type: "batch", repoId, path, batch }));
  }
  return ids;
}

// ---- Cross-branch object search --------------------------------------------------------------------------

/** The flat "Object matches" list under the filter box: server-side name search across every object leaf
 * (tables, views, AND files, whose name is their path), so a leaf not yet expanded in the tree is findable. */
function ObjectMatches({ filter, onSelect }: { filter: string; onSelect: (id: string) => void }) {
  const matches = useQuery({
    queryKey: ["catalog-object-matches", filter],
    queryFn: () => lineageApi.objects({ name: filter, pageSize: 20 }),
  });

  if (matches.isPending) {
    return <Skeleton height={32} data-testid="catalog-matches-loading" />;
  }
  if (matches.isError) {
    return <Alert severity="error">{`Object search failed: ${String(matches.error)}`}</Alert>;
  }
  if (matches.data.items.length === 0) {
    return null;
  }
  return (
    <Box data-testid="catalog-object-matches">
      <Typography variant="overline" color="text.secondary">Object matches</Typography>
      <List dense disablePadding>
        {matches.data.items.map((row) => (
          <ListItemButton key={row.key} onClick={() => onSelect(encodeNodeId({ type: "object", objectKey: row.key }))} sx={{ borderRadius: 1 }}>
            <ListItemIcon sx={{ minWidth: 30 }}>{metaForKind(row.kind).icon}</ListItemIcon>
            <ListItemText
              primary={row.name}
              secondary={[row.database, row.schema].filter((part) => part !== null).join(".") || row.serverRef}
              primaryTypographyProps={{ variant: "body2", noWrap: true }}
              secondaryTypographyProps={{ variant: "caption", noWrap: true }}
            />
          </ListItemButton>
        ))}
      </List>
    </Box>
  );
}

/** Local debounce so the server-side object search fires after typing pauses, not per keystroke. */
function useDebounced(value: string, delayMs: number): string {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    const handle = window.setTimeout(() => setDebounced(value), delayMs);
    return () => window.clearTimeout(handle);
  }, [value, delayMs]);
  return debounced;
}

// ---- The tree --------------------------------------------------------------------------------------------

export interface CatalogTreeProps {
  selectedId: string | null;
  onSelect: (id: string) => void;
  /** Ids to start expanded (the deep-linked node's ancestor chain plus the roots). */
  initialExpanded: string[];
}

/**
 * The explorer tree over the catalog, in three perspectives: Databases (the SQL estate: database > schema >
 * kind > object, merged across connection references, files excluded), Sources (the file estate by canonical
 * origin: storage account / SFTP server / filesystem > container > folder > file), and Flows (the pipeline
 * estate: repo > batch > flow). The Databases and Sources skeletons arrive in bounded calls and fold client
 * side; database object leaves and flow leaves load lazily on expand. The filter narrows every branch client
 * side and searches every object leaf server side.
 */
export function CatalogTree({ selectedId, onSelect, initialExpanded }: CatalogTreeProps) {
  const [expanded, setExpanded] = useState<string[]>(initialExpanded);
  const [filter, setFilter] = useState("");
  const debouncedFilter = useDebounced(filter.trim(), 350);
  const needle = lower(debouncedFilter);

  const schemaKinds = useQuery({ queryKey: ["catalog-schema-kinds"], queryFn: () => lineageApi.schemaKinds() });
  const fileTree = useQuery({ queryKey: ["catalog-file-tree"], queryFn: () => lineageApi.fileTree() });
  const repos = useQuery({ queryKey: ["catalog-repos"], queryFn: () => repoApi.list({ pageSize: 200 }) });
  // Every flow, in as few calls as the page size allows: the folder tree needs the whole set to render, and
  // flows are bounded by the estate's file count, not its data volume.
  const pipelines = useQuery({
    queryKey: ["catalog-all-pipelines"],
    queryFn: async () => {
      const items: PipelineSummary[] = [];
      for (let page = 1; ; page++) {
        const result = await pipelineApi.list({ page, pageSize: 500 });
        items.push(...result.items);
        if (result.items.length === 0 || result.page * result.pageSize >= result.total) {
          break;
        }
      }
      return items;
    },
  });

  const databases = useMemo(() => foldDatabases(schemaKinds.data ?? []), [schemaKinds.data]);

  const filteredDatabases = useMemo(() => {
    if (needle === "") {
      return databases;
    }
    const match = (label: string | null) => lower(label ?? UNRESOLVED_LABEL).includes(needle);
    return databases
      .map((database) => {
        if (match(database.database)) {
          return database;
        }
        const kept = database.schemas.filter((schema) => match(schema.schema));
        return kept.length > 0 ? { ...database, schemas: kept } : null;
      })
      .filter((database): database is DatabaseBranch => database !== null);
  }, [databases, needle]);

  // Filter the flat file list (by origin/container/path/name) BEFORE folding, so a matched leaf keeps its
  // whole provider/origin/container/folder chain.
  const fileProviders = useMemo(() => {
    const files = fileTree.data ?? [];
    const kept = needle === ""
      ? files
      : files.filter((f) =>
        lower(f.origin).includes(needle) || lower(f.container ?? "").includes(needle)
        || lower(f.path ?? "").includes(needle) || lower(f.name).includes(needle));
    return foldFileProviders(kept);
  }, [fileTree.data, needle]);

  const repoList: Repo[] = useMemo(() => (repos.data?.items ?? []).slice().sort((a, b) => byNameCi(a.name, b.name)), [repos.data]);

  // Filter the flat flow list (by repo name, folder path, batch, or flow name) BEFORE folding, so a matched
  // flow keeps its whole repo/folder/batch chain.
  const flowsByRepo = useMemo(() => {
    const all = pipelines.data ?? [];
    const repoNameById = new Map((repos.data?.items ?? []).map((r) => [r.id, r.name]));
    const kept = needle === ""
      ? all
      : all.filter((p) =>
        lower(p.name).includes(needle) || lower(p.relativePath).includes(needle)
        || lower(p.batch ?? "default").includes(needle) || lower(repoNameById.get(p.repoId) ?? "").includes(needle));
    return foldFlows(kept);
  }, [pipelines.data, repos.data, needle]);

  // Reveal the selected file leaf (and, while filtering, the matched upper levels) without hand-expanding.
  const effectiveExpanded = useMemo(() => {
    const open = new Set(expanded);
    const selected = selectedId === null ? null : decodeNodeId(selectedId);
    if (selected?.type === "object") {
      const file = (fileTree.data ?? []).find((f) => f.key === selected.objectKey);
      if (file) {
        for (const id of fileAncestorIds(file)) {
          open.add(id);
        }
      }
    }
    if (needle !== "") {
      open.add(encodeNodeId({ type: "databasesRoot" }));
      open.add(encodeNodeId({ type: "sourcesRoot" }));
      open.add(encodeNodeId({ type: "flowsRoot" }));
      for (const database of filteredDatabases) {
        open.add(encodeNodeId({ type: "database", database: database.database }));
      }
      for (const provider of fileProviders) {
        open.add(encodeNodeId({ type: "provider", provider: provider.kind }));
        if (PROVIDER_META[provider.kind].hasOrigin) {
          for (const origin of provider.origins) {
            open.add(encodeNodeId({ type: "origin", provider: provider.kind, origin: origin.origin }));
          }
        }
      }
      for (const [repoId, root] of flowsByRepo) {
        open.add(encodeNodeId({ type: "repo", repoId }));
        for (const id of collectFlowIds(repoId, "", root)) {
          open.add(id);
        }
      }
    }
    return [...open];
  }, [expanded, needle, selectedId, fileTree.data, filteredDatabases, fileProviders, flowsByRepo]);

  const skeletonError = [schemaKinds, fileTree, repos, pipelines].find((query) => query.isError);
  if (skeletonError !== undefined) {
    return (
      <Alert severity="error"
        action={<Button color="inherit" size="small" onClick={() => void skeletonError.refetch()}>Retry</Button>}
        data-testid="catalog-tree-error">
        {`The catalog tree could not load: ${String(skeletonError.error)}`}
      </Alert>
    );
  }
  if (schemaKinds.isPending || fileTree.isPending || repos.isPending || pipelines.isPending) {
    return (
      <Stack spacing={1} data-testid="catalog-tree-loading">
        {Array.from({ length: 8 }, (_, i) => <Skeleton key={i} height={28} />)}
      </Stack>
    );
  }

  const expandedSet = new Set(effectiveExpanded);
  const totalDbObjects = databases.reduce((sum, database) => sum + database.objectCount, 0);
  const totalFiles = (fileTree.data ?? []).length;
  const totalFlows = (pipelines.data ?? []).length;

  return (
    <Stack spacing={1.5} data-testid="catalog-tree">
      <TextField
        size="small"
        placeholder="Filter the tree or search objects"
        value={filter}
        onChange={(event) => setFilter(event.target.value)}
        inputProps={{ "data-testid": "catalog-filter" }}
      />
      {debouncedFilter.length >= 2 && <ObjectMatches filter={debouncedFilter} onSelect={onSelect} />}

      <SimpleTreeView
        expandedItems={effectiveExpanded}
        onExpandedItemsChange={(_event, ids) => setExpanded(ids)}
        selectedItems={selectedId}
        onSelectedItemsChange={(_event, id) => {
          // Pseudo rows (loading, empty, load-more) carry a "#" suffix and are not selectable nodes.
          if (id !== null && !id.includes("#")) {
            onSelect(id);
          }
        }}
        aria-label="Catalog tree"
      >
        {/* Databases */}
        <TreeItem
          itemId={encodeNodeId({ type: "databasesRoot" })}
          label={<NodeLabel icon={<StorageOutlinedIcon fontSize="small" />} text="Databases" count={totalDbObjects} />}
        >
          {filteredDatabases.length === 0 && (
            <TreeItem itemId="dbs#empty" disabled label={<NodeLabel text="No database objects in the catalog yet" />} />
          )}
          {filteredDatabases.map((database) => {
            const databaseId = encodeNodeId({ type: "database", database: database.database });
            return (
              <TreeItem key={databaseId} itemId={databaseId}
                label={<NodeLabel icon={<StorageOutlinedIcon fontSize="small" />} text={database.database ?? UNRESOLVED_LABEL} count={database.objectCount} />}>
                {database.schemas.map((schema) => {
                  const schemaId = encodeNodeId({ type: "schema", database: database.database, schema: schema.schema });
                  return (
                    <TreeItem key={schemaId} itemId={schemaId}
                      label={<NodeLabel icon={<SchemaOutlinedIcon fontSize="small" />} text={schema.schema ?? UNRESOLVED_LABEL} count={schema.objectCount} />}>
                      {schema.kinds.map((kindRow) => {
                        const kindNode: CatalogNode = { type: "kind", database: database.database, schema: schema.schema, kind: kindRow.kind };
                        const kindId = encodeNodeId(kindNode);
                        const meta = metaForKind(kindRow.kind);
                        return (
                          <TreeItem key={kindId} itemId={kindId} label={<NodeLabel icon={meta.icon} text={meta.plural} count={kindRow.objectCount} />}>
                            {expandedSet.has(kindId)
                              ? (
                                <PagedObjectLeaves
                                  parentId={kindId}
                                  queryKey={["catalog-objects", database.database, schema.schema, kindRow.kind]}
                                  fetchPage={(page) => lineageApi.objects({
                                    database: database.database ?? undefined,
                                    schema: schema.schema ?? undefined,
                                    kind: kindRow.kind,
                                    page,
                                    pageSize: LEAF_PAGE_SIZE,
                                  })}
                                />
                              )
                              : <TreeItem itemId={`${kindId}#placeholder`} disabled label={<NodeLabel text="…" />} />}
                          </TreeItem>
                        );
                      })}
                    </TreeItem>
                  );
                })}
              </TreeItem>
            );
          })}
        </TreeItem>

        {/* Sources (file origins, grouped by provider: Azure / Amazon S3 / Google Cloud / SFTP / ... / Local) */}
        <TreeItem
          itemId={encodeNodeId({ type: "sourcesRoot" })}
          label={<NodeLabel icon={<CloudOutlinedIcon fontSize="small" />} text="Sources" count={totalFiles} />}
        >
          {fileProviders.length === 0 && (
            <TreeItem itemId="sources#empty" disabled label={<NodeLabel text="No file sources in the catalog yet" />} />
          )}
          {fileProviders.map((group) => <ProviderNode key={encodeNodeId({ type: "provider", provider: group.kind })} group={group} />)}
        </TreeItem>

        {/* Flows (repo > repository folder > batch > flow) */}
        <TreeItem
          itemId={encodeNodeId({ type: "flowsRoot" })}
          label={<NodeLabel icon={<AccountTreeIcon fontSize="small" />} text="Flows" count={totalFlows} />}
        >
          {flowsByRepo.size === 0 && (
            <TreeItem itemId="flows#empty" disabled label={<NodeLabel text="No flows in the catalog yet" />} />
          )}
          {repoList.map((repo) => {
            const root = flowsByRepo.get(repo.id);
            if (root === undefined) {
              return null;
            }
            const repoId = encodeNodeId({ type: "repo", repoId: repo.id });
            return (
              <TreeItem key={repoId} itemId={repoId}
                label={<NodeLabel icon={<SourceOutlinedIcon fontSize="small" />} text={repo.name} count={root.count} />}>
                {renderFlowFolder(repo.id, "", root)}
              </TreeItem>
            );
          })}
        </TreeItem>
      </SimpleTreeView>
    </Stack>
  );
}
