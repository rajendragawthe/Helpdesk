# Phase 1 — Auth (Entra ID SSO) Design

## Goal
Wire end-to-end Entra ID SSO: SPA login via MSAL React, bearer token attached to API calls, JWT validation in the API via Microsoft Identity Web, and Admin/Agent role claims read from Entra App Roles. This satisfies `implementation-plan.md` Phase 1 (steps 7–12).

## Context / constraint
No `Agent`/`Ticket` entities exist yet (Phase 2). Because there's no DB-backed agent table to source roles from, Admin/Agent role assignment for the MVP comes from **Entra ID App Roles** assigned directly to users in the Entra portal — not from application data. Phase 3 (admin adds agents) is additive on top of this; it does not replace the App Roles mechanism for now.

Entra app registrations must be created manually by the user in the Azure portal — this is an external action outside the repo. This design produces code that reads Entra config values (tenant ID, client IDs, scope) as placeholders; the auth flow cannot be manually verified until the user creates the registrations and supplies real values.

## Entra ID setup (manual, done by user)
1. **API app registration** (`Helpdesk.Api`):
   - Expose an API scope, e.g. `access_as_user`.
   - Define App Roles `Admin` and `Agent` (value + display name), assignable to users.
2. **SPA app registration** (`Helpdesk.Web`):
   - Public client / SPA platform, redirect URI `http://localhost:5173`.
   - API permission = the scope exposed by the API app registration; admin consent granted.
3. User assigns themselves the `Admin` app role via the API app's Enterprise Application → Users and groups blade.
4. User supplies back: tenant ID, API app client ID + scope URI (`api://<api-client-id>/access_as_user`), SPA app client ID.

## Backend (`Helpdesk.Api`, `Helpdesk.Core`)
- Add `Microsoft.Identity.Web` NuGet package to `Helpdesk.Api`.
- `Program.cs`: call `AddMicrosoftIdentityWebApiAuthentication(builder.Configuration.GetSection("AzureAd"))` and `AddAuthorization`.
- `appsettings.json` / `appsettings.Development.json`: add an `AzureAd` section (Instance, TenantId, ClientId, Audience) with placeholder values and a comment/README note that real values come from the user.
- `Helpdesk.Core/Enums/Role.cs`: `enum Role { Admin, Agent }`.
- Register two named authorization policies in `Program.cs`: `AdminOnly` (requires role claim `Admin`), `AgentOnly` (requires role claim `Admin` or `Agent`, since an Admin should also be able to act as an Agent for testing purposes).
- Microsoft Identity Web maps the token's `roles` claim to `ClaimTypes.Role` automatically — no custom claims transformation needed.
- New `Helpdesk.Api/Controllers/AuthController.cs`: `GET /api/auth/me` (`[Authorize]`), returns `{ name, email, roles }` read from `User.Claims`. This isn't in the plan's literal step list but is needed to manually verify the flow (plan step 12).

## Frontend (`client/helpdesk-web`)
- Add `@azure/msal-browser` and `@azure/msal-react` packages.
- `src/authConfig.ts`: `msalConfig` (clientId, authority = `https://login.microsoftonline.com/<tenantId>`, redirectUri) and `apiScopes` (the API scope URI), values sourced from `import.meta.env.VITE_*` (added to `.env.example`).
- Wrap `<App />` in `MsalProvider` (instantiated `PublicClientApplication`) in `main.tsx`.
- Add a login page/button using `loginRedirect` (simpler than popup for local dev, avoids popup-blocker issues).
- Add an `apiFetch` helper: acquires a token via `acquireTokenSilent` (falling back to `acquireTokenRedirect` on failure) and attaches `Authorization: Bearer <token>` to `/api/*` requests.
- Add a minimal authenticated page that calls `/api/auth/me` on load and renders the result, to prove the round trip.

## Testing
Manual test only (per plan step 12): user logs in via the SPA, frontend acquires a token and calls `/api/auth/me`, response confirms the user's name and `Admin` role. No automated tests are added in this phase — there's no business logic yet to unit test; auth wiring is inherently an integration concern verified manually.

## Out of scope (explicitly deferred)
- `Agent`/`Ticket`/`Classification` entities and repositories (Phase 2).
- Admin-adds-agent UI and DB-backed agent records (Phase 3).
- Any change to how roles are sourced once Phase 3 lands — that's a future decision, not part of this design.
