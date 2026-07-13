import { useState, type CSSProperties, type ReactNode } from "react";
import KeyboardArrowDownIcon from "@mui/icons-material/KeyboardArrowDown";
import { buildContents, type TreeContents, type TreeNodeContext, type TreeSegment } from "./tree";
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
 * Arbitrary-depth tree grouping over the rows given. Each row yields a path of ancestor nodes (outermost first);
 * the row is a leaf inside the last one. Rows sharing a path prefix nest under the SAME node, so a repo/folder
 * path like ["dwh-test", "flows", "api"] builds a real folder tree (one "flows" node holding "api", "broken", ...)
 * rather than a flat list of full paths. A two-level hierarchy (batch -> step) is just a path of length two. Every
 * node renders as an independently expandable tree row indented one step past its parent, with leaf rows deepest.
 * A node with an empty path is a leaf at the top level. The tree building itself lives in ./tree.
 */
export interface TableTree<T> {
  path: (row: T) => TreeSegment[];
  /** Node content for one interior node; receives the node's whole subtree (for counts/aggregates). */
  renderNode: (node: TreeNodeContext<T>) => ReactNode;
}

export type { TreeSegment, TreeNodeContext } from "./tree";

interface DataTableProps<T> {
  columns: Column<T>[];
  /** The rows to render; `undefined` means loading, which draws skeleton rows. */
  rows: T[] | undefined;
  rowKey: (row: T) => string | number;
  onRowClick?: (row: T) => void;
  /** Which rows respond to clicks (hover + pointer + onRowClick); defaults to all when onRowClick is set. */
  rowClickable?: (row: T) => boolean;
  /** Optional per-row style (a failed statement tints red, say); return undefined for the default styling. */
  rowSx?: (row: T) => CSSProperties | undefined;
  emptyMessage: string;
  tree?: TableTree<T>;
  /** Rendered inside the bordered surface, below the table (the PagedTable pagination lives here). */
  footer?: ReactNode;
  skeletonRows?: number;
  "data-testid"?: string;
}

/** One indentation step per tree depth, in theme spacing units; a leaf sits one step past the deepest node. */
const TREE_INDENT = 3.5;

/**
 * The presentational table shell every list renders through: the bordered surface, the header row, loading
 * skeletons, the shared empty state, optional row-click affordance, and optional arbitrary-depth tree grouping
 * (each node level independently expandable above the leaf rows). PagedTable wraps this with server-side paging
 * and a query; pages holding their own already-fetched rows (the repos list) render it directly, so there is one
 * table code path instead of several hand-rolled shells.
 */
export function DataTable<T>({
  columns, rows, rowKey, onRowClick, rowClickable, rowSx, emptyMessage, tree, footer,
  skeletonRows = 5, "data-testid": testId,
}: DataTableProps<T>) {
  // Collapsed node ids, keyed by each node's full path (e.g. "/repoId/flows/api"), so a node keeps its
  // collapsed state by identity across refetches and a genuinely new node starts expanded. Every tree level
  // shares this one set.
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
        sx={{ ...(canClick ? { cursor: "pointer" } : {}), ...(rowSx?.(row) ?? {}) }}
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

  // An interior tree node (a repo, a folder, a batch, a step): an expandable row whose first cell carries the
  // depth indent, the expander chevron, and the node's summary; the summary spans the remaining columns so
  // aggregates read across.
  const nodeRow = (
    nodeId: string, depth: number, isCollapsed: boolean, nodeTestId: string, content: ReactNode,
  ) => (
    <TableRow
      key={nodeId}
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

  // Walks one node's contents into table rows: each child renders its own tree row (depth 0 is a "group-header-row",
  // deeper levels are "subgroup-header-row", so a two-level batch/step tree keeps its familiar test hooks) and, when
  // expanded, recurses into its subtree; a node's direct-leaf rows render after its child nodes, folder-explorer
  // style. The node id is its full path, so a node keeps its collapsed state by identity across refetches.
  const renderContents = (contents: TreeContents<T>, depth: number, idPrefix: string, keys: string[]): ReactNode[] => {
    const out: ReactNode[] = [];
    for (const node of contents.children) {
      const nodeId = `${idPrefix}/${node.key}`;
      const nodeKeys = [...keys, node.key];
      const isCollapsed = collapsed.has(nodeId);
      out.push(nodeRow(
        nodeId, depth, isCollapsed,
        depth === 0 ? "group-header-row" : "subgroup-header-row",
        tree!.renderNode({ keys: nodeKeys, depth, rows: node.rows }),
      ));
      if (!isCollapsed) {
        out.push(...renderContents({ children: node.children, leaves: node.leaves }, depth + 1, nodeId, nodeKeys));
      }
    }

    for (const row of contents.leaves) {
      out.push(dataRow(row, depth));
    }

    return out;
  };

  const treeBody = (items: T[], spec: TableTree<T>) =>
    renderContents(buildContents(items.map((row) => ({ row, segs: spec.path(row) })), 0), 0, "", []);

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
            {rows !== undefined && (tree
              ? treeBody(rows, tree)
              : rows.map((row) => dataRow(row, 0)))}
          </TableBody>
        </Table>
      </TableContainer>
      {footer}
    </Paper>
  );
}
