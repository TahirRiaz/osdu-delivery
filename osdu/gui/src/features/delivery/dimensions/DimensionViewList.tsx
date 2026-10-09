import { Link, useNavigate } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { Info } from "lucide-react";
import { DataTable, type Column } from "@/components/DataTable";
import { RichTooltip } from "@/components/RichTooltip";
import { deliveryApi, type DeliveryDimensionView } from "../../../api/delivery";
import { counted } from "../assertions/assertionFormat";
import { ProblemView } from "../TemplateSheet";
import { ViewCheckMark, ViewStandingMark } from "./DimensionViewBadges";
import { DimensionViewRemoveButton } from "./DimensionViewRemoveButton";
import { DimensionViewSuggestButton } from "./DimensionViewSuggest";
import { viewHref, viewObject } from "./dimensionViewFormat";

/** How often the list is read again, so a build under way shows what it wrote as it lands. */
const REFRESH_MS = 15000;

/** What a view is, as hovering the section's heading says it. */
const VIEWS_PURPOSE = "A view puts dimensions of the flow side by side at the grain of one of them, so a pipeline, a report or a person reads one table instead of writing the joins. Each build of the flow writes every view it declares as osdu.dimv_<name>, after its dimensions, and checks it in the run's partition: its rows, what each join found, and the values a conversion could not read.";

/** A view's joins in a line: each joined dimension, by its alias where the view gives it one. */
function joinsText(view: DeliveryDimensionView): string {
  return view.joins.map((join) => (join.alias === join.to ? join.to : `${join.alias} (${join.to})`)).join(", ");
}

/** A view's joins one a line, as hovering them says: the alias, the dimension, and the column joined on. */
function joinsDetail(view: DeliveryDimensionView): string {
  return view.joins.map((join) => `${join.alias}: ${join.to} on ${join.on}`).join("\n");
}

/**
 * The views a dimension flow declares, on its Dimensions tab: each view's name and the object it is in the database, the
 * dimension whose rows are its rows, what it joins, where it stands (written as declared, changed, dropped, not written
 * yet, or no longer declared) and what its newest check found. A row opens the view's page (its name is the link, for a
 * new tab); a view no active flow declares any more can be removed there or here by an admin. Suggest joins offers the
 * joins a new view could make.
 */
export function DimensionViewsSection({ pipelineId, dimensions }: {
  pipelineId: string;
  /** The dimensions the flow declares, which Suggest joins offers as a view's rows. */
  dimensions: readonly string[];
}) {
  const navigate = useNavigate();
  const views = useQuery({
    queryKey: ["delivery", "dimensions", "views", "flow", pipelineId],
    queryFn: () => deliveryApi.dimensionFlowViews(pipelineId),
    refetchInterval: REFRESH_MS,
  });

  const count = views.data?.length ?? 0;
  const undeclared = views.data?.filter((view) => !view.declared).length ?? 0;
  const columns: Column<DeliveryDimensionView>[] = [
    {
      // The object in the database beside the name where the section is wide enough, on hover where it is not: the joins
      // are the one column that gives up width, so a narrow panel keeps every column in view.
      id: "view",
      header: "View",
      render: (row) => (
        <span className="flex items-baseline gap-2">
          <RichTooltip title="In the database" body={viewObject(row)} mono>
            <Link
              to={viewHref(row.name)}
              onClick={(event) => event.stopPropagation()}
              className="font-medium hover:underline"
              data-testid={`dimension-view-open-${row.name}`}
            >
              {row.name}
            </Link>
          </RichTooltip>
          <span className="hidden font-mono text-[11.5px] text-muted-foreground @4xl/views:inline">{viewObject(row)}</span>
        </span>
      ),
    },
    { id: "from", header: "From", render: (row) => <span className="font-mono text-[12px]">{row.from}</span> },
    {
      id: "joins",
      header: "Joins",
      fill: true,
      floor: 100,
      render: (row) => (row.joins.length === 0
        ? <span className="text-[12px] text-muted-foreground">none</span>
        : (
          <RichTooltip title="Joins" body={joinsDetail(row)} mono>
            <span className="block truncate font-mono text-[12px]">{joinsText(row)}</span>
          </RichTooltip>
        )),
    },
    { id: "state", header: "State", render: (row) => <ViewStandingMark view={row} testId={`dimension-view-state-${row.name}`} /> },
    { id: "check", header: "Last check", render: (row) => <ViewCheckMark check={row.lastCheck} testId={`dimension-view-check-${row.name}`} /> },
  ];
  if (undeclared > 0) {
    columns.push({
      id: "actions",
      header: "",
      align: "right",
      render: (row) => (
        <span className="flex items-center justify-end" onClick={(event) => event.stopPropagation()}>
          <DimensionViewRemoveButton view={row} iconOnly />
        </span>
      ),
    });
  }

  return (
    <section className="@container/views flex flex-col gap-2" data-testid="dimension-views">
      <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
        <h3 className="text-[13px] font-semibold">Views</h3>
        <RichTooltip title="Views" body={VIEWS_PURPOSE}>
          <Info className="size-3.5 shrink-0 text-muted-foreground" aria-label="What a view is" />
        </RichTooltip>
        {views.data !== undefined && (
          <span className="text-[12px] text-muted-foreground" data-testid="dimension-views-count">
            {count === 0 ? "The flow declares none." : `${counted(count - undeclared, "view")} declared${undeclared > 0 ? `, ${undeclared} no longer` : ""}`}
          </span>
        )}
        <span className="ml-auto">
          <DimensionViewSuggestButton pipelineId={pipelineId} dimensions={dimensions} />
        </span>
      </div>
      {views.isError
        ? <ProblemView error={views.error} testId="dimension-views-error" />
        : (views.data === undefined || count > 0) && (
          <DataTable
            columns={columns}
            rows={views.data}
            rowKey={(row) => row.name}
            onRowClick={(row) => navigate(viewHref(row.name))}
            emptyMessage="The flow declares no view."
            skeletonRows={2}
            data-testid="dimension-views-table"
          />
        )}
    </section>
  );
}
