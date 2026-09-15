import {
  DatabaseZap, FileCode2, FileJson, GitCompare, LayoutTemplate, PackageCheck, PencilRuler, ScrollText, ShieldCheck, Workflow,
} from "lucide-react";
import type { RunSummary } from "@/api/types";
import type { Column } from "@/components/DataTable";
import { TruncatedText } from "@/components/TruncatedText";
import { lazyRoute } from "@/lib/lazyRoute";
import type { FlowKindContribution, GuiModule, RunDetailContribution } from "@/modules/registry";
import { Deferred } from "./features/delivery/Deferred";
import { CacheRunActions, DeliveryRunActions, DeliveryRunCounts, DeliveryRunMeta } from "./features/delivery/DeliveryRunHeader";
import { DeliveryTriggerFields } from "./features/delivery/DeliveryTriggerFields";

// The OSDU Delivery module: its pages, its navigation, the panels of the delivery, retrieval and cache kinds on SQLFlow's
// pipeline, run and trigger pages, the delivery records in search, and the product's branding. Everything heavy (the
// pages, the panels, anything with the code editor) loads with the surface that shows it.

const DeliveryOverviewPage = lazyRoute("DeliveryOverviewPage", () => import("./features/delivery/DeliveryOverviewPage"));
const ManualSubmissionPage = lazyRoute("ManualSubmissionPage", () => import("./features/delivery/ManualSubmissionPage"));
const DeliveryRecordPage = lazyRoute("DeliveryRecordPage", () => import("./features/delivery/DeliveryRecordPage"));
const DeliverySubmissionPage = lazyRoute("DeliverySubmissionPage", () => import("./features/delivery/DeliverySubmissionPage"));
const DeliveryActivityPage = lazyRoute("DeliveryActivityPage", () => import("./features/delivery/DeliveryActivityPage"));
const DeliveryDocumentsPage = lazyRoute("DeliveryDocumentsPage", () => import("./features/delivery/DeliveryDocumentsPage"));
const TemplatesPage = lazyRoute("TemplatesPage", () => import("./features/delivery/TemplatesPage"));
const MappingBuilderPage = lazyRoute("MappingBuilderPage", () => import("./features/delivery/MappingBuilderPage"));
const DeliveryCachePage = lazyRoute("DeliveryCachePage", () => import("./features/delivery/DeliveryCachePage"));

const DeliveryFlowPanel = lazyRoute(
  "DeliveryFlowPanel",
  () => import("./features/delivery/DeliveryFlowPanel").then((loaded) => ({ default: loaded.DeliveryFlowPanel })),
);
const RetrievalFlowPanel = lazyRoute(
  "RetrievalFlowPanel",
  () => import("./features/delivery/RetrievalFlowPanel").then((loaded) => ({ default: loaded.RetrievalFlowPanel })),
);
const DeliveryCacheFlowVersions = lazyRoute(
  "DeliveryCacheFlowVersions",
  () => import("./features/delivery/DeliveryCacheFlowVersions").then((loaded) => ({ default: loaded.DeliveryCacheFlowVersions })),
);
const DeliveryRunCard = lazyRoute("DeliveryRunCard", () => import("./features/delivery/DeliveryRunCard"));
const RecordSearchHits = lazyRoute(
  "RecordSearchHits",
  () => import("./features/delivery/RecordSearchHits").then((loaded) => ({ default: loaded.RecordSearchHits })),
);

/** SQLFlow's pipeline tabs that describe its own flows' data (view columns, processed files), not these kinds. */
const HIDDEN_PIPELINE_TABS = ["transforms", "files"];

/** SQLFlow's run tabs that describe its own runs' work (files, SQL statements, keys, assertions, health metrics). */
const HIDDEN_RUN_TABS = ["files", "statements", "surrogate-keys", "assertions", "health-metrics"];

const runColumns: Column<RunSummary>[] = [
  { id: "operation", header: "Operation", render: (row) => <span className="font-mono text-[12px]">{row.operation ?? "-"}</span> },
  { id: "requested-by", header: "Requested by", render: (row) => <TruncatedText text={row.requestedBy} mono maxWidth={200} /> },
];

function runPanels(extra: Omit<RunDetailContribution, "card" | "hiddenTabs">): RunDetailContribution {
  return {
    ...extra,
    card: (run) => <Deferred placeholder={false}><DeliveryRunCard run={run} /></Deferred>,
    hiddenTabs: HIDDEN_RUN_TABS,
  };
}

const deliveryKind: FlowKindContribution = {
  kind: "delivery",
  pipelineTabs: [
    {
      value: "delivery",
      label: "Delivery",
      testId: "pipeline-tab-delivery",
      render: (pipeline) => (
        <Deferred><DeliveryFlowPanel pipelineId={pipeline.id} flowName={pipeline.name} section="overview" /></Deferred>
      ),
    },
    {
      value: "records",
      label: "Records",
      testId: "pipeline-tab-records",
      render: (pipeline) => (
        <Deferred><DeliveryFlowPanel pipelineId={pipeline.id} flowName={pipeline.name} section="records" /></Deferred>
      ),
    },
    {
      value: "submissions",
      label: "Submissions",
      testId: "pipeline-tab-submissions",
      render: (pipeline) => (
        <Deferred><DeliveryFlowPanel pipelineId={pipeline.id} flowName={pipeline.name} section="submissions" /></Deferred>
      ),
    },
  ],
  defaultPipelineTab: "delivery",
  hiddenPipelineTabs: HIDDEN_PIPELINE_TABS,
  runColumns,
  run: runPanels({
    headerActions: (run) => <DeliveryRunActions run={run} />,
    headerMeta: (run) => <DeliveryRunMeta run={run} />,
    headerDetails: (run) => <DeliveryRunCounts run={run} />,
  }),
  trigger: { Fields: DeliveryTriggerFields },
};

