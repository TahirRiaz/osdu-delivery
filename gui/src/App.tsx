import { Suspense } from "react";
import { Navigate, Route, Routes } from "react-router-dom";
import LoginPage from "./auth/LoginPage";
import { TopProgressBar } from "./components/TopProgressBar";
import { RequireAuth, RequireScope } from "./auth/RequireAuth";
import AppShell from "./layout/AppShell";
import { lazyRoute } from "./lib/lazyRoute";

// Feature pages are lazy so heavy dependencies (Monaco, Recharts) load with their page, not at boot.
const DashboardPage = lazyRoute("DashboardPage", () => import("./features/dashboard/DashboardPage"));
const RunsPage = lazyRoute("RunsPage", () => import("./features/runs/RunsPage"));
const RunDetailPage = lazyRoute("RunDetailPage", () => import("./features/runs/RunDetailPage"));
const DeliveryOverviewPage = lazyRoute("DeliveryOverviewPage", () => import("./features/delivery/DeliveryOverviewPage"));
const DeliveryRecordPage = lazyRoute("DeliveryRecordPage", () => import("./features/delivery/DeliveryRecordPage"));
const DeliverySubmissionPage = lazyRoute("DeliverySubmissionPage", () => import("./features/delivery/DeliverySubmissionPage"));
const DeliveryActivityPage = lazyRoute("DeliveryActivityPage", () => import("./features/delivery/DeliveryActivityPage"));
const DeliveryDocumentsPage = lazyRoute("DeliveryDocumentsPage", () => import("./features/delivery/DeliveryDocumentsPage"));
const DeliveryCachePage = lazyRoute("DeliveryCachePage", () => import("./features/delivery/DeliveryCachePage"));
const ManualSubmissionPage = lazyRoute("ManualSubmissionPage", () => import("./features/delivery/ManualSubmissionPage"));
const DropOffPage = lazyRoute("DropOffPage", () => import("./features/delivery/DropOffPage"));
const RunGroupPage = lazyRoute("RunGroupPage", () => import("./features/runs/RunGroupPage"));
const NodesPage = lazyRoute("NodesPage", () => import("./features/nodes/NodesPage"));
const ReposPage = lazyRoute("ReposPage", () => import("./features/repos/ReposPage"));
const RepoDetailPage = lazyRoute("RepoDetailPage", () => import("./features/repos/RepoDetailPage"));
const PipelinesPage = lazyRoute("PipelinesPage", () => import("./features/pipelines/PipelinesPage"));
const PipelineDetailPage = lazyRoute("PipelineDetailPage", () => import("./features/pipelines/PipelineDetailPage"));
const SchedulesPage = lazyRoute("SchedulesPage", () => import("./features/schedules/SchedulesPage"));
const ScheduleTimelinePage = lazyRoute("ScheduleTimelinePage", () => import("./features/schedules/ScheduleTimelinePage"));
const SearchPage = lazyRoute("SearchPage", () => import("./features/search/SearchPage"));
const UsersPage = lazyRoute("UsersPage", () => import("./features/users/UsersPage"));
const AccessTokensPage = lazyRoute("AccessTokensPage", () => import("./features/tokens/AccessTokensPage"));
const NotificationsPage = lazyRoute("NotificationsPage", () => import("./features/notifications/NotificationsPage"));
const MaintenancePage = lazyRoute("MaintenancePage", () => import("./features/maintenance/MaintenancePage"));

export default function App() {
  return (
    <Suspense fallback={<TopProgressBar />}>
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route
          element={(
            <RequireAuth>
              <AppShell />
            </RequireAuth>
          )}
        >
          <Route path="/" element={<DashboardPage />} />
          <Route path="/runs" element={<RunsPage />} />
          <Route path="/runs/groups/:groupId" element={<RunGroupPage />} />
          <Route path="/runs/:runId" element={<RunDetailPage />} />
          <Route path="/delivery" element={<DeliveryOverviewPage />} />
          <Route path="/delivery/submit" element={<ManualSubmissionPage />} />
          <Route path="/delivery/dropoff" element={<DropOffPage />} />
          <Route path="/delivery/records/:key" element={<DeliveryRecordPage />} />
          <Route path="/delivery/submissions/:submissionId" element={<DeliverySubmissionPage />} />
          <Route path="/delivery/activity" element={<DeliveryActivityPage />} />
          <Route path="/delivery/documents" element={<DeliveryDocumentsPage />} />
          <Route path="/delivery/cache" element={<DeliveryCachePage />} />
          <Route path="/nodes" element={<NodesPage />} />
          <Route path="/repos" element={<ReposPage />} />
          <Route path="/repos/:repoId" element={<RepoDetailPage />} />
          <Route path="/pipelines" element={<PipelinesPage />} />
          <Route path="/pipelines/:pipelineId" element={<PipelineDetailPage />} />
          {/* Timeline route sits above the table route so the sidebar's Timeline entry resolves; both live under
              /schedules so the Schedules nav group stays highlighted. */}
          <Route path="/schedules/timeline" element={<ScheduleTimelinePage />} />
          <Route path="/schedules" element={<SchedulesPage />} />
          {/* Repo sources merged into the Repos page; keep the old path working for bookmarks. */}
          <Route path="/repo-sources" element={<Navigate to="/repos" replace />} />
          <Route path="/search" element={<SearchPage />} />
          {/* Self-service and available to any authenticated user; no scope guard beyond RequireAuth. */}
          <Route path="/settings/tokens" element={<AccessTokensPage />} />
          <Route path="/settings/notifications" element={<NotificationsPage />} />
          <Route path="/settings/maintenance" element={<MaintenancePage />} />
          <Route
            path="/users"
            element={(
              <RequireScope scope="admin">
                <UsersPage />
              </RequireScope>
            )}
          />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Route>
      </Routes>
    </Suspense>
  );
}
