# App-Shell Redesign (icon rail, master-detail queue, dashboard, theme) — Design

Date: 2026-09-27
Status: Approved in brainstorming (chat); awaiting written-spec review
Origin: manual full-loop test of Phase 7 (2026-09-27): "entire UI can be improved".
Plan: `docs/superpowers/plans/2026-09-27-app-shell-redesign.md` (to be written)
Sub-project 2 of 2 (sub-project 1, the sandboxed email HTML view, is merged and pushed).

## Goal

Turn the current top-navbar plus separate pages into one agent workspace: a slim icon rail, a
master-detail queue (list beside the selected ticket) and an action-oriented dashboard, with consistent
polish and a light/dark theme. An agent can triage, read, edit and reply without leaving one screen.

Frontend only. No backend, API, auth or migration changes.

## Decisions (from brainstorming)

1. **Layout B: icon rail + list + detail.** A 56 px icon-only rail (tooltips), a 340 px list column, and
   the detail pane taking the rest (most room for the email and the draft).
2. **Dashboard A: counts + "Needs your attention".** Three count cards (Unassigned, Mine, Needs review)
   and a list of the tickets to act on next. Built from existing list calls only.
3. **Theme: follow the system setting, with a manual toggle** remembered in the browser.
4. **Responsive: desktop-first.** Below 1024 px the list and detail stack (list first, detail full width
   with a Back button). The rail stays.
5. **Approach: nested layout routes** (not page-state selection, not restyle-only), so every ticket keeps a
   deep-linkable URL and the browser Back button works.
6. **Scope:** all four pieces (shell, master-detail queue, dashboard, polish + dark mode).

## Component map

```
client/helpdesk-web/src/
  App.tsx                         — slimmed: Routes with one RequireAuth layout + one admin guard
  layouts/
    AppShell.tsx (new)            — rail + header area + <Outlet/>; owns sign-out, theme menu
    TicketsLayout.tsx (new)       — list column + detail <Outlet/>; filter tabs; reads list state from context
  context/
    TicketsProvider.tsx (new)     — mounted in AppShell: holds the loaded list per filter (queue/mine/all),
                                    loading/error state and refresh(filter); shared by the rail badge, the
                                    dashboard and the list column so a claim/send refreshes all of them
  components/
    NavRail.tsx (new; replaces NavBar.tsx) — icon links, unassigned-count badge on Queue, profile menu
    ThemeProvider.tsx (new)       — 'system' | 'light' | 'dark', toggles class on <html>, localStorage
    TicketListItem.tsx (new)      — one row: subject, category, review-flag + status badges, assignee, age
    StatusBadge.tsx (new)         — status and needs-review badges shared by list, detail, dashboard
    EmptyState.tsx (new)          — shared empty/loading/error placeholder (skeletons via shadcn Skeleton)
    EmailHtmlViewer.tsx, MessageBody.tsx — UNCHANGED (email stays light-on-white)
  pages/
    DashboardPage.tsx (new)       — replaces HomePage as the /home content
    TicketDetail.tsx (refactor of TicketDetailPage.tsx) — same claim/release/edit/send behaviour, rendered
                                    inside the detail pane; no page chrome of its own
    QueuePage.tsx                 — removed; its list/filter/loading logic moves into TicketsLayout
    AdminUsersPage.tsx            — kept; rendered inside the shell (styling polish only)
    LandingPage.tsx               — kept, outside the shell
  hooks/useCurrentUser.ts         — unchanged
```

No new npm dependency is required: shadcn `Skeleton`, `Tooltip`, `DropdownMenu`, `Badge` and Lucide icons
are already vendored or installable with the pinned CLI (`npx shadcn@3.8.5 add <component>`).

## Behaviour

### Routing and guards
- `/` — `LandingPage` when signed out, redirect to `/home` when signed in (as today).
- `<RequireAuth>` layout route: waits out MSAL `Startup` (the existing comment in `App.tsx` explains why),
  redirects unauthenticated users to `/`, shows nothing while the current user is loading, and redirects a
  signed-in user with no registered record to `/home` (as today, the dashboard shows the "not registered"
  message). Its `<Outlet/>` is `AppShell`.
- Routes inside the shell: `/home` (dashboard), `/tickets` (list + empty detail state), `/tickets/:id`
  (list + detail), `/admin/users` (admin only; a non-admin is redirected to `/home`).
- The old per-route copy-pasted auth/NavBar wrappers in `App.tsx` are deleted.

### NavRail
- Icons: Dashboard, Queue, Mine, All (admins only), Users (admins only); bottom: profile/theme/sign-out menu.
- Active route highlighted; each icon has a tooltip label and an accessible name.
- The Queue icon shows the unassigned count (from `TicketsProvider`, which fetches the `queue` list once the
  shell mounts and on refresh(); no polling at MVP); a small dot when any ticket in
  the visible queue needs review. This gives the Phase 8 "needs review" surfacing without a new API.
