# SQLFlow GUI

The control plane GUI: a React + TypeScript + Vite single-page app over the control plane REST API
(`/api/v1`). It is an observe-and-operate surface for a YAML-first product: pipelines are authored in git and
synced into the shadow catalog; the GUI shows them read-only (Monaco), triggers and monitors runs, watches the
compute fleet, and manages schedules, repo sources, and users.

## Sign-in

Three methods, all ending in the same SQLFlow-issued bearer token:

- Regular SQLFlow users: username + password (`POST /auth/login`).
- Azure single sign-on: "Sign in with Microsoft" (MSAL popup, then `POST /auth/exchange`). Shown when the
  control plane has `ControlPlane:AzureAd` enabled; first sign-in provisions the user with the viewer role.
- Bootstrap secret (break-glass, behind "Advanced" on the login page). Use it once to provision real users.

The token lives in memory + sessionStorage: a reload keeps the session, closing the tab ends it.

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
npm install
npm run dev        # http://localhost:5173
npm run typecheck
npm run build      # tsc + vite build into dist/
```

Run the control plane alongside (from the repo root):

```bash
ASPNETCORE_URLS=http://localhost:5000 \
SQLFLOW_CATALOG_DB="Server=localhost;Database=SqlFlowCatalog;Trusted_Connection=True;TrustServerCertificate=True" \
ControlPlane__Jwt__SigningKey=<32+ byte secret> \
ControlPlane__Jwt__BootstrapSecret=<32+ byte secret> \
ControlPlane__Cors__AllowedOrigins__0=http://localhost:5173 \
dotnet run --project src/SqlFlow.ControlPlane
```

Bootstrap provisioning applies catalog migrations, seeds the roles, and (when
`ControlPlane:Bootstrap:AdminUsername` / `AdminPasswordReference` are set) creates the first admin. To watch
compute node mode, start a worker: `sqlflow worker --db "<catalog connection>"`; it appears on the Nodes page
within a heartbeat.

## End-to-end tests (Playwright)

```bash
npx playwright install chromium   # once
npm run e2e
```

The Playwright config spins up everything itself: the control plane (against a dedicated local test catalog
database, with bootstrap provisioning creating the e2e admin) and the Vite dev server, then exercises the GUI
element by element. See `playwright.config.ts` and `e2e/`.

## Branding

`src/theme/branding.css` holds the design tokens (carried over from the previous SQLFlow GUI's Radzen
Material 3 color base). The MUI theme is built from those custom properties at runtime
(`src/theme/ThemeModeContext.tsx`), so brand changes happen in one file.
