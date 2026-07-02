import { Fragment, useState, type ReactNode } from "react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import KeyboardArrowDownIcon from "@mui/icons-material/KeyboardArrowDown";
import Box from "@mui/material/Box";
import Paper from "@mui/material/Paper";
import Skeleton from "@mui/material/Skeleton";
import Stack from "@mui/material/Stack";
import Table from "@mui/material/Table";
import TableBody from "@mui/material/TableBody";
import TableCell from "@mui/material/TableCell";
import TableContainer from "@mui/material/TableContainer";
import TableHead from "@mui/material/TableHead";
import TablePagination from "@mui/material/TablePagination";
import TableRow from "@mui/material/TableRow";
import Typography from "@mui/material/Typography";
import type { PagedResult } from "../api/types";
import { isApiError } from "../api/client";
import { pollingInterval } from "../hooks/usePolling";
import { CorrelationError } from "./CorrelationError";

export interface Column<T> {
  id: string;
  header: string;
  render: (row: T) => ReactNode;
  align?: "left" | "right" | "center";
  width?: number | string;
}

/**
 * Tree grouping over the current page: CONTIGUOUS rows sharing the same group key nest under one expandable
 * tree node (so a time-ordered list reconstructs each joint execution without re-sorting the page), and an
 * optional sub key nests a second, independently expandable level beneath it; rows within a group are stably
 * sorted by the sub key (ascending). Each level renders as a tree row: indented one step past its parent, with
 * its own expander, leaf rows deepest, mirroring the batch report's batch -> step -> run hierarchy.
 */
export interface TableGrouping<T> {
  groupKey: (row: T) => string;
  /** Node content for one group; receives every row of the group (for aggregates). */
  renderGroupHeader: (rows: T[]) => ReactNode;
  subKey?: (row: T) => number | string;
  /** Node content for one sub-group; required when subKey is set. */
  renderSubHeader?: (rows: T[]) => ReactNode;
}

interface PagedTableProps<T> {
  /** Cache identity for the query; include every filter value so a filter change is a new query. */
  queryKey: readonly unknown[];
  fetchPage: (page: number, pageSize: number) => Promise<PagedResult<T>>;
  columns: Column<T>[];
  rowKey: (row: T) => string | number;
  onRowClick?: (row: T) => void;
  /** Poll cadence for live views (runs, nodes); omit for on-demand pages. */
  pollMs?: number;
  emptyMessage: string;
  grouping?: TableGrouping<T>;
  "data-testid"?: string;
}

interface Cluster<T> {
  key: string;
  rows: T[];
}

/** Splits rows into clusters of CONTIGUOUS equal keys, preserving row order. */
function clusterContiguous<T>(rows: T[], key: (row: T) => string): Cluster<T>[] {
  const clusters: Cluster<T>[] = [];
  for (const row of rows) {
    const k = key(row);
    const last = clusters.at(-1);
    if (last !== undefined && last.key === k) {
      last.rows.push(row);
    } else {
      clusters.push({ key: k, rows: [row] });
    }
  }

  return clusters;
}

/** Stable-sorts a cluster's rows by the sub key (ascending) and splits them into one cluster per distinct key. */
function subClusters<T>(rows: T[], subKey: (row: T) => number | string): Cluster<T>[] {
  const sorted = rows
    .map((row, index) => ({ row, index, key: subKey(row) }))
    .sort((a, b) => (a.key < b.key ? -1 : a.key > b.key ? 1 : a.index - b.index))
    .map((entry) => entry.row);
  return clusterContiguous(sorted, (row) => String(subKey(row)));
}

/** One indentation step per tree depth, in theme spacing units; a leaf sits one step past the deepest node. */
const TREE_INDENT = 3.5;

/**
 * The single list-page code path: every collection page renders through this. Server-side offset paging against
 * the API's PagedResult, loading skeletons, the shared error rendering, optional polling for live views, and
 * optional tree grouping (up to two independently expandable node levels above the leaf rows) for report-style
 * lists.
 */
