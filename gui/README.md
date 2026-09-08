# OSDU Delivery GUI

The control plane GUI: a React + TypeScript + Vite single-page app over the control plane REST API
(`/api/v1`). It is an observe-and-operate surface for a YAML-first product: flows are authored in git and
synced into the catalog; the GUI shows them read-only (Monaco), triggers and monitors runs with their live
trace, watches the compute fleet, and manages schedules, repo sources, users, tokens, and notifications.

## Sign-in

Three methods, all ending in the same bearer token the control plane issues:

- Regular users: username + password (`POST /auth/login`).
- Azure single sign-on: "Sign in with Microsoft" (MSAL popup, then `POST /auth/exchange`). Shown when the
  control plane has `ControlPlane:AzureAd` enabled; first sign-in provisions the user with the viewer role.
- Bootstrap secret (break-glass, behind "Advanced" on the login page). Use it once to provision real users.

The token lives in memory + sessionStorage by default: a reload keeps the session, closing the tab ends it.
"Keep me signed in on this device" moves it to localStorage and renews it on every open.

## Configuration

The API base URL resolves in this order:

- Production build: `public/config.json` (`{"apiBaseUrl": "https://controlplane.example.com"}`), swapped per
  environment at deploy time; falls back to the build-time `VITE_API_BASE_URL`.
- Dev server: the `VITE_API_BASE_URL` environment variable wins (see `.env.development`), so a shell or the
  e2e harness can point the GUI anywhere; falls back to `config.json`.

The control plane must allow this origin in `ControlPlane:Cors:AllowedOrigins`
(for development: `["http://localhost:5173"]`).

## Development

```bash
npm ci
npm run dev        # http://localhost:5173
npm run typecheck
npm run build      # tsc + vite build into dist/
```

Run the control plane alongside (from the repo root):

```bash
ASPNETCORE_URLS=http://localhost:5000 \
SQLFLOW_CATALOG_DB="Server=localhost;Database=OsduDelivery;Trusted_Connection=True;TrustServerCertificate=True" \
ControlPlane__Jwt__SigningKey=<32+ byte secret> \
ControlPlane__Jwt__BootstrapSecret=<32+ byte secret> \
ControlPlane__Cors__AllowedOrigins__0=http://localhost:5173 \
dotnet run --project src/SqlFlow.ControlPlane
```

Bootstrap provisioning applies catalog migrations, seeds the roles, and (when
`ControlPlane:Bootstrap:AdminUsername` / `AdminPasswordReference` are set) creates the first admin. To watch
compute node mode, start a worker: `sqlflow worker --db "<catalog connection>"`; it appears on the Nodes page
within a heartbeat.

## Layout

- `src/api/`: the typed client (`client.ts`), one function per endpoint (`endpoints.ts`), and the DTO mirror
  (`types.ts`). Keep `types.ts` in step with `src/SqlFlow.ControlPlane/Api`.
- `src/features/<area>/`: one folder per page family (dashboard, runs, nodes, repos, pipelines, schedules,
  search, users, tokens, notifications, maintenance, activity).
- `src/layout/`: the VS Code-style workbench (activity bar, side bar, tabs, bottom panel, command palette).
- `src/components/`: shared building blocks; `src/components/ui/` are the shadcn primitives.
- `DESIGN.md` is the binding design reference for every screen.

## End-to-end tests (Playwright)

```bash
npx playwright install chromium   # once
npm run e2e
```

The Playwright config spins up everything itself: the control plane (against a dedicated local test catalog
database, with bootstrap provisioning creating the e2e admin) and the Vite dev server, then exercises the GUI
element by element. See `playwright.config.ts` and `e2e/`. The specs that seed and run a flow need the
delivery flow kind, so they pass only once it is registered in the control plane.

## Branding

`src/theme/branding.css` holds the design tokens, and `public/brand/` the logo files. Brand changes happen in
those two places.

## Delivery pages

`src/features/delivery` holds the delivery domain's pages over `src/api/delivery.ts`: the overview (`/delivery`),
the per-flow Delivery, Records and Submissions tabs on a delivery pipeline's page, the record page
(`/delivery/records/:key`) with its history and interventions, the submission page, the audit trail
(`/delivery/activity`), and the mappings and snapshots page (`/delivery/documents`). Target-side actions (probe,
read back, delete) queue a compute task and poll it through `useComputeTask`.
