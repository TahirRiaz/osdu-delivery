import { Fragment, useState, type ReactNode } from "react";
import KeyboardArrowDownIcon from "@mui/icons-material/KeyboardArrowDown";
import Paper from "@mui/material/Paper";
import Skeleton from "@mui/material/Skeleton";
import Stack from "@mui/material/Stack";
import Table from "@mui/material/Table";
import TableBody from "@mui/material/TableBody";
import TableCell from "@mui/material/TableCell";
import TableContainer from "@mui/material/TableContainer";
import TableHead from "@mui/material/TableHead";
import TableRow from "@mui/material/TableRow";
import { EmptyState } from "./EmptyState";

export interface Column<T> {
  id: string;
  header: string;
  render: (row: T) => ReactNode;
  align?: "left" | "right" | "center";
  width?: number | string;
}

/**
 * Tree grouping over the rows given: CONTIGUOUS rows sharing the same group key nest under one expandable
 * tree node (so a time-ordered list reconstructs each joint execution without re-sorting), and an optional
 * sub key nests a second, independently expandable level beneath it; rows within a group are stably sorted by
 * the sub key (ascending). Each level renders as a tree row: indented one step past its parent, with its own
 * expander, leaf rows deepest, mirroring the batch report's batch -> step -> run hierarchy.
 */
export interface TableGrouping<T> {
  groupKey: (row: T) => string;
  /** Node content for one group; receives every row of the group (for aggregates). */
  renderGroupHeader: (rows: T[]) => ReactNode;
  subKey?: (row: T) => number | string;
  /** Node content for one sub-group; required when subKey is set. */
  renderSubHeader?: (rows: T[]) => ReactNode;
}

interface DataTableProps<T> {
  columns: Column<T>[];
  /** The rows to render; `undefined` means loading, which draws skeleton rows. */
  rows: T[] | undefined;
  rowKey: (row: T) => string | number;
  onRowClick?: (row: T) => void;
  /** Which rows respond to clicks (hover + pointer + onRowClick); defaults to all when onRowClick is set. */
  rowClickable?: (row: T) => boolean;
  emptyMessage: string;
  grouping?: TableGrouping<T>;
  /** Rendered inside the bordered surface, below the table (the PagedTable pagination lives here). */
  footer?: ReactNode;
  skeletonRows?: number;
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
 * The presentational table shell every list renders through: the bordered surface, the header row, loading
 * skeletons, the shared empty state, optional row-click affordance, and optional tree grouping (up to two
 * independently expandable node levels above the leaf rows). PagedTable wraps this with server-side paging and
 * a query; pages holding their own already-fetched rows (the repos list) render it directly, so there is one
 * table code path instead of several hand-rolled shells.
 */
export function DataTable<T>({
  columns, rows, rowKey, onRowClick, rowClickable, emptyMessage, grouping, footer,
  skeletonRows = 5, "data-testid": testId,
}: DataTableProps<T>) {
  // Collapsed node ids: node key + first row key, so the state survives refreshes of the same data (a
  // genuinely new node gets a new id and starts expanded). Both tree levels share this one set.
  const [collapsed, setCollapsed] = useState<ReadonlySet<string>>(new Set());

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

  const clickable = (row: T) => onRowClick !== undefined && (rowClickable?.(row) ?? true);

  // A leaf (data) row: the actual record. Its first cell is indented to `depth` so it nests visibly under its
  // parent node in the tree; the remaining cells align to the grid columns like any flat row.
  const dataRow = (row: T, depth: number) => {
    const canClick = clickable(row);
    return (
      <TableRow
        key={rowKey(row)}
        hover={canClick}
        onClick={canClick ? () => onRowClick!(row) : undefined}
        sx={canClick ? { cursor: "pointer" } : undefined}
        data-testid="table-row"
      >
        {columns.map((column, i) => (
          <TableCell
            key={column.id}
            align={column.align}
            // Only grouped leaves (depth > 0) indent under their node; a flat row keeps the default cell
            // padding so its first column lines up with the header (a depth of 0 must not force pl to 0).
            sx={i === 0 && depth > 0 ? { pl: TREE_INDENT * depth } : undefined}
          >
            {column.render(row)}
          </TableCell>
        ))}
      </TableRow>
    );
  };

  // An interior tree node (batch or step): an expandable row whose first cell carries the depth indent, the
  // expander chevron, and the node's summary; the summary spans the remaining columns so aggregates read across.
  const nodeRow = (
    nodeId: string, depth: number, isCollapsed: boolean, nodeTestId: string, content: ReactNode,
  ) => (
    <TableRow
      hover
      onClick={() => toggleNode(nodeId)}
      sx={{ cursor: "pointer" }}
      data-testid={nodeTestId}
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

  return (
    <Paper variant="outlined" data-testid={testId}>
      <TableContainer>
        <Table size="small">
          <TableHead>
            <TableRow>
              {columns.map((column) => (
                <TableCell key={column.id} align={column.align} sx={{ width: column.width }}>
                  {column.header}
                </TableCell>
              ))}
            </TableRow>
          </TableHead>
          <TableBody>
            {rows === undefined && Array.from({ length: skeletonRows }, (_, i) => (
              <TableRow key={`skeleton-${i}`}>
                {columns.map((column) => (
                  <TableCell key={column.id}><Skeleton /></TableCell>
                ))}
              </TableRow>
            ))}
            {rows !== undefined && rows.length === 0 && (
              <TableRow>
                <TableCell colSpan={columns.length} sx={{ border: 0 }}>
                  <EmptyState title={emptyMessage} data-testid="empty-message" />
                </TableCell>
              </TableRow>
            )}
            {rows !== undefined && (grouping
              ? groupedBody(rows, grouping)
              : rows.map((row) => dataRow(row, 0)))}
          </TableBody>
        </Table>
      </TableContainer>
      {footer}
    </Paper>
  );
}
