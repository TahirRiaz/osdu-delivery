import { Suspense, lazy } from "react";
import { Navigate, Route, Routes } from "react-router-dom";
import LinearProgress from "@mui/material/LinearProgress";
import LoginPage from "./auth/LoginPage";
import { RequireAuth, RequireScope } from "./auth/RequireAuth";
import AppShell from "./layout/AppShell";

// Feature pages are lazy so heavy dependencies (Monaco, React Flow, Recharts) load with their page, not at boot.
const DashboardPage = lazy(() => import("./features/dashboard/DashboardPage"));
const RunsPage = lazy(() => import("./features/runs/RunsPage"));
const RunDetailPage = lazy(() => import("./features/runs/RunDetailPage"));
const NodesPage = lazy(() => import("./features/nodes/NodesPage"));
const ReposPage = lazy(() => import("./features/repos/ReposPage"));
const RepoDetailPage = lazy(() => import("./features/repos/RepoDetailPage"));
const PipelinesPage = lazy(() => import("./features/pipelines/PipelinesPage"));
const PipelineDetailPage = lazy(() => import("./features/pipelines/PipelineDetailPage"));
const SchedulesPage = lazy(() => import("./features/schedules/SchedulesPage"));
const RepoSourcesPage = lazy(() => import("./features/repo-sources/RepoSourcesPage"));
const LineagePage = lazy(() => import("./features/lineage/LineagePage"));
const LineageGraphPage = lazy(() => import("./features/lineage/LineageGraphPage"));
const SearchPage = lazy(() => import("./features/search/SearchPage"));
const UsersPage = lazy(() => import("./features/users/UsersPage"));

export default function App() {
  return (
    <Suspense fallback={<LinearProgress />}>
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
          <Route path="/runs/:runId" element={<RunDetailPage />} />
          <Route path="/nodes" element={<NodesPage />} />
          <Route path="/repos" element={<ReposPage />} />
          <Route path="/repos/:repoId" element={<RepoDetailPage />} />
          <Route path="/pipelines" element={<PipelinesPage />} />
          <Route path="/pipelines/:pipelineId" element={<PipelineDetailPage />} />
          <Route path="/schedules" element={<SchedulesPage />} />
          <Route path="/repo-sources" element={<RepoSourcesPage />} />
          <Route path="/lineage" element={<LineagePage />} />
          <Route path="/lineage/graph" element={<LineageGraphPage />} />
          <Route path="/search" element={<SearchPage />} />
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
