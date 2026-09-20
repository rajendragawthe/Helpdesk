# E2E tests (Playwright)

Full-stack browser tests. Playwright starts the API and the Vite dev server
itself (see `playwright.config.ts`) and points the API at an isolated
Postgres database so runs never touch dev/seeded data.

## One-time setup

```
cd e2e
npm install
npx playwright install --with-deps chromium
```

## Test database

This points at a `helpdesk_test` database on the same local Postgres
instance as dev (`localhost:5432`) — separate database, so E2E runs never
touch dev/seeded data.

Create the database once and apply migrations:

```
createdb -h localhost -p 5432 -U helpdesk helpdesk_test
dotnet ef database update \
  --project ../src/Helpdesk.Infrastructure \
  --startup-project ../src/Helpdesk.Api \
  --connection "Host=localhost;Port=5432;Database=helpdesk_test;Username=helpdesk;Password=helpdesk"
```

Re-run the migration command after pulling schema changes.

## Running tests

```
cd e2e
npm run test:e2e        # headless
npm run test:e2e:ui     # Playwright UI mode
npm run test:e2e:headed # headed browser
```

`playwright.config.ts`'s `webServer` entries launch `dotnet run` (API, with
`ConnectionStrings__DefaultConnection` overridden to the test DB) and
`npm run dev` (frontend) automatically, and reuse them if already running
locally. Override the target URLs/connection string via `E2E_FRONTEND_URL`,
`E2E_BACKEND_URL`, `E2E_DB_CONNECTION_STRING` env vars if needed (e.g. in CI).

## Known gap: Entra ID auth

The app authenticates via real Microsoft Entra ID SSO (MSAL + JWT bearer,
see root `CLAUDE.md`). There is no test identity provider wired up yet, so
tests that need an authenticated session will need a strategy for that
(e.g. a mocked auth flow or a dedicated test tenant) before they can be
written.