- Filter icons (Queue/Mine/All) deep-link to `/tickets?filter=queue|mine|all`.

### Master-detail queue
- `TicketsLayout` reads `filter` from the URL search (default `queue`; `all` only for admins, otherwise
  falls back to `queue`) and gets that filter's list from `TicketsProvider`, which calls the existing
  `GET /api/tickets?filter=…` (the provider is the only place the list endpoint is called).
- The list column shows a row per ticket via `TicketListItem`; the selected ticket (from `:id`) is highlighted.
  Keeping the existing stale-response protection: a response for an outdated filter is ignored.
- Selecting a row navigates to `/tickets/:id?filter=…` (the filter is preserved).
- `TicketDetail` fetches `GET /api/tickets/{id}` as today and keeps all existing behaviour (claim, release,
  edit draft, send reply, error messages, per-message Formatted/Plain text toggle). After a claim, release
  or send it refreshes the list state so the row (status, assignee) and the rail count update without a
  full reload.
- If the selected ticket is not in the current filter's list (for example it was just replied and drops out
  of the queue), the detail pane still shows it; the list simply no longer highlights a row.
- Below 1024 px: at `/tickets` only the list is shown; at `/tickets/:id` only the detail is shown, with a
  Back link to `/tickets?filter=…`.
- Empty states: no tickets in the filter; no ticket selected (detail pane); ticket not found (404 from API).

### Dashboard (`/home`)
- Reads the `queue` and `mine` lists from `TicketsProvider` (loaded in parallel, two existing calls; `all`
  is never called for agents), refreshed when the dashboard mounts.
- Cards: **Unassigned** (queue items with no assignee), **Mine** (count of the `mine` list),
  **Needs review** (items in either list with `needsReview`). Each card links to the matching queue view.
- **Needs your attention** list, up to 8 rows, de-duplicated across the two lists: flagged tickets first,
  then oldest by `createdAt`; each row links to `/tickets/:id`.
- Loading skeletons, an empty state ("Nothing needs your attention"), and an error state with Retry.
- The "not registered" and generic error messages that `HomePage` shows today are preserved.

### Theme
- `ThemeProvider` holds `'system' | 'light' | 'dark'`. `system` follows `prefers-color-scheme` live;
  light/dark override it. The choice is stored in `localStorage` (every access wrapped in try/catch; the app
  renders correctly, following the system setting, if storage is unavailable).
- Applied as the `dark` class on `<html>` (the shadcn/Tailwind v4 convention already implied by the tokens in
  `src/index.css`); if the dark token set is incomplete, the missing tokens are added there.
- The theme is applied before first paint (a tiny inline script in `index.html` or an early class set in
  `main.tsx`) to avoid a light flash.
- The email iframe is deliberately not themed: customer HTML keeps a white background.

### Polish
Consistent spacing/typography scale, skeleton loaders instead of blank pages, shared empty/error states, a
uniform badge set (status: New/InReview/Replied; needs-review flag), and focus-visible rings on interactive
elements. `AdminUsersPage` keeps its logic and gets the shell's page padding and card styling only.

## Error handling

| Case | Result |
|---|---|
| List fetch fails | Error state in the list column with Retry; detail pane unaffected |
| Ticket fetch 404 | "Ticket not found" empty state with a link to the queue |
| Ticket fetch other failure | Existing error text with Retry |
| Dashboard fetch fails | Error state with Retry; nav still works |
| `localStorage` unavailable | Theme follows system; toggle works for the session only |
| Non-admin opens `?filter=all` or `/admin/users` | Falls back to `queue` / redirects to `/home` |
| User not registered | Redirected to `/home`, which shows the existing not-registered message |

## Testing

- There is no frontend test framework in this repo (as in the email-view sub-project): verification is
  `npm run lint` (no new errors or warnings in new files) and `npm run build`.
- Pure logic is kept in small, dependency-free functions so a later unit-test setup can cover it: the
  dashboard aggregation (counts, de-duplication, attention ordering), the filter-from-search parsing, and the
  theme resolution (`system` + media query → applied theme).
- Manual pass (user's hands: run both servers; a few real or seeded tickets): sign in as an agent and as an
  admin; rail icons and tooltips; Queue/Mine/All views and admin-only visibility; select tickets, deep-link
  and reload `/tickets/:id`, browser Back; claim, edit, send updates the row and rail count; the dashboard
  counts and attention list match the queue; theme toggle, system default and reload persistence; resize
  below 1024 px for the stacked list/detail with Back; the email HTML view still renders and stays white in
  dark mode; `/admin/users` inside the shell.
- Playwright E2E (`qa-engineer`) coverage for the new shell is deferred until after this merges, as in
  Phases 7 and 8.

## Out of scope

New or changed backend endpoints; charts and reporting (dashboard variant B); search, sorting, pagination and
bulk actions; keyboard shortcuts; notifications; per-row review-flag reason tooltips (only the badge);
customising the rail; changing the auth model or the email viewer; Phase 7b behaviour.
