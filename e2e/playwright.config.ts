import { defineConfig, devices } from '@playwright/test'
import { BACKEND_URL, FRONTEND_URL, TEST_DB_CONNECTION_STRING } from './env'

// https://playwright.dev/docs/test-configuration
export default defineConfig({
  testDir: './tests',
  globalSetup: './global-setup.ts',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  workers: process.env.CI ? 1 : undefined,
  reporter: 'html',
  use: {
    baseURL: FRONTEND_URL,
    trace: 'on-first-retry',
    // Optional escape hatch for environments where `npx playwright install` can't reach
    // playwright.dev's CDN to fetch the separate chrome-headless-shell binary (only the full
    // Chromium browser got downloaded) — point this at an already-installed chrome.exe/chrome
    // binary to run headless tests against that instead. Unset by default; normal setups
    // following e2e/README.md's `npx playwright install --with-deps chromium` never need it.
    launchOptions: process.env.E2E_CHROMIUM_EXECUTABLE_PATH
      ? { executablePath: process.env.E2E_CHROMIUM_EXECUTABLE_PATH }
      : undefined,
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
  // Both the API and the SPA dev server are started against the isolated
  // `helpdesk_test` database (same local Postgres instance as dev, port
  // 5432) so E2E runs never touch dev/seeded data. globalSetup then
  // truncates that database's tables before tests run (see global-setup.ts)
  // — no teardown, so a failed run's data is left in place to inspect.
  webServer: [
    {
      command: 'dotnet run --project ../src/Helpdesk.Api',
      url: `${BACKEND_URL}/api/health`,
      reuseExistingServer: !process.env.CI,
      timeout: 120_000,
      env: {
        ASPNETCORE_ENVIRONMENT: 'Development',
        ASPNETCORE_URLS: BACKEND_URL,
        ConnectionStrings__DefaultConnection: TEST_DB_CONNECTION_STRING,
        // Explicitly disable Graph email ingestion for E2E runs. appsettings.Development.json
        // has a real GraphApi section (non-secret ids), so an empty/unset env var wouldn't
        // reliably "unset" it via ASP.NET Core's config layering - GraphApi:Enabled=false is
        // checked explicitly by AddGraphApi/AddApplication's presence check and always wins.
        GraphApi__Enabled: 'false',
        // Same for OpenRouter AI classification: never call the real API from E2E runs.
        OpenRouter__Enabled: 'false',
      },
    },
    {
      command: 'npm run dev',
      cwd: '../client/helpdesk-web',
      url: FRONTEND_URL,
      reuseExistingServer: !process.env.CI,
      timeout: 60_000,
    },
  ],
})
