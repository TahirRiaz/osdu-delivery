import { useState, type ReactNode } from "react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import Box from "@mui/material/Box";
import Paper from "@mui/material/Paper";
import Skeleton from "@mui/material/Skeleton";
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
  "data-testid"?: string;
}

/**
 * The single list-page code path: every collection page renders through this. Server-side offset paging against
 * the API's PagedResult, loading skeletons, the shared error rendering, and optional polling for live views.
 */
export function PagedTable<T>({
  queryKey, fetchPage, columns, rowKey, onRowClick, pollMs, emptyMessage, "data-testid": testId,
}: PagedTableProps<T>) {
  const [page, setPage] = useState(0); // MUI pagination is 0-based; the API is 1-based
  const [pageSize, setPageSize] = useState(50);

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
            {result?.items.map((row) => (
              <TableRow
                key={rowKey(row)}
                hover={Boolean(onRowClick)}
                onClick={onRowClick ? () => onRowClick(row) : undefined}
                sx={onRowClick ? { cursor: "pointer" } : undefined}
                data-testid="table-row"
              >
                {columns.map((column) => (
                  <TableCell key={column.id} align={column.align}>{column.render(row)}</TableCell>
                ))}
              </TableRow>
            ))}
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
