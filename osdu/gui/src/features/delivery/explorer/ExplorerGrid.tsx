import { useLayoutEffect, useRef, useState, type KeyboardEvent, type ReactNode, type UIEvent } from "react";
import { cn } from "@/lib/utils";

/** The height of one row; fixed, so the rows in view are worked out from the scroll position alone. */
const ROW = 34;

/** Rows drawn above and below what is in view, so a quick scroll does not show blank space. */
const OVERSCAN = 12;

/** How near the end of the rows, in rows, the grid asks for the next page. */
const NEAR_END = 30;

/** A column of the grid: what it is called, how much of the width it takes, and what a row shows in it. */
export interface GridColumn<T> {
  id: string;
  header: ReactNode;
  /** A share of what the fixed columns leave (`flex`), or a width in pixels (`width`). */
  flex?: number;
  width?: number;
  align?: "left" | "right";
  render: (row: T, index: number) => ReactNode;
}

/**
 * A list of records as a grid that holds ten thousand rows as lightly as a hundred: rows of one height, only those in view
 * (and a margin around them) drawn, the column headers fixed above them, every column holding its share of the width so
 * the grid never scrolls sideways. A row opens on a click or on Enter; the arrow keys, Page Up and Down, Home and End move
 * through the rows, and the row the keys are on is kept in view. Scrolling near the end asks for the next page.
 */
export function ExplorerGrid<T>({ rows, columns, rowKey, onOpen, onNearEnd, footer, label, testId }: {
  rows: T[];
  columns: GridColumn<T>[];
  rowKey: (row: T) => string;
  onOpen: (row: T) => void;
  /** Called when the rows are scrolled near their end; the caller reads the next page when there is one. */
  onNearEnd?: () => void;
  /** What stands under the last row: the next page loading, or why there is no more. */
  footer?: ReactNode;
  label: string;
  testId: string;
}) {
  const scroller = useRef<HTMLDivElement>(null);
  const [view, setView] = useState({ top: 0, height: 600 });
  const [cursor, setCursor] = useState<number | null>(null);

  // The height in view, followed as the panel is resized; measured before paint so the first rows drawn are the right ones.
  useLayoutEffect(() => {
    const element = scroller.current;
    if (element === null) {
      return undefined;
    }

    const measure = () => setView((was) => (was.height === element.clientHeight ? was : { top: element.scrollTop, height: element.clientHeight }));
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  const first = Math.max(0, Math.floor(view.top / ROW) - OVERSCAN);
  const last = Math.min(rows.length, Math.ceil((view.top + view.height) / ROW) + OVERSCAN);
  const scrolled = (event: UIEvent<HTMLDivElement>) => {
    const element = event.currentTarget;
    setView({ top: element.scrollTop, height: element.clientHeight });
    if (onNearEnd !== undefined && element.scrollTop + element.clientHeight > rows.length * ROW - NEAR_END * ROW) {
      onNearEnd();
    }
  };

  const moveTo = (index: number) => {
    const to = Math.max(0, Math.min(rows.length - 1, index));
    setCursor(to);
    const element = scroller.current;
    if (element !== null) {
      // The header takes the first row's height, so a row is in view below it.
      const top = to * ROW;
      if (top < element.scrollTop) {
        element.scrollTop = top;
      } else if (top + 2 * ROW > element.scrollTop + element.clientHeight) {
        element.scrollTop = top + 2 * ROW - element.clientHeight;
      }
    }
  };

  const keyed = (event: KeyboardEvent<HTMLDivElement>) => {
    if (rows.length === 0) {
      return;
    }

    const page = Math.max(1, Math.floor(view.height / ROW) - 2);
    const at = cursor ?? -1;
    const moves: Record<string, number> = {
      ArrowDown: at + 1,
      ArrowUp: at - 1,
      PageDown: at + page,
      PageUp: at - page,
      Home: 0,
      End: rows.length - 1,
    };
    if (event.key in moves) {
      event.preventDefault();
      moveTo(moves[event.key]);
    } else if (event.key === "Enter" && cursor !== null && rows[cursor] !== undefined) {
      event.preventDefault();
      onOpen(rows[cursor]);
    }
  };

  const template = columns.map((column) => (column.width !== undefined ? `${column.width}px` : `minmax(0, ${column.flex ?? 1}fr)`)).join(" ");
  return (
    <div
      ref={scroller}
      role="grid"
      aria-label={label}
      aria-rowcount={rows.length + 1}
      tabIndex={0}
      onScroll={scrolled}
      onKeyDown={keyed}
      className="relative min-h-0 flex-1 overflow-y-auto overflow-x-hidden outline-none focus-visible:ring-2 focus-visible:ring-ring/50 focus-visible:ring-inset"
      data-testid={testId}
    >
      <div
        role="row"
        aria-rowindex={1}
        className="sticky top-0 z-10 grid items-center gap-3 border-b bg-card px-3 text-[11px] font-medium uppercase tracking-wide text-muted-foreground"
        style={{ gridTemplateColumns: template, height: ROW }}
      >
        {columns.map((column) => (
          <div key={column.id} role="columnheader" className={cn("min-w-0 truncate", column.align === "right" && "text-right")}>{column.header}</div>
        ))}
      </div>
      <div className="relative" style={{ height: rows.length * ROW }}>
        {rows.slice(first, last).map((row, offset) => {
          const index = first + offset;
          return (
            <div
              key={rowKey(row)}
              role="row"
              aria-rowindex={index + 2}
              aria-selected={cursor === index}
              onClick={() => { setCursor(index); onOpen(row); }}
              className={cn(
                "group/row absolute inset-x-0 grid cursor-pointer items-center gap-3 border-b border-border/60 px-3 text-[13px] hover:bg-accent/50",
                cursor === index && "bg-accent/70 shadow-[inset_2px_0_0_var(--primary)]",
              )}
              style={{ top: index * ROW, height: ROW, gridTemplateColumns: template }}
              data-testid={`${testId}-row`}
            >
              {columns.map((column) => (
                <div key={column.id} role="gridcell" className={cn("flex min-w-0 items-center", column.align === "right" && "justify-end")}>
                  {column.render(row, index)}
                </div>
              ))}
            </div>
          );
        })}
      </div>
      {footer}
    </div>
  );
}