export function PagedTable<T>({
  queryKey, fetchPage, columns, rowKey, onRowClick, pollMs, emptyMessage, grouping, "data-testid": testId,
}: PagedTableProps<T>) {
  const [page, setPage] = useState(0); // MUI pagination is 0-based; the API is 1-based
  const [pageSize, setPageSize] = useState(50);
  // Collapsed node ids: node key + first row key, so the state survives polling refreshes of the same data
  // (a genuinely new node gets a new id and starts expanded). Both tree levels share this one set.
  const [collapsed, setCollapsed] = useState<ReadonlySet<string>>(new Set());

  const query = useQuery({
    queryKey: [...queryKey, page, pageSize],
    queryFn: () => fetchPage(page + 1, pageSize),
    placeholderData: keepPreviousData,
    refetchInterval: pollMs ? pollingInterval(pollMs) : undefined,
  });

  if (query.isError) {
    return isApiError(query.error)
      ? <CorrelationError error={query.error} />
      : <Typography color="error">{String(query.error)}</Typography>;
  }

  const toggleNode = (nodeId: string) => {
    setCollapsed((current) => {
      const next = new Set(current);
      if (next.has(nodeId)) {
        next.delete(nodeId);
      } else {
        next.add(nodeId);
      }

      return next;
    });
  };

  // A leaf (data) row: the actual record. Its first cell is indented to `depth` so it nests visibly under its
  // parent node in the tree; the remaining cells align to the grid columns like any flat row.
  const dataRow = (row: T, depth: number) => (
    <TableRow
      key={rowKey(row)}
      hover={Boolean(onRowClick)}
      onClick={onRowClick ? () => onRowClick(row) : undefined}
      sx={onRowClick ? { cursor: "pointer" } : undefined}
      data-testid="table-row"
    >
      {columns.map((column, i) => (
        <TableCell
          key={column.id}
          align={column.align}
          sx={i === 0 ? { pl: TREE_INDENT * depth } : undefined}
        >
          {column.render(row)}
        </TableCell>
      ))}
    </TableRow>
  );

  // An interior tree node (batch or step): an expandable row whose first cell carries the depth indent, the
  // expander chevron, and the node's summary; the summary spans the remaining columns so aggregates read across.
  const nodeRow = (
    nodeId: string, depth: number, isCollapsed: boolean, testId2: string, content: ReactNode,
  ) => (
    <TableRow
      hover
      onClick={() => toggleNode(nodeId)}
      sx={{ cursor: "pointer" }}
      data-testid={testId2}
      aria-expanded={!isCollapsed}
    >
      <TableCell colSpan={columns.length} sx={{ py: 0.25 }}>
        <Stack direction="row" spacing={0.5} alignItems="center" sx={{ pl: TREE_INDENT * depth }}>
          <KeyboardArrowDownIcon
            fontSize="small"
            sx={{
              color: "text.secondary",
              transition: "transform 120ms",
              transform: isCollapsed ? "rotate(-90deg)" : "none",
            }}
          />
          {content}
        </Stack>
      </TableCell>
    </TableRow>
  );

  const groupedBody = (items: T[], group: TableGrouping<T>) =>
    clusterContiguous(items, group.groupKey).map((cluster) => {
      const clusterId = `${cluster.key}::${String(rowKey(cluster.rows[0]))}`;
      const isCollapsed = collapsed.has(clusterId);
      const hasSub = group.subKey !== undefined;
      return (
        <Fragment key={clusterId}>
          {nodeRow(clusterId, 0, isCollapsed, "group-header-row", group.renderGroupHeader(cluster.rows))}
          {!isCollapsed && (hasSub
            ? subClusters(cluster.rows, group.subKey!).map((sub) => {
              const subId = `${clusterId}::${sub.key}`;
              const subCollapsed = collapsed.has(subId);
              return (
                <Fragment key={subId}>
                  {nodeRow(
                    subId, 1, subCollapsed, "subgroup-header-row",
                    group.renderSubHeader?.(sub.rows) ?? sub.key,
                  )}
                  {!subCollapsed && sub.rows.map((row) => dataRow(row, 2))}
                </Fragment>
              );
            })
            : cluster.rows.map((row) => dataRow(row, 1)))}
        </Fragment>
      );
    });

  const result = query.data;
  return (
    <Paper variant="outlined" data-testid={testId ?? "paged-table"}>
      <TableContainer>
        <Table size="small">
          <TableHead>
            <TableRow>
              {columns.map((column) => (
                <TableCell key={column.id} align={column.align} sx={{ width: column.width, fontWeight: 600 }}>
                  {column.header}
                </TableCell>
              ))}
            </TableRow>
          </TableHead>
          <TableBody>
            {result === undefined && Array.from({ length: 5 }, (_, i) => (
              <TableRow key={`skeleton-${i}`}>
                {columns.map((column) => (
                  <TableCell key={column.id}><Skeleton /></TableCell>
                ))}
              </TableRow>
            ))}
            {result !== undefined && result.items.length === 0 && (
              <TableRow>
                <TableCell colSpan={columns.length}>
                  <Box py={4} textAlign="center">
                    <Typography color="text.secondary" data-testid="empty-message">{emptyMessage}</Typography>
                  </Box>
                </TableCell>
              </TableRow>
            )}
            {result !== undefined && (grouping
              ? groupedBody(result.items, grouping)
              : result.items.map(dataRow))}
          </TableBody>
        </Table>
      </TableContainer>
      <TablePagination
        component="div"
        count={result?.total ?? 0}
        page={page}
        onPageChange={(_, next) => setPage(next)}
        rowsPerPage={pageSize}
        onRowsPerPageChange={(event) => {
          setPageSize(Number.parseInt(event.target.value, 10));
          setPage(0);
        }}
        rowsPerPageOptions={[25, 50, 100, 200]}
      />
    </Paper>
  );
}
