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

If `playwright install` can't reach playwright.dev's CDN in your environment
(e.g. a sandboxed/TLS-intercepted network) and only the full Chromium binary
downloads successfully (not the separate `chrome-headless-shell`), point
tests at the Chromium binary that did download instead of failing outright:

```
E2E_CHROMIUM_EXECUTABLE_PATH="/path/to/chrome" npm run test:e2e
```

See `playwright.config.ts`'s `use.launchOptions` — unset by default, this has
no effect on a normal setup.

## Test database

This points at a `helpdesk_test` database on the same local Postgres
instance as dev (`localhost:5432`) — separate database, so E2E runs never
touch dev/seeded data.

Create the database once and apply migrations:

```
createdb -h localhost -p 5432 -U helpdesk helpdesk_test
OpenRouter__Enabled=false GraphApi__Enabled=false dotnet ef database update \
  --project ../src/Helpdesk.Infrastructure \
  --startup-project ../src/Helpdesk.Api \
  --connection "Host=localhost;Port=5432;Database=helpdesk_test;Username=helpdesk;Password=helpdesk"
```

Re-run the migration command after pulling schema changes.

Each run's `globalSetup` (`global-setup.ts`) truncates `Users`, `Tickets`,
`Messages`, and `Classifications` (not `__EFMigrationsHistory`) before tests
start, so every run begins from an empty, known state. There's no teardown —
a failed run's data is left in place so you can inspect it with `psql`.

## Running tests

```
cd e2e
npm run test:e2e        # headless
npm run test:e2e:ui     # Playwright UI mode
npm run test:e2e:headed # headed browser
```

`playwright.config.ts`'s `webServer` entries launch `dotnet run` (API, with
`ConnectionStrings__DefaultConnection` overridden to the test DB, and
`GraphApi__Enabled=false` so the Phase 4 email-ingestion poller never runs
against a real mailbox or writes into the test DB mid-run, and
`OpenRouter__Enabled=false` so AI classification never calls the real
OpenRouter API) and
`npm run dev` (frontend) automatically, and reuse them if already running
locally. All of the target URLs and DB connection fields (`env.ts`) can be
overridden via `E2E_FRONTEND_URL`, `E2E_BACKEND_URL`, `E2E_DB_HOST`,
`E2E_DB_PORT`, `E2E_DB_NAME`, `E2E_DB_USER`, `E2E_DB_PASSWORD` — useful in CI.

## Known gap: Entra ID auth

The app authenticates via real Microsoft Entra ID SSO (MSAL + JWT bearer,
see root `CLAUDE.md`). There is no test identity provider wired up, so any
scenario that requires a *real, validly-signed* Entra token — the backend
actually issuing a 200 from `/api/auth/me`, the `HelpdeskUserClaimsTransformation`
first-login `ExternalObjectId` backfill, or a genuine `AdminOnly`/`AgentOnly`
success path — cannot be exercised end-to-end here. This would need either a
dedicated Entra test tenant with a service-principal-driven ROPC/client-credentials
flow, or a minimal test-only ASP.NET Core auth handler gated behind a
non-Development/non-Production `ASPNETCORE_ENVIRONMENT=Testing`, added
deliberately (not as an unreviewed bypass).

`tests/auth/` works around this gap two ways instead:
- **Frontend-only scenarios** (`not-registered-user.spec.ts`,
  `role-based-access.spec.ts`, `session-expiry-and-errors.spec.ts`,
  `multi-tab-session-isolation.spec.ts`) use `tests/auth/msal-mock.ts` to
  fabricate a *working* `@azure/msal-browser` sessionStorage cache (a real
  account + a real, unexpired cached access token — verified against
  `BrowserCacheManager`'s actual cache-key format, not guessed at) so
  `useIsAuthenticated()` is true and `apiFetch` resolves a token from cache
  with **no network call to Microsoft at all**, and mock `/api/auth/me` (and
  other `/api/*` calls) via `page.route()`. The fake access token is never
  validated by anything — it only ever reaches `page.route()` mocks, never
  the real backend — so this never bypasses real auth, it only drives the
  SPA's own state machine.
- **Negative API-layer scenarios** (`unauthenticated-access.spec.ts`,
  `api-authorization-edge-cases.spec.ts`) hit the real backend directly with
  no/malformed/tampered Authorization headers, which is fully testable
  without an IdP since JWT bearer validation rejects all of them before
  `HelpdeskUserClaimsTransformation` (or any controller) ever runs.
