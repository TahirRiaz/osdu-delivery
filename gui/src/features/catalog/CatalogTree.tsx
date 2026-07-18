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
import DnsOutlinedIcon from "@mui/icons-material/DnsOutlined";
import FolderOutlinedIcon from "@mui/icons-material/FolderOutlined";
import Inventory2OutlinedIcon from "@mui/icons-material/Inventory2Outlined";
import SchemaOutlinedIcon from "@mui/icons-material/SchemaOutlined";
import StorageOutlinedIcon from "@mui/icons-material/StorageOutlined";
import { SimpleTreeView } from "@mui/x-tree-view/SimpleTreeView";
import { TreeItem } from "@mui/x-tree-view/TreeItem";
import { lineageApi, pipelineApi, repoApi } from "../../api/endpoints";
import type { LineageObject, LineageSchema, PipelineBatch, PipelineSummary, SchemaKindCount } from "../../api/types";
import { compareKinds, metaForKind } from "./kindMeta";
import { encodeNodeId, UNRESOLVED_LABEL, type CatalogNode } from "./nodeIds";

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

/** The skeleton hierarchy folded from the flat /lineage/schemas rows. */
interface ServerBranch {
  serverRef: string;
  objectCount: number;
  databases: DatabaseBranch[];
}

interface DatabaseBranch {
  database: string | null;
  objectCount: number;
  schemas: SchemaBranch[];
}

interface SchemaBranch {
  schema: string | null;
  objectCount: number;
}

function foldSchemas(rows: LineageSchema[]): ServerBranch[] {
  const servers = new Map<string, ServerBranch>();
  for (const row of rows) {
    let server = servers.get(row.serverRef);
    if (server === undefined) {
      server = { serverRef: row.serverRef, objectCount: 0, databases: [] };
      servers.set(row.serverRef, server);
    }
    server.objectCount += row.objectCount;
    let database = server.databases.find((d) => d.database === row.database);
    if (database === undefined) {
      database = { database: row.database, objectCount: 0, schemas: [] };
      server.databases.push(database);
    }
    database.objectCount += row.objectCount;
    database.schemas.push({ schema: row.schema, objectCount: row.objectCount });
  }
  // The API returns the rows ordered, so insertion order already reads top-down within each level.
  return [...servers.values()];
}

/** The object leaves of one (server, database, schema, kind) folder: paged, with a load-more row.
 * Selection is handled by the enclosing tree, so a leaf only renders its label. */
function ObjectLeaves({ node }: { node: Extract<CatalogNode, { type: "kind" }> }) {
  const parentId = encodeNodeId(node);
  const objects = useInfiniteQuery({
    queryKey: ["catalog-objects", node.serverRef, node.database, node.schema, node.kind],
    queryFn: ({ pageParam }) => lineageApi.objects({
      serverRef: node.serverRef,
      // A null database/schema is a real grouping value ("(unresolved)") the filter cannot express, so those
      // leaves list unfiltered within the kind; the counts still match because unresolved rows are rare and
      // grouped apart. An empty string filter would be dropped by the query builder, so send only real values.
      database: node.database ?? undefined,
      schema: node.schema ?? undefined,
      kind: node.kind,
      page: pageParam,
      pageSize: LEAF_PAGE_SIZE,
    }),
    initialPageParam: 1,
    getNextPageParam: (last) => (last.page * last.pageSize < last.total ? last.page + 1 : undefined),
  });

  if (objects.isPending) {
    return <TreeItem itemId={`${parentId}#loading`} disabled label={<NodeLabel text="Loading…" />} />;
  }
  if (objects.isError) {
    return (
      <TreeItem
        itemId={`${parentId}#error`}
        disabled
        label={<NodeLabel text={`Could not load objects: ${String(objects.error)}`} />}
      />
    );
  }

  const pages = objects.data.pages;
  const rows: LineageObject[] = pages.flatMap((page) => page.items);
  const total = pages[0]?.total ?? 0;
  const remaining = total - rows.length;
  const meta = metaForKind(node.kind);
  return (
    <>
      {rows.length === 0 && (
        <TreeItem itemId={`${parentId}#empty`} disabled label={<NodeLabel text="No objects" />} />
      )}
      {rows.map((row) => (
        <TreeItem
          key={row.key}
          itemId={encodeNodeId({ type: "object", objectKey: row.key })}
          label={<NodeLabel icon={meta.icon} text={row.name} />}
        />
      ))}
      {remaining > 0 && (
        <TreeItem
          itemId={`${parentId}#more`}
          label={(
            <Button
              size="small"
              disabled={objects.isFetchingNextPage}
              onClick={(event) => {
                event.stopPropagation();
                void objects.fetchNextPage();
              }}
              startIcon={objects.isFetchingNextPage ? <CircularProgress size={14} /> : undefined}
            >
              {`Load more (${remaining} remaining)`}
            </Button>
          )}
        />
      )}
    </>
  );
}

