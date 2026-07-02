import { defineConfig, devices } from "@playwright/test";

// The e2e suite spins up EVERYTHING itself:
//  1. the control plane (dotnet), pointed at a dedicated local test catalog database; bootstrap provisioning
//     applies migrations, seeds roles, and creates the e2e admin the tests sign in with;
//  2. the GUI dev server, pointed at that control plane via VITE_API_BASE_URL (which wins in dev mode).
// Requirements on the machine: a local SQL Server (integrated security) and the .NET SDK, same as the
// DB-backed xUnit suites.

export const E2E = {
  apiBaseUrl: "http://localhost:5299",
  guiBaseUrl: "http://localhost:5173",
  adminUsername: "e2e-admin",
  adminPassword: "e2e-admin-password-123456",
  bootstrapSecret: "e2e-bootstrap-secret-0123456789-PADDING",
  catalogDb: "Server=localhost;Database=SqlFlowCatalogE2E;Trusted_Connection=True;TrustServerCertificate=True",
} as const;

export default defineConfig({
  testDir: "./e2e",
  testMatch: /.*\.spec\.ts$/, // never pick up compiled .js siblings
  globalSetup: "./e2e/global-setup.ts",
  fullyParallel: false, // one control plane + one catalog: keep mutations ordered
  workers: 1,
  retries: process.env.CI ? 1 : 0,
  reporter: [["list"], ["html", { open: "never" }]],
  timeout: 60_000,
  use: {
    baseURL: E2E.guiBaseUrl,
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  projects: [
    {
      name: "chromium",
      use: { ...devices["Desktop Chrome"] },
    },
  ],
  webServer: [
    {
      command: "dotnet run --project ../src/SqlFlow.ControlPlane",
      url: `${E2E.apiBaseUrl}/health/live`,
      timeout: 240_000,
      reuseExistingServer: !process.env.CI,
      env: {
        ASPNETCORE_URLS: E2E.apiBaseUrl,
        ASPNETCORE_ENVIRONMENT: "Production",
        ControlPlane__Catalog__ConnectionReference: E2E.catalogDb,
        ControlPlane__Jwt__SigningKey: "e2e-signing-key-0123456789abcdef-0123456789abcdef-PADDING",
        ControlPlane__Jwt__BootstrapSecret: E2E.bootstrapSecret,
        ControlPlane__Bootstrap__AdminUsername: E2E.adminUsername,
        ControlPlane__Bootstrap__AdminPasswordReference: E2E.adminPassword,
        ControlPlane__Cors__AllowedOrigins__0: E2E.guiBaseUrl,
        // The suite polls aggressively; the production rate limit would trip on an innocent run.
        ControlPlane__RateLimit__PermitPerWindow: "1000000",
        // Tight loops so the suite is not waiting on production cadences.
        ControlPlane__Scheduler__PollSeconds: "1",
        ControlPlane__ManagedSync__PollSeconds: "1",
        // The sample CSV flow's sink connection reference: runs triggered by the suite really load data.
        SQLFlowSinkConStr: E2E.catalogDb,
      },
    },
    {
      command: "npm run dev",
      url: E2E.guiBaseUrl,
      timeout: 120_000,
      reuseExistingServer: !process.env.CI,
      env: {
        VITE_API_BASE_URL: E2E.apiBaseUrl,
      },
    },
  ],
});
