import { defineConfig, devices } from '@playwright/test'

const FRONTEND_URL = process.env.E2E_FRONTEND_URL ?? 'http://localhost:5173'
const BACKEND_URL = process.env.E2E_BACKEND_URL ?? 'http://localhost:5080'
const TEST_DB_CONNECTION_STRING =
  process.env.E2E_DB_CONNECTION_STRING ??
  'Host=localhost;Port=5432;Database=helpdesk_test;Username=helpdesk;Password=helpdesk'

// https://playwright.dev/docs/test-configuration
export default defineConfig({
  testDir: './tests',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  workers: process.env.CI ? 1 : undefined,
  reporter: 'html',
  use: {
    baseURL: FRONTEND_URL,
    trace: 'on-first-retry',
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
  // Both the API and the SPA dev server are started against the isolated
  // `helpdesk_test` database (same local Postgres instance as dev, port
  // 5432) so E2E runs never touch dev/seeded data.
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