/** The flow leaves of one (repo, batch) folder: paged, with a load-more row. */
function FlowLeaves({ repoId, batch }: { repoId: string; batch: string }) {
  const parentId = encodeNodeId({ type: "batch", repoId, batch });
  const flows = useInfiniteQuery({
    queryKey: ["catalog-flows", repoId, batch],
    queryFn: ({ pageParam }) => pipelineApi.list({ repoId, batch, page: pageParam, pageSize: LEAF_PAGE_SIZE }),
    initialPageParam: 1,
    getNextPageParam: (last) => (last.page * last.pageSize < last.total ? last.page + 1 : undefined),
  });

  if (flows.isPending) {
    return <TreeItem itemId={`${parentId}#loading`} disabled label={<NodeLabel text="Loading…" />} />;
  }
  if (flows.isError) {
    return (
      <TreeItem
        itemId={`${parentId}#error`}
        disabled
        label={<NodeLabel text={`Could not load flows: ${String(flows.error)}`} />}
      />
    );
  }

  const pages = flows.data.pages;
  const rows: PipelineSummary[] = pages.flatMap((page) => page.items);
  const total = pages[0]?.total ?? 0;
  const remaining = total - rows.length;
  return (
    <>
      {rows.length === 0 && (
        <TreeItem itemId={`${parentId}#empty`} disabled label={<NodeLabel text="No flows" />} />
      )}
      {rows.map((row) => (
        <TreeItem
          key={row.id}
          itemId={encodeNodeId({ type: "flow", repoId, pipelineId: row.id })}
          label={(
            <Stack direction="row" spacing={1} alignItems="center" sx={{ minWidth: 0, py: 0.25 }}>
              <AccountTreeIcon fontSize="small" sx={{ color: "text.secondary", flexShrink: 0 }} />
              <Typography variant="body2" noWrap sx={{ minWidth: 0, flexGrow: 1 }}>{row.name}</Typography>
              <Chip label={row.kind} size="small" variant="outlined" sx={{ height: 18, fontSize: 11, flexShrink: 0 }} />
            </Stack>
          )}
        />
      ))}
      {remaining > 0 && (
        <TreeItem
          itemId={`${parentId}#more`}
          label={(
            <Button
              size="small"
              disabled={flows.isFetchingNextPage}
              onClick={(event) => {
                event.stopPropagation();
                void flows.fetchNextPage();
              }}
              startIcon={flows.isFetchingNextPage ? <CircularProgress size={14} /> : undefined}
            >
              {`Load more (${remaining} remaining)`}
            </Button>
          )}
        />
      )}
    </>
  );
}

/** The flat "Object matches" list under the filter box: server-side name search across every leaf, so an
 * object whose page is not yet loaded in the tree is still findable. */
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
          <ListItemButton
            key={row.key}
            onClick={() => onSelect(encodeNodeId({ type: "object", objectKey: row.key }))}
            sx={{ borderRadius: 1 }}
          >
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

export interface CatalogTreeProps {
  selectedId: string | null;
  onSelect: (id: string) => void;
  /** Ids to start expanded (the deep-linked node's ancestor chain plus the roots). */
  initialExpanded: string[];
}

/**
 * The explorer tree over the whole catalog: an Objects branch (server, database, schema, then kind folders
 * with counts, then the paged object leaves) and a Flows branch (repo, then batch, then the paged flow
 * leaves). The skeleton levels arrive in four bounded calls; only leaf pages load lazily on expand. The
 * filter narrows the loaded skeleton client-side and searches every object leaf server-side.
 */
