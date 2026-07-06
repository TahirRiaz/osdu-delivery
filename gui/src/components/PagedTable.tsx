import { keepPreviousData, useQuery } from "@tanstack/react-query";
import TablePagination from "@mui/material/TablePagination";
import Typography from "@mui/material/Typography";
import { useState } from "react";
import type { PagedResult } from "../api/types";
import { isApiError } from "../api/client";
import { pollingInterval } from "../hooks/usePolling";
import { CorrelationError } from "./CorrelationError";
import { DataTable, type Column, type TableGrouping } from "./DataTable";

// Re-exported so the pages keep importing the table types from here (their single table entry point).
export type { Column, TableGrouping } from "./DataTable";

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

/**
 * The single paged list-page code path: server-side offset paging against the API's PagedResult on top of the
 * shared DataTable shell (loading skeletons, the shared error and empty rendering, optional tree grouping),
 * with optional polling for live views. Pages that already hold their rows render DataTable directly instead.
 */
export function PagedTable<T>({
  queryKey, fetchPage, columns, rowKey, onRowClick, pollMs, emptyMessage, grouping, "data-testid": testId,
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
    <DataTable
      columns={columns}
      rows={result?.items}
      rowKey={rowKey}
      onRowClick={onRowClick}
      emptyMessage={emptyMessage}
      grouping={grouping}
      data-testid={testId ?? "paged-table"}
      footer={(
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
      )}
    />
  );
}