const retrievalKind: FlowKindContribution = {
  kind: "retrieval",
  pipelineTabs: [
    {
      value: "retrievals",
      label: "Retrievals",
      testId: "pipeline-tab-retrievals",
      render: (pipeline) => <Deferred><RetrievalFlowPanel pipelineId={pipeline.id} /></Deferred>,
    },
  ],
  defaultPipelineTab: "retrievals",
  hiddenPipelineTabs: HIDDEN_PIPELINE_TABS,
  runColumns,
  run: runPanels({}),
  trigger: { Fields: DeliveryTriggerFields },
};

const cacheKind: FlowKindContribution = {
  kind: "cache",
  pipelineTabs: [
    {
      value: "versions",
      label: "Cache versions",
      testId: "pipeline-tab-versions",
      render: (pipeline) => <Deferred><DeliveryCacheFlowVersions flowName={pipeline.name} /></Deferred>,
    },
  ],
  defaultPipelineTab: "versions",
  hiddenPipelineTabs: HIDDEN_PIPELINE_TABS,
  runColumns,
  run: runPanels({
    headerActions: (run) => <CacheRunActions run={run} />,
  }),
  trigger: { Fields: DeliveryTriggerFields },
};

export const osduDeliveryModule: GuiModule = {
  id: "osdu-delivery",
  routes: [
    { path: "/delivery", component: DeliveryOverviewPage },
    { path: "/delivery/submit", component: ManualSubmissionPage },
    { path: "/delivery/records/:key", component: DeliveryRecordPage },
    { path: "/delivery/submissions/:submissionId", component: DeliverySubmissionPage },
    { path: "/delivery/activity", component: DeliveryActivityPage },
    { path: "/delivery/documents", component: DeliveryDocumentsPage },
    { path: "/delivery/templates", component: TemplatesPage },
    { path: "/delivery/mappings/build", component: MappingBuilderPage },
    { path: "/delivery/cache", component: DeliveryCachePage },
  ],
  navItems: [
    { group: "operate", label: "Delivery", to: "/delivery", icon: PackageCheck, testId: "nav-delivery", after: "/" },
    { group: "operate", label: "Manual submission", to: "/delivery/submit", icon: FileJson, testId: "nav-delivery-submit", after: "/delivery" },
    { group: "operate", label: "Audit trail", to: "/delivery/activity", icon: ScrollText, testId: "nav-delivery-activity", after: "/runs" },
    { group: "workspace", label: "Mappings", to: "/delivery/documents", icon: FileCode2, testId: "nav-delivery-documents", after: "/pipelines" },
    { group: "workspace", label: "Templates", to: "/delivery/templates", icon: LayoutTemplate, testId: "nav-delivery-templates", after: "/delivery/documents" },
    { group: "workspace", label: "Mapping builder", to: "/delivery/mappings/build", icon: PencilRuler, testId: "nav-delivery-mapping-builder", after: "/delivery/templates" },
    { group: "workspace", label: "OSDU cache", to: "/delivery/cache", icon: DatabaseZap, testId: "nav-delivery-cache", after: "/delivery/mappings/build" },
  ],
  detailTitles: [
    { pattern: /^\/delivery\/records\/[^/]+/, title: () => "Record" },
    { pattern: /^\/delivery\/submissions\/([^/]+)/, title: (match) => `Submission ${match[1].slice(0, 8)}` },
  ],
  kinds: [deliveryKind, retrievalKind, cacheKind],
  searchCategories: [
    {
      key: "records",
      render: ({ category }) => <Deferred placeholder={false}><RecordSearchHits category={category} /></Deferred>,
    },
  ],
  branding: {
    productName: "OSDU Delivery",
    attribution: "powered by SQLFlow",
    documentTitle: "OSDU Delivery",
    login: {
      headline: "Deliver to OSDU with confidence.",
      tagline: "Publish subsurface records into OSDU, send only what changed, and keep every record traceable from a single control plane.",
      capabilities: [
        {
          icon: Workflow,
          title: "Flows and mappings as code",
          body: "Flow documents and the mappings they pin live as YAML in your repository, versioned and reviewed like the rest of your codebase.",
        },
        {
          icon: ScrollText,
          title: "Every record accounted for",
          body: "Which source row it came from, every attempt and its outcome, the OSDU id and version it landed as, and who asked for each change.",
        },
        {
          icon: GitCompare,
          title: "Only what changed",
          body: "Rendered documents and their payloads are hashed independently, so an unchanged record is skipped and a changed one is re-sent on its own.",
        },
        {
          icon: ShieldCheck,
          title: "Governed execution",
          body: "Managed identity, secrets resolved from the vault by reference, and an audited history of every run and intervention.",
        },
      ],
    },
  },
};