export function CatalogTree({ selectedId, onSelect, initialExpanded }: CatalogTreeProps) {
  const [expanded, setExpanded] = useState<string[]>(initialExpanded);
  const [filter, setFilter] = useState("");
  const debouncedFilter = useDebounced(filter.trim(), 350);

  const schemas = useQuery({ queryKey: ["catalog-schemas"], queryFn: () => lineageApi.schemas() });
  const schemaKinds = useQuery({ queryKey: ["catalog-schema-kinds"], queryFn: () => lineageApi.schemaKinds() });
  const repos = useQuery({ queryKey: ["catalog-repos"], queryFn: () => repoApi.list({ pageSize: 200 }) });
  const batches = useQuery({ queryKey: ["catalog-batches"], queryFn: () => pipelineApi.batches() });

  const servers = useMemo(() => foldSchemas(schemas.data ?? []), [schemas.data]);

  // Kind folders per schema, keyed by the schema node id so lookup during render is O(1).
  const kindsBySchema = useMemo(() => {
    const map = new Map<string, SchemaKindCount[]>();
    for (const row of schemaKinds.data ?? []) {
      const id = encodeNodeId({
        type: "schema", serverRef: row.serverRef, database: row.database, schema: row.schema,
      });
      const list = map.get(id);
      if (list === undefined) {
        map.set(id, [row]);
      } else {
        list.push(row);
      }
    }
    for (const list of map.values()) {
      list.sort((a, b) => compareKinds(a.kind, b.kind));
    }
    return map;
  }, [schemaKinds.data]);

  const batchesByRepo = useMemo(() => {
    const map = new Map<string, PipelineBatch[]>();
    for (const row of batches.data ?? []) {
      const list = map.get(row.repoId);
      if (list === undefined) {
        map.set(row.repoId, [row]);
      } else {
        list.push(row);
      }
    }
    return map;
  }, [batches.data]);

  // Client-side skeleton filter: keep a schema when the server/database/schema label matches; keep a
  // server/database when any retained descendant remains. Repos/batches filter by their labels the same way.
  const filteredServers = useMemo(() => {
    if (debouncedFilter === "") {
      return servers;
    }
    const match = (label: string | null) =>
      (label ?? UNRESOLVED_LABEL).toLowerCase().includes(debouncedFilter.toLowerCase());
    return servers
      .map((server) => {
        if (match(server.serverRef)) {
          return server;
        }
        const databases = server.databases
          .map((database) => {
            if (match(database.database)) {
              return database;
            }
            const kept = database.schemas.filter((schema) => match(schema.schema));
            return kept.length > 0 ? { ...database, schemas: kept } : null;
          })
          .filter((database): database is DatabaseBranch => database !== null);
        return databases.length > 0 ? { ...server, databases } : null;
      })
      .filter((server): server is ServerBranch => server !== null);
  }, [servers, debouncedFilter]);

  const filteredRepos = useMemo(() => {
    const rows = repos.data?.items ?? [];
    if (debouncedFilter === "") {
      return rows;
    }
    const match = (label: string) => label.toLowerCase().includes(debouncedFilter.toLowerCase());
    return rows.filter((repo) =>
      match(repo.name) || (batchesByRepo.get(repo.id) ?? []).some((batch) => match(batch.batch)));
  }, [repos.data, batchesByRepo, debouncedFilter]);

  // While filtering, force the retained upper levels open so matches are visible without hand-expanding.
  const effectiveExpanded = useMemo(() => {
    if (debouncedFilter === "") {
      return expanded;
    }
    const open = new Set(expanded);
    open.add(encodeNodeId({ type: "objectsRoot" }));
    open.add(encodeNodeId({ type: "flowsRoot" }));
    for (const server of filteredServers) {
      open.add(encodeNodeId({ type: "server", serverRef: server.serverRef }));
      for (const database of server.databases) {
        open.add(encodeNodeId({ type: "database", serverRef: server.serverRef, database: database.database }));
      }
    }
    for (const repo of filteredRepos) {
      open.add(encodeNodeId({ type: "repo", repoId: repo.id }));
    }
    return [...open];
  }, [expanded, debouncedFilter, filteredServers, filteredRepos]);

  const skeletonError = [schemas, schemaKinds, repos, batches].find((query) => query.isError);
  if (skeletonError !== undefined) {
    return (
      <Alert
        severity="error"
        action={<Button color="inherit" size="small" onClick={() => void skeletonError.refetch()}>Retry</Button>}
        data-testid="catalog-tree-error"
      >
        {`The catalog tree could not load: ${String(skeletonError.error)}`}
      </Alert>
    );
  }
  if (schemas.isPending || schemaKinds.isPending || repos.isPending || batches.isPending) {
    return (
      <Stack spacing={1} data-testid="catalog-tree-loading">
        {Array.from({ length: 8 }, (_, i) => <Skeleton key={i} height={28} />)}
      </Stack>
    );
  }

  const expandedSet = new Set(effectiveExpanded);

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
        <TreeItem
          itemId={encodeNodeId({ type: "objectsRoot" })}
          label={(
            <NodeLabel
              icon={<Inventory2OutlinedIcon fontSize="small" />}
              text="Objects"
              count={servers.reduce((sum, server) => sum + server.objectCount, 0)}
            />
          )}
        >
          {filteredServers.length === 0 && (
            <TreeItem itemId="objects#empty" disabled label={<NodeLabel text="No objects in the catalog yet" />} />
          )}
          {filteredServers.map((server) => {
            const serverId = encodeNodeId({ type: "server", serverRef: server.serverRef });
            return (
              <TreeItem
                key={serverId}
                itemId={serverId}
                label={(
                  <NodeLabel
                    icon={<DnsOutlinedIcon fontSize="small" />}
                    text={server.serverRef}
                    count={server.objectCount}
                  />
                )}
              >
                {server.databases.map((database) => {
                  const databaseId = encodeNodeId({
                    type: "database", serverRef: server.serverRef, database: database.database,
                  });
                  return (
                    <TreeItem
                      key={databaseId}
                      itemId={databaseId}
                      label={(
                        <NodeLabel
                          icon={<StorageOutlinedIcon fontSize="small" />}
                          text={database.database ?? UNRESOLVED_LABEL}
                          count={database.objectCount}
                        />
                      )}
                    >
                      {database.schemas.map((schema) => {
                        const schemaNode: CatalogNode = {
                          type: "schema",
                          serverRef: server.serverRef,
                          database: database.database,
                          schema: schema.schema,
                        };
                        const schemaId = encodeNodeId(schemaNode);
                        const kinds = kindsBySchema.get(schemaId) ?? [];
                        return (
                          <TreeItem
                            key={schemaId}
                            itemId={schemaId}
                            label={(
                              <NodeLabel
                                icon={<SchemaOutlinedIcon fontSize="small" />}
                                text={schema.schema ?? UNRESOLVED_LABEL}
                                count={schema.objectCount}
                              />
                            )}
                          >
                            {kinds.map((kindRow) => {
                              const kindNode: CatalogNode = {
                                type: "kind",
                                serverRef: server.serverRef,
                                database: database.database,
                                schema: schema.schema,
                                kind: kindRow.kind,
                              };
                              const kindId = encodeNodeId(kindNode);
                              const meta = metaForKind(kindRow.kind);
                              return (
                                <TreeItem
                                  key={kindId}
                                  itemId={kindId}
                                  label={<NodeLabel icon={meta.icon} text={meta.plural} count={kindRow.objectCount} />}
                                >
                                  {expandedSet.has(kindId)
                                    ? <ObjectLeaves node={kindNode} />
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
            );
          })}
        </TreeItem>

        <TreeItem
          itemId={encodeNodeId({ type: "flowsRoot" })}
          label={(
            <NodeLabel
              icon={<AccountTreeIcon fontSize="small" />}
              text="Flows"
              count={(batches.data ?? []).reduce((sum, batch) => sum + batch.flowCount, 0)}
            />
          )}
        >
          {filteredRepos.length === 0 && (
            <TreeItem itemId="flows#empty" disabled label={<NodeLabel text="No repos in the catalog yet" />} />
          )}
          {filteredRepos.map((repo) => {
            const repoId = encodeNodeId({ type: "repo", repoId: repo.id });
            const repoBatches = batchesByRepo.get(repo.id) ?? [];
            return (
              <TreeItem
                key={repoId}
                itemId={repoId}
                label={(
                  <NodeLabel
                    icon={<FolderOutlinedIcon fontSize="small" />}
                    text={repo.name}
                    count={repoBatches.reduce((sum, batch) => sum + batch.flowCount, 0)}
                  />
                )}
              >
                {repoBatches.length === 0 && (
                  <TreeItem itemId={`${repoId}#empty`} disabled label={<NodeLabel text="No flows" />} />
                )}
                {repoBatches.map((batch) => {
                  const batchId = encodeNodeId({ type: "batch", repoId: repo.id, batch: batch.batch });
                  return (
                    <TreeItem
                      key={batchId}
                      itemId={batchId}
                      label={(
                        <NodeLabel
                          icon={<FolderOutlinedIcon fontSize="small" />}
                          text={batch.batch}
                          count={batch.flowCount}
                        />
                      )}
                    >
                      {expandedSet.has(batchId)
                        ? <FlowLeaves repoId={repo.id} batch={batch.batch} />
                        : <TreeItem itemId={`${batchId}#placeholder`} disabled label={<NodeLabel text="…" />} />}
                    </TreeItem>
                  );
                })}
              </TreeItem>
            );
          })}
        </TreeItem>
      </SimpleTreeView>
    </Stack>
  );
}
