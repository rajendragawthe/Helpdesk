# App-Shell Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the top navbar and separate pages with one agent workspace: icon rail, master-detail ticket queue, action-oriented dashboard, and a light/dark theme (frontend only).

**Architecture:** Nested layout routes: `RequireAuth` guard -> `AppShell` (icon rail + `TicketsProvider`) -> pages. `/tickets` is a `TicketsLayout` (list column + `<Outlet/>` for the ticket detail at `/tickets/:id`). One `TicketsProvider` holds the loaded ticket lists per filter and is the only caller of `GET /api/tickets`; the rail badge, dashboard and list column read from it, and the detail calls `refresh()` after claim/release/send. `ThemeProvider` toggles the `dark` class on `<html>`.

**Tech Stack:** React 19, TypeScript, Vite, react-router 7, Tailwind v4, shadcn/ui (vendored in `src/components/ui`), lucide-react, date-fns, MSAL. No new npm dependency.

**Spec:** `docs/superpowers/specs/2026-09-27-app-shell-redesign-design.md`

## Global Constraints

- Frontend only, in `client/helpdesk-web`. Do not touch the backend, `authConfig.ts`, `hooks/useCurrentUser.ts`, `lib/emailDocument.ts`, `components/EmailHtmlViewer.tsx` or `components/MessageBody.tsx`.
- No new npm dependency and no test framework (none exists in this repo). Every task ends with `npm run lint` (0 errors, no new warnings in the files it touched; ~19 warnings in `src/components/ui/*` pre-exist) and `npm run build` succeeding, run from `client/helpdesk-web`.
- Use existing shadcn components from `@/components/ui/*` (`Alert`, `Badge`, `Button`, `Card`, `Skeleton`, `Tabs`, `Tooltip`, `DropdownMenu`, `Textarea`); import `cn` from `@/lib/utils`. No `dangerouslySetInnerHTML`. Call the API only through `apiFetch` (`src/api/apiFetch.ts`).
- Relative `/api/...` paths only. The list endpoint is called only from `TicketsProvider`.
- Every commit message ends with the line `Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>`.
- Never edit `implementation-plan.md` (non-UTF8 byte); doc changes go in `CLAUDE.md` only, with targeted edits.
- The customer-email iframe stays white in dark mode (do not theme it).
- The worktree has no `node_modules`: run `npm install` in `client/helpdesk-web` first (Task 1, step 1).

## Plan clarifications (rulings against the spec)

- "Mine" on the dashboard counts the caller's tickets that are not `Replied`; the attention list also excludes `Replied` (a replied ticket needs no attention). The queue list already excludes `Replied`.
- A signed-in user with no registered record (or a failed `/api/auth/me`) sees a standalone `AccessNotice` (message + Sign out) from the guard on any path, instead of being redirected to `/home` (the shell must not mount for them, since its list call would 403).
- Route guards live in `src/layouts/guards.tsx` (small addition to the spec's component map).
- Route params: `TicketsLayout` is a parent of the `:id` route, so it uses `useMatch('/tickets/:id')` (not `useParams`).

## Review Focus

Inputs and conditions the spec implies but no automated test exercises (no frontend test framework), most likely to bite first. Each is checked by the owning task's step and by the manual pass in Task 7.

1. Deep link and reload on `/tickets/:id?filter=mine` and `/admin/users` on a cold load: the MSAL `Startup` wait in `App.tsx` must stay, and the guard must not redirect before the session is known (Task 6).
2. Rapid filter switching (Queue -> Mine -> All): a slow response for an old filter must not overwrite a newer one (per-filter request counter in `TicketsProvider`, Task 2).
3. A ticket that drops out of the current list after a reply (`Replied` leaves the queue): the detail pane keeps showing it and `refresh()` must not reset the agent's editor text or notice (Tasks 3, 5).
4. Non-admin with `?filter=all` or `/admin/users`: falls back to `queue` / redirects to `/home` (Tasks 1, 5, 6).
5. Theme: `localStorage` throwing (private window), OS theme change while on `system`, no light flash on load, custom colours (`text-text`, `border-border`, `bg-bg`) readable in dark (Task 1).
6. Narrow width (< 1024px): `/tickets` shows only the list, `/tickets/:id` only the detail with a Back link (Task 5).
7. Unregistered / failed `/api/auth/me` user: sees the notice with a working Sign out and never the shell (Task 6).

---

### Task 1: Foundation: theme, pure ticket logic, global CSS

**Files:**
- Create: `client/helpdesk-web/src/lib/theme.ts`, `src/components/ThemeProvider.tsx`, `src/lib/ticketLogic.ts`
- Modify: `client/helpdesk-web/src/index.css`, `index.html`, `src/main.tsx`, `src/pages/LandingPage.tsx`

**Interfaces:**
- Produces: `ThemePreference`, `ResolvedTheme`, `resolveTheme`, `readStoredPreference`, `storePreference`, `applyTheme`, `systemPrefersDark` (lib/theme.ts); `ThemeProvider`, `useTheme()` returning `{ preference, resolved, setPreference }`; `parseFilter`, `unassignedCount`, `hasNeedsReview`, `buildDashboard`, `ATTENTION_LIMIT`, `DashboardData` (lib/ticketLogic.ts).

- [ ] **Step 1: Install dependencies**

Run in `client/helpdesk-web`: `npm install`. Expected: completes; `npm run build` baseline passes.

- [ ] **Step 2: Create `src/lib/theme.ts`**

```ts
export type ThemePreference = 'system' | 'light' | 'dark'
export type ResolvedTheme = 'light' | 'dark'

export const THEME_STORAGE_KEY = 'helpdesk-theme'

export function resolveTheme(preference: ThemePreference, systemDark: boolean): ResolvedTheme {
  if (preference === 'system') {
    return systemDark ? 'dark' : 'light'
  }
  return preference
}

export function systemPrefersDark(): boolean {
  return window.matchMedia('(prefers-color-scheme: dark)').matches
}

export function readStoredPreference(): ThemePreference {
  try {
    const value = window.localStorage.getItem(THEME_STORAGE_KEY)
    return value === 'light' || value === 'dark' || value === 'system' ? value : 'system'
  } catch {
    return 'system'
  }
}

export function storePreference(preference: ThemePreference): void {
  try {
    window.localStorage.setItem(THEME_STORAGE_KEY, preference)
  } catch {
    // Storage unavailable (private window, blocked site data): the choice lasts for this session only.
  }
}

export function applyTheme(theme: ResolvedTheme): void {
  document.documentElement.classList.toggle('dark', theme === 'dark')
}
```

- [ ] **Step 3: Create `src/components/ThemeProvider.tsx`**

```tsx
import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import {
  applyTheme,
  readStoredPreference,
  resolveTheme,
  storePreference,
  systemPrefersDark,
  type ResolvedTheme,
  type ThemePreference,
} from '../lib/theme'

type ThemeContextValue = {
  preference: ThemePreference
  resolved: ResolvedTheme
  setPreference: (preference: ThemePreference) => void
}

const ThemeContext = createContext<ThemeContextValue | null>(null)

export function ThemeProvider({ children }: { children: ReactNode }) {
  const [preference, setPreferenceState] = useState<ThemePreference>(readStoredPreference)
  const [systemDark, setSystemDark] = useState<boolean>(systemPrefersDark)

  useEffect(() => {
    const query = window.matchMedia('(prefers-color-scheme: dark)')
    const onChange = (event: MediaQueryListEvent) => setSystemDark(event.matches)
    query.addEventListener('change', onChange)
    return () => query.removeEventListener('change', onChange)
  }, [])

  const resolved = resolveTheme(preference, systemDark)

  useEffect(() => {
    applyTheme(resolved)
  }, [resolved])

  const value = useMemo<ThemeContextValue>(
    () => ({
      preference,
      resolved,
      setPreference: (next) => {
        setPreferenceState(next)
        storePreference(next)
      },
    }),
    [preference, resolved],
  )

  return <ThemeContext.Provider value={value}>{children}</ThemeContext.Provider>
}

export function useTheme(): ThemeContextValue {
  const context = useContext(ThemeContext)
  if (!context) {
    throw new Error('useTheme must be used within a ThemeProvider')
  }
  return context
}
```

- [ ] **Step 4: Create `src/lib/ticketLogic.ts`**

```ts
import type { TicketFilter, TicketListItem } from '../api/tickets'

export const ATTENTION_LIMIT = 8

/** Reads the queue filter from the URL; `all` is admin-only, anything unknown means `queue`. */
export function parseFilter(value: string | null, isAdmin: boolean): TicketFilter {
  if (value === 'mine') return 'mine'
  if (value === 'all' && isAdmin) return 'all'
  return 'queue'
}

export function unassignedCount(queue: TicketListItem[]): number {
  return queue.filter((ticket) => ticket.assignee === null).length
}

export function hasNeedsReview(tickets: TicketListItem[]): boolean {
  return tickets.some((ticket) => ticket.needsReview)
}

export type DashboardData = {
  unassigned: number
  mine: number
  needsReview: number
  attention: TicketListItem[]
}

function timeOf(value: string): number {
  const parsed = Date.parse(value)
  return Number.isNaN(parsed) ? 0 : parsed
}

/**
 * Dashboard numbers from the `queue` and `mine` lists. Replied tickets never count; tickets present in both
 * lists are counted once; attention = flagged first, then oldest, capped at ATTENTION_LIMIT.
 */
export function buildDashboard(queue: TicketListItem[], mine: TicketListItem[]): DashboardData {
  const open = (ticket: TicketListItem) => ticket.status !== 'Replied'
  const mineOpen = mine.filter(open)

  const unique = new Map<string, TicketListItem>()
  for (const ticket of [...queue, ...mineOpen]) {
    if (open(ticket) && !unique.has(ticket.id)) {
      unique.set(ticket.id, ticket)
    }
  }
  const all = [...unique.values()]

  const attention = all
    .slice()
    .sort(
      (a, b) =>
        Number(b.needsReview) - Number(a.needsReview) || timeOf(a.createdAt) - timeOf(b.createdAt),
    )
    .slice(0, ATTENTION_LIMIT)

  return {
    unassigned: unassignedCount(queue),
    mine: mineOpen.length,
    needsReview: all.filter((ticket) => ticket.needsReview).length,
    attention,
  }
}
```

- [ ] **Step 5: CSS (`src/index.css`)**

Replace the `@media (prefers-color-scheme: dark) { :root { ... } }` block (lines ~19-30) with a class-based block, and change `body`/`#root`:

```css
.dark {
  --color-text: #9ca3af;
  --color-text-h: #f3f4f6;
  --color-bg: #16171d;
  --color-border: #2e303a;
  --color-code-bg: #1f2028;
  --color-accent: #c084fc;
  --color-accent-bg: rgba(192, 132, 252, 0.15);
  --color-accent-border: rgba(192, 132, 252, 0.5);
}

:root {
  color-scheme: light;
}

.dark {
  color-scheme: dark;
}

body {
  margin: 0;
}

#root {
  width: 100%;
  min-height: 100svh;
  display: flex;
  flex-direction: column;
  box-sizing: border-box;
}
```

(Removes `color-scheme: light dark` from `body`, and removes `max-width`, `margin: 0 auto`, `text-align: center` and `border-inline` from `#root`.) Note the existing later `.dark { --background: ... }` shadcn block stays untouched; the new `.dark` block only holds the custom `--color-*` variables. Also make the shadcn `body` colours apply: if `body` has no `bg-background text-foreground` classes anywhere, add `body { background-color: var(--background); color: var(--foreground); }` so dark mode paints the page.

- [ ] **Step 6: `index.html`: apply the theme before first paint**

Add inside `<head>`, after the viewport meta:

```html
    <script>
      try {
        var stored = localStorage.getItem('helpdesk-theme');
        var dark = stored === 'dark' || (stored !== 'light' && window.matchMedia('(prefers-color-scheme: dark)').matches);
        document.documentElement.classList.toggle('dark', dark);
      } catch (e) {}
    </script>
```

- [ ] **Step 7: `src/main.tsx`: mount the providers**

Add imports `import { ThemeProvider } from './components/ThemeProvider.tsx'` and `import { TooltipProvider } from '@/components/ui/tooltip'`, and wrap the tree:

```tsx
<StrictMode>
  <ThemeProvider>
    <TooltipProvider>
      <MsalProvider instance={msalInstance}>
        <BrowserRouter>
          <App />
        </BrowserRouter>
      </MsalProvider>
    </TooltipProvider>
  </ThemeProvider>
</StrictMode>,
```

- [ ] **Step 8: `src/pages/LandingPage.tsx`**

`#root` no longer centres text, so add `text-center` to the section: `className="flex grow flex-col items-center gap-5 px-8 py-16 text-center"`.

- [ ] **Step 9: Verify and commit**

Run `npm run lint` then `npm run build` in `client/helpdesk-web`. Commit: `git add -A && git commit -m "feat: add theme provider, ticket logic helpers and full-width root layout"` (plus the Co-Authored-By trailer).

---

### Task 2: TicketsProvider and shared list components

**Files:**
- Create: `client/helpdesk-web/src/context/TicketsProvider.tsx`, `src/components/StatusBadge.tsx`, `src/components/EmptyState.tsx`, `src/components/TicketListRow.tsx`

**Interfaces:**
- Consumes: `TicketFilter`, `TicketListItem`, `TicketStatus`, `errorMessage` (api/tickets.ts); `apiFetch`.
- Produces: `TicketsProvider`, `useTickets()` returning `{ lists: Record<TicketFilter, { items: TicketListItem[] | null; error: string | null }>, load(filter): Promise<void>, refresh(): Promise<void> }` (inert defaults outside a provider); `StatusBadge({ status })`, `ReviewBadge()`; `EmptyState({ title, description?, action? })`, `ListSkeleton({ rows? })`; `TicketListRow({ ticket, to, selected? })`.

- [ ] **Step 1: Create `src/context/TicketsProvider.tsx`**

```tsx
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { useMsal } from '@azure/msal-react'
import { apiFetch } from '../api/apiFetch'
import { errorMessage, type TicketFilter, type TicketListItem } from '../api/tickets'

export type ListState = { items: TicketListItem[] | null; error: string | null }
type Lists = Record<TicketFilter, ListState>

const EMPTY: ListState = { items: null, error: null }
const INITIAL: Lists = { queue: EMPTY, mine: EMPTY, all: EMPTY }

type TicketsContextValue = {
  lists: Lists
  load: (filter: TicketFilter) => Promise<void>
  refresh: () => Promise<void>
}

// Outside a provider the hooks are inert, so a page that calls useTickets() still renders.
const INERT: TicketsContextValue = { lists: INITIAL, load: async () => {}, refresh: async () => {} }

const TicketsContext = createContext<TicketsContextValue>(INERT)

export function TicketsProvider({ children }: { children: ReactNode }) {
  const { instance } = useMsal()
  const [lists, setLists] = useState<Lists>(INITIAL)
  const requestIds = useRef<Record<TicketFilter, number>>({ queue: 0, mine: 0, all: 0 })
  const loaded = useRef(new Set<TicketFilter>())

  const load = useCallback(
    async (filter: TicketFilter) => {
      loaded.current.add(filter)
      const requestId = ++requestIds.current[filter]
      try {
        const response = await apiFetch(instance, `/api/tickets?filter=${filter}`)
        if (!response.ok) {
          throw new Error(await errorMessage(response))
        }
        const items = (await response.json()) as TicketListItem[]
        // A newer request for the same filter supersedes this one: ignore the stale response.
        if (requestId === requestIds.current[filter]) {
          setLists((prev) => ({ ...prev, [filter]: { items, error: null } }))
        }
      } catch (err) {
        if (requestId === requestIds.current[filter]) {
          const message = err instanceof Error ? err.message : 'Unknown error'
          setLists((prev) => ({ ...prev, [filter]: { items: prev[filter].items, error: message } }))
        }
      }
    },
    [instance],
  )

  const refresh = useCallback(async () => {
    await Promise.all([...loaded.current].map((filter) => load(filter)))
  }, [load])

  // The rail badge needs the queue as soon as the shell mounts.
  useEffect(() => {
    void load('queue')
  }, [load])

  const value = useMemo(() => ({ lists, load, refresh }), [lists, load, refresh])

  return <TicketsContext.Provider value={value}>{children}</TicketsContext.Provider>
}

export function useTickets(): TicketsContextValue {
  return useContext(TicketsContext)
}
```

- [ ] **Step 2: Create `src/components/StatusBadge.tsx`**

```tsx
import { Flag } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import type { TicketStatus } from '../api/tickets'

function statusVariant(status: TicketStatus): 'default' | 'secondary' | 'outline' {
  if (status === 'Replied') return 'secondary'
  if (status === 'InReview') return 'default'
  return 'outline'
}

export function StatusBadge({ status }: { status: TicketStatus }) {
  return <Badge variant={statusVariant(status)}>{status}</Badge>
}

export function ReviewBadge() {
  return (
    <Badge variant="destructive" className="gap-1">
      <Flag className="size-3" aria-hidden="true" />
      Review
    </Badge>
  )
}
```

- [ ] **Step 3: Create `src/components/EmptyState.tsx`**

```tsx
import type { ReactNode } from 'react'
import { Skeleton } from '@/components/ui/skeleton'

type EmptyStateProps = {
  title: string
  description?: string
  action?: ReactNode
}

export function EmptyState({ title, description, action }: EmptyStateProps) {
  return (
    <div className="flex flex-col items-center gap-2 px-6 py-12 text-center">
      <p className="font-medium text-foreground">{title}</p>
      {description && <p className="text-sm text-muted-foreground">{description}</p>}
      {action}
    </div>
  )
}

export function ListSkeleton({ rows = 6 }: { rows?: number }) {
  return (
    <div role="status" aria-label="Loading" className="flex flex-col gap-2 p-3">
      {Array.from({ length: rows }, (_, index) => (
        <Skeleton key={index} className="h-16 w-full" />
      ))}
    </div>
  )
}
```

- [ ] **Step 4: Create `src/components/TicketListRow.tsx`**

```tsx
import { Link } from 'react-router'
import { formatDistanceToNow } from 'date-fns'
import { cn } from '@/lib/utils'
import { Badge } from '@/components/ui/badge'
import type { TicketListItem } from '../api/tickets'
import { ReviewBadge, StatusBadge } from './StatusBadge'

function formatAge(value: string): string {
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '—' : formatDistanceToNow(date, { addSuffix: true })
}

type TicketListRowProps = {
  ticket: TicketListItem
  to: string
  selected?: boolean
}

export function TicketListRow({ ticket, to, selected = false }: TicketListRowProps) {
  return (
    <Link
      to={to}
      aria-current={selected ? 'true' : undefined}
      className={cn(
        'flex flex-col gap-1 border-b border-border px-4 py-3 text-left text-sm hover:bg-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
        selected && 'bg-accent',
      )}
    >
      <span className="truncate font-medium text-foreground">{ticket.subject}</span>
      <span className="truncate text-xs text-muted-foreground">{ticket.requesterEmail}</span>
      <span className="flex flex-wrap items-center gap-1.5">
        <StatusBadge status={ticket.status} />
        {ticket.needsReview && <ReviewBadge />}
        {ticket.category && <Badge variant="outline">{ticket.category}</Badge>}
      </span>
      <span className="flex justify-between gap-2 text-xs text-muted-foreground">
        <span className="truncate">{ticket.assignee?.displayName ?? 'Unassigned'}</span>
        <span className="shrink-0">{formatAge(ticket.createdAt)}</span>
      </span>
    </Link>
  )
}
```

- [ ] **Step 5: Verify and commit**

`npm run lint` and `npm run build`. Commit: `feat: add tickets provider and shared list components`.

---

### Task 3: Refactor the ticket detail into a pane component

**Files:**
- Rename: `client/helpdesk-web/src/pages/TicketDetailPage.tsx` -> `src/pages/TicketDetail.tsx` (use `git mv`)
- Modify: `src/pages/TicketDetail.tsx`, `src/App.tsx` (import path only)

**Interfaces:**
- Consumes: `useTickets` (`refresh`), `StatusBadge`, `ReviewBadge`, `parseFilter`.
- Produces: default export `TicketDetail({ user, isAdmin })` with unchanged behaviour (claim, release, edit, send, per-message toggle), rendered as an `<article>` with no page chrome; calls `refresh()` after every successful action; shows a Back link (`lg:hidden`) to `/tickets?filter=<current>`.

- [ ] **Step 1: Rename**

`git mv src/pages/TicketDetailPage.tsx src/pages/TicketDetail.tsx`. In `src/App.tsx` change the import to `import TicketDetailPage from './pages/TicketDetail'` (the route element keeps working; `App.tsx` is rewritten in Task 6).

- [ ] **Step 2: Edit imports and names in `TicketDetail.tsx`**

- `import { Link, useParams } from 'react-router'` -> `import { Link, useParams, useSearchParams } from 'react-router'`.
- Add: `import { useTickets } from '../context/TicketsProvider'`, `import { ReviewBadge, StatusBadge } from '../components/StatusBadge'`, `import { parseFilter } from '../lib/ticketLogic'`, `import { ListSkeleton } from '../components/EmptyState'`.
- Rename type `TicketDetailPageProps` -> `TicketDetailProps`, function `TicketDetailPage` -> `TicketDetail`, `export default TicketDetail`. Keep `TicketDetailContent` and the `key={id}` wrapper exactly as is.

- [ ] **Step 3: Add the refresh call and the back link target**

At the top of `TicketDetailContent` (after `const { instance } = useMsal()`):

```tsx
  const { refresh } = useTickets()
  const [searchParams] = useSearchParams()
  const backTo = `/tickets?filter=${parseFilter(searchParams.get('filter'), isAdmin)}`
```

In `act`, immediately after the `try { data = ... } catch { ... return false }` block that parses the response (before `if (!active.current) { return true }`), add:

```tsx
      // Lists (rail badge, queue rows, dashboard) must reflect the claim/release/send; the detail keeps its own state.
      void refresh()
```

Do not call `refresh()` from the initial fetch, and do not touch `text`, `notice` or `draftSeeded` in response to it.

- [ ] **Step 4: Replace the render wrappers**

- `loadError` return becomes:

```tsx
    return (
      <div className="mx-auto max-w-3xl p-6 text-left">
        <Alert variant="destructive">
          <AlertDescription>{loadError}</AlertDescription>
        </Alert>
        <Link to={backTo} className="mt-4 inline-block text-sm hover:underline">
          ← Back to tickets
        </Link>
      </div>
    )
```

- Loading return becomes `return <ListSkeleton rows={3} />`.
- The main return: replace `<main className="mx-auto flex max-w-4xl flex-col gap-6 px-8 py-8">` ... `</main>` with `<article className="mx-auto flex max-w-3xl flex-col gap-6 p-6 text-left">` ... `</article>`; the first child link becomes `<Link to={backTo} className="text-sm hover:underline lg:hidden">← Back to tickets</Link>`; in the header, replace `<Badge>{ticket.status}</Badge>` with `<StatusBadge status={ticket.status} />{ticket.needsReview && <ReviewBadge />}` (keep the category `Badge`). Keep the `Badge` import (still used for Customer/Agent and category).

- [ ] **Step 5: Verify and commit**

`npm run lint` and `npm run build`. Commit: `refactor: turn ticket detail page into a pane component`.

---

### Task 4: Dashboard page and access notice

**Files:**
- Create: `client/helpdesk-web/src/pages/DashboardPage.tsx`, `src/components/AccessNotice.tsx`

**Interfaces:**
- Consumes: `useTickets`, `buildDashboard`, `TicketListRow`, `EmptyState`, `ListSkeleton`, `CurrentUser`.
- Produces: default exports `DashboardPage({ user })` and `AccessNotice({ error, notRegistered })`.

- [ ] **Step 1: Create `src/pages/DashboardPage.tsx`**

```tsx
import { useCallback, useEffect } from 'react'
import { Link } from 'react-router'
import { useMsal } from '@azure/msal-react'
import type { CurrentUser } from '../hooks/useCurrentUser'
import { useTickets } from '../context/TicketsProvider'
import { buildDashboard } from '../lib/ticketLogic'
import { TicketListRow } from '../components/TicketListRow'
import { EmptyState, ListSkeleton } from '../components/EmptyState'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'

type DashboardPageProps = {
  user: CurrentUser | null
}

function DashboardPage({ user }: DashboardPageProps) {
  const { accounts } = useMsal()
  const { lists, load } = useTickets()
  const displayName = user?.name ?? accounts[0]?.name ?? accounts[0]?.username ?? ''

  const reload = useCallback(() => {
    void load('queue')
    void load('mine')
  }, [load])

  useEffect(() => {
    reload()
  }, [reload])

  const error = lists.queue.error ?? lists.mine.error
  const queue = lists.queue.items
  const mine = lists.mine.items
  const data = queue && mine ? buildDashboard(queue, mine) : null
  const loading = !error && data === null

  const cards = data
    ? [
        { label: 'Unassigned', value: data.unassigned, to: '/tickets?filter=queue', danger: false },
        { label: 'Mine', value: data.mine, to: '/tickets?filter=mine', danger: false },
        { label: 'Needs review', value: data.needsReview, to: '/tickets?filter=queue', danger: data.needsReview > 0 },
      ]
    : []

  return (
    <section className="mx-auto flex max-w-4xl flex-col gap-6 p-6 text-left">
      <h1 className="font-heading text-2xl font-semibold text-text-h">
        Welcome{displayName ? `, ${displayName}` : ''}
      </h1>

      {error && (
        <Alert variant="destructive">
          <AlertDescription className="flex items-center justify-between gap-4">
            <span>{error}</span>
            <Button type="button" size="sm" variant="outline" onClick={reload}>
              Retry
            </Button>
          </AlertDescription>
        </Alert>
      )}

      {loading && <ListSkeleton rows={4} />}

      {data && (
        <>
          <div className="grid gap-4 sm:grid-cols-3">
            {cards.map((card) => (
              <Link key={card.label} to={card.to} className="block focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring rounded-xl">
                <Card className={card.danger ? 'border-destructive/50' : undefined}>
                  <CardHeader>
                    <CardTitle className="text-sm font-medium text-muted-foreground">{card.label}</CardTitle>
                  </CardHeader>
                  <CardContent className="text-3xl font-semibold">{card.value}</CardContent>
                </Card>
              </Link>
            ))}
          </div>

          <Card>
            <CardHeader>
              <CardTitle>Needs your attention</CardTitle>
            </CardHeader>
            <CardContent className="p-0">
              {data.attention.length === 0 ? (
                <EmptyState title="Nothing needs your attention" description="New and flagged tickets will appear here." />
              ) : (
                data.attention.map((ticket) => (
                  <TicketListRow key={ticket.id} ticket={ticket} to={`/tickets/${ticket.id}?filter=queue`} />
                ))
              )}
            </CardContent>
          </Card>
        </>
      )}
    </section>
  )
}

export default DashboardPage
```

- [ ] **Step 2: Create `src/components/AccessNotice.tsx`**

```tsx
import { useMsal } from '@azure/msal-react'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'

type AccessNoticeProps = {
  error: string | null
  notRegistered: boolean
}

function AccessNotice({ error, notRegistered }: AccessNoticeProps) {
  const { instance } = useMsal()

  return (
    <section className="flex grow flex-col items-center justify-center gap-4 px-8 py-16 text-center">
      <Alert variant="destructive" className="max-w-[560px] text-left">
        <AlertTitle>{notRegistered ? 'Access not set up' : 'Something went wrong'}</AlertTitle>
        <AlertDescription>{error ?? 'Your account could not be loaded.'}</AlertDescription>
      </Alert>
      <Button type="button" variant="outline" onClick={() => instance.logoutRedirect()}>
        Sign out
      </Button>
    </section>
  )
}

export default AccessNotice
```

- [ ] **Step 3: Verify and commit**

`npm run lint` and `npm run build`. Commit: `feat: add dashboard page and access notice`.

---

### Task 5: Master-detail tickets layout

**Files:**
- Create: `client/helpdesk-web/src/layouts/TicketsLayout.tsx`

**Interfaces:**
- Consumes: `useTickets`, `parseFilter`, `TicketListRow`, `EmptyState`, `ListSkeleton`.
- Produces: default export `TicketsLayout({ isAdmin })`: a layout route element rendering the list column and, in the detail slot, `<Outlet/>` when a ticket is selected (the child route `:id` renders `TicketDetail`) or an empty state.

- [ ] **Step 1: Create `src/layouts/TicketsLayout.tsx`**

```tsx
import { useCallback, useEffect } from 'react'
import { Outlet, useMatch, useNavigate, useSearchParams } from 'react-router'
import { cn } from '@/lib/utils'
import type { TicketFilter } from '../api/tickets'
import { useTickets } from '../context/TicketsProvider'
import { parseFilter } from '../lib/ticketLogic'
import { EmptyState, ListSkeleton } from '../components/EmptyState'
import { TicketListRow } from '../components/TicketListRow'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs'

const TAB_LABELS: Record<TicketFilter, string> = {
  queue: 'Queue',
  mine: 'Mine',
  all: 'All',
}

type TicketsLayoutProps = {
  isAdmin: boolean
}

function TicketsLayout({ isAdmin }: TicketsLayoutProps) {
  const [searchParams] = useSearchParams()
  const navigate = useNavigate()
  // TicketsLayout is the parent of the ":id" route, so useParams() would not see the id here.
  const match = useMatch('/tickets/:id')
  const selectedId = match?.params.id
  const filter = parseFilter(searchParams.get('filter'), isAdmin)
  const { lists, load } = useTickets()
  const list = lists[filter]

  const reload = useCallback(() => {
    void load(filter)
  }, [load, filter])

  useEffect(() => {
    reload()
  }, [reload])

  const filters: TicketFilter[] = isAdmin ? ['queue', 'mine', 'all'] : ['queue', 'mine']

  const changeFilter = (next: TicketFilter) => {
    navigate(`/tickets${selectedId ? `/${selectedId}` : ''}?filter=${next}`)
  }

  return (
    <div className="flex h-full min-h-0">
      <section
        aria-label="Tickets"
        className={cn(
          'flex w-full min-w-0 flex-col border-r border-border lg:w-[340px] lg:shrink-0',
          selectedId && 'hidden lg:flex',
        )}
      >
        <div className="border-b border-border p-3">
          <Tabs value={filter} onValueChange={(value) => changeFilter(value as TicketFilter)}>
            <TabsList className="w-full">
              {filters.map((f) => (
                <TabsTrigger key={f} value={f} className="flex-1">
                  {TAB_LABELS[f]}
                </TabsTrigger>
              ))}
            </TabsList>
          </Tabs>
        </div>

        <div className="min-h-0 flex-1 overflow-y-auto">
          {list.error && (
            <div className="p-3">
              <Alert variant="destructive">
                <AlertDescription className="flex items-center justify-between gap-3">
                  <span>{list.error}</span>
                  <Button type="button" size="sm" variant="outline" onClick={reload}>
                    Retry
                  </Button>
                </AlertDescription>
              </Alert>
            </div>
          )}
          {list.items === null && !list.error && <ListSkeleton />}
          {list.items !== null && list.items.length === 0 && (
            <EmptyState title="No tickets here" description="New tickets will show up in this list." />
          )}
          {list.items?.map((ticket) => (
            <TicketListRow
              key={ticket.id}
              ticket={ticket}
              to={`/tickets/${ticket.id}?filter=${filter}`}
              selected={ticket.id === selectedId}
            />
          ))}
        </div>
      </section>

      <div className={cn('min-w-0 flex-1 overflow-y-auto', !selectedId && 'hidden lg:block')}>
        {selectedId ? (
          <Outlet />
        ) : (
          <EmptyState title="Select a ticket" description="Pick a ticket from the list to read it and reply." />
        )}
      </div>
    </div>
  )
}

export default TicketsLayout
```

- [ ] **Step 2: Verify and commit**

`npm run lint` and `npm run build`. Commit: `feat: add master-detail tickets layout`.

---

### Task 6: Icon rail, app shell, guards and routing

**Files:**
- Create: `client/helpdesk-web/src/components/NavRail.tsx`, `src/layouts/AppShell.tsx`, `src/layouts/guards.tsx`
- Modify: `src/App.tsx` (full rewrite), `src/pages/AdminUsersPage.tsx` (one className)
- Delete: `src/components/NavBar.tsx`, `src/pages/QueuePage.tsx`, `src/pages/HomePage.tsx`

**Interfaces:**
- Consumes: everything from Tasks 1-5.
- Produces: the final route tree; `NavRail({ user, isAdmin })`; `AppShell({ user, isAdmin })`; `RequireAuth`, `RequireAdmin` (guards.tsx).

- [ ] **Step 1: Check the dropdown API**

Open `src/components/ui/dropdown-menu.tsx` and confirm it exports `DropdownMenu`, `DropdownMenuTrigger`, `DropdownMenuContent`, `DropdownMenuLabel`, `DropdownMenuSeparator`, `DropdownMenuItem`, `DropdownMenuRadioGroup`, `DropdownMenuRadioItem`. If a radio component is missing, use `DropdownMenuItem` with a check mark for the active preference instead.

- [ ] **Step 2: Create `src/components/NavRail.tsx`**

```tsx
import type { ReactNode } from 'react'
import { Link, useLocation, useSearchParams } from 'react-router'
import { useMsal } from '@azure/msal-react'
import {
  CircleUser,
  Inbox,
  Layers,
  LayoutDashboard,
  LogOut,
  UserCheck,
  UserCog,
  type LucideIcon,
} from 'lucide-react'
import { cn } from '@/lib/utils'
import type { CurrentUser } from '../hooks/useCurrentUser'
import { useTickets } from '../context/TicketsProvider'
import { useTheme } from './ThemeProvider'
import { hasNeedsReview, parseFilter, unassignedCount } from '../lib/ticketLogic'
import type { ThemePreference } from '../lib/theme'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuRadioGroup,
  DropdownMenuRadioItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'

type RailItem = {
  to: string
  label: string
  icon: LucideIcon
  active: boolean
  badge?: ReactNode
}

const ITEM_CLASS =
  'relative flex size-10 items-center justify-center rounded-lg text-muted-foreground hover:bg-accent hover:text-accent-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring'

type NavRailProps = {
  user: CurrentUser | null
  isAdmin: boolean
}

function NavRail({ user, isAdmin }: NavRailProps) {
  const { instance } = useMsal()
  const { pathname } = useLocation()
  const [searchParams] = useSearchParams()
  const { lists } = useTickets()
  const { preference, setPreference } = useTheme()

  const onTickets = pathname.startsWith('/tickets')
  const activeFilter = parseFilter(searchParams.get('filter'), isAdmin)
  const queueItems = lists.queue.items ?? []
  const unassigned = unassignedCount(queueItems)
  const anyReview = hasNeedsReview(queueItems)

  const items: RailItem[] = [
    { to: '/home', label: 'Dashboard', icon: LayoutDashboard, active: pathname === '/home' },
    {
      to: '/tickets?filter=queue',
      label: `Queue, ${unassigned} unassigned${anyReview ? ', some need review' : ''}`,
      icon: Inbox,
      active: onTickets && activeFilter === 'queue',
      badge: (
        <>
          {unassigned > 0 && (
            <span className="absolute -top-0.5 -right-0.5 min-w-4 rounded-full bg-primary px-1 text-center text-[10px] leading-4 text-primary-foreground">
              {unassigned}
            </span>
          )}
          {anyReview && <span className="absolute right-0.5 bottom-0.5 size-2 rounded-full bg-destructive" aria-hidden="true" />}
        </>
      ),
    },
    { to: '/tickets?filter=mine', label: 'Mine', icon: UserCheck, active: onTickets && activeFilter === 'mine' },
    ...(isAdmin
      ? [
          { to: '/tickets?filter=all', label: 'All tickets', icon: Layers, active: onTickets && activeFilter === 'all' },
          { to: '/admin/users', label: 'Manage agents', icon: UserCog, active: pathname === '/admin/users' },
        ]
      : []),
  ]

  return (
    <nav aria-label="Main" className="flex w-14 shrink-0 flex-col items-center gap-2 border-r border-border bg-sidebar py-3">
      <span className="mb-2 flex size-10 items-center justify-center rounded-lg bg-primary font-heading text-lg font-semibold text-primary-foreground" aria-hidden="true">
        H
      </span>

      {items.map((item) => (
        <Tooltip key={item.to}>
          <TooltipTrigger asChild>
            <Link
              to={item.to}
              aria-label={item.label}
              aria-current={item.active ? 'page' : undefined}
              className={cn(ITEM_CLASS, item.active && 'bg-accent text-accent-foreground')}
            >
              <item.icon className="size-5" aria-hidden="true" />
              {item.badge}
            </Link>
          </TooltipTrigger>
          <TooltipContent side="right">{item.label.split(',')[0]}</TooltipContent>
        </Tooltip>
      ))}

      <div className="mt-auto">
        <DropdownMenu>
          <Tooltip>
            <TooltipTrigger asChild>
              <DropdownMenuTrigger aria-label="Account and theme" className={ITEM_CLASS}>
                <CircleUser className="size-5" aria-hidden="true" />
              </DropdownMenuTrigger>
            </TooltipTrigger>
            <TooltipContent side="right">Account</TooltipContent>
          </Tooltip>
          <DropdownMenuContent side="right" align="end" className="w-52">
            <DropdownMenuLabel className="truncate">{user?.name ?? user?.email ?? 'Signed in'}</DropdownMenuLabel>
            <DropdownMenuSeparator />
            <DropdownMenuLabel className="text-xs font-normal text-muted-foreground">Theme</DropdownMenuLabel>
            <DropdownMenuRadioGroup value={preference} onValueChange={(value) => setPreference(value as ThemePreference)}>
              <DropdownMenuRadioItem value="system">System</DropdownMenuRadioItem>
              <DropdownMenuRadioItem value="light">Light</DropdownMenuRadioItem>
              <DropdownMenuRadioItem value="dark">Dark</DropdownMenuRadioItem>
            </DropdownMenuRadioGroup>
            <DropdownMenuSeparator />
            <DropdownMenuItem onSelect={() => instance.logoutRedirect()}>
              <LogOut className="size-4" aria-hidden="true" />
              Sign out
            </DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
      </div>
    </nav>
  )
}

export default NavRail
```

If any icon name does not exist in the installed `lucide-react`, pick the nearest existing icon (`npx tsc -b` reports it); if `Tooltip` around a `DropdownMenuTrigger` misbehaves (tooltip stuck open), drop the tooltip on the account trigger and keep its `aria-label`.

- [ ] **Step 3: Create `src/layouts/AppShell.tsx`**

```tsx
import { Outlet } from 'react-router'
import type { CurrentUser } from '../hooks/useCurrentUser'
import { TicketsProvider } from '../context/TicketsProvider'
import NavRail from '../components/NavRail'

type AppShellProps = {
  user: CurrentUser | null
  isAdmin: boolean
}

function AppShell({ user, isAdmin }: AppShellProps) {
  return (
    <TicketsProvider>
      <div className="flex h-svh grow text-left">
        <NavRail user={user} isAdmin={isAdmin} />
        <main className="min-h-0 min-w-0 flex-1 overflow-y-auto">
          <Outlet />
        </main>
      </div>
    </TicketsProvider>
  )
}

export default AppShell
```

- [ ] **Step 4: Create `src/layouts/guards.tsx`**

```tsx
import { Navigate, Outlet } from 'react-router'
import type { CurrentUser } from '../hooks/useCurrentUser'
import AccessNotice from '../components/AccessNotice'

type RequireAuthProps = {
  isAuthenticated: boolean
  loading: boolean
  user: CurrentUser | null
  error: string | null
  notRegistered: boolean
}

export function RequireAuth({ isAuthenticated, loading, user, error, notRegistered }: RequireAuthProps) {
  if (!isAuthenticated) {
    return <Navigate to="/" replace />
  }
  if (loading) {
    return null
  }
  if (!user) {
    return <AccessNotice error={error} notRegistered={notRegistered} />
  }
  return <Outlet />
}

export function RequireAdmin({ isAdmin }: { isAdmin: boolean }) {
  return isAdmin ? <Outlet /> : <Navigate to="/home" replace />
}
```

- [ ] **Step 5: Rewrite `src/App.tsx`**

```tsx
import { Navigate, Route, Routes } from 'react-router'
import { useIsAuthenticated, useMsal } from '@azure/msal-react'
import { InteractionStatus } from '@azure/msal-browser'
import LandingPage from './pages/LandingPage'
import DashboardPage from './pages/DashboardPage'
import AdminUsersPage from './pages/AdminUsersPage'
import TicketDetail from './pages/TicketDetail'
import AppShell from './layouts/AppShell'
import TicketsLayout from './layouts/TicketsLayout'
import { RequireAdmin, RequireAuth } from './layouts/guards'
import { useCurrentUser } from './hooks/useCurrentUser'

function App() {
  const { inProgress } = useMsal()
  const isAuthenticated = useIsAuthenticated()
  const { user, loading, error, notRegistered } = useCurrentUser()
  const isAdmin = user?.roles.includes('Admin') ?? false

  // MsalProvider always mounts with inProgress "Startup" and empty accounts, even though
  // msalInstance.initialize() already resolved in main.tsx — it re-runs initialize()/
  // handleRedirectPromise() itself and only flips to "None" (with accounts populated from
  // cache) once that settles, one render tick after the first paint. useIsAuthenticated()
  // is hard-coded to return false during Startup, so deciding a route's Navigate off it at
  // this point would treat every cold page load as unauthenticated and redirect a deep link
  // (e.g. /admin/users) away before the real cached session is known — bouncing it through
  // "/" and landing on /home instead of the requested route. Wait out Startup first.
  if (inProgress === InteractionStatus.Startup) {
    return null
  }

  return (
    <Routes>
      <Route
        path="/"
        element={isAuthenticated ? <Navigate to="/home" replace /> : <LandingPage />}
      />
      <Route
        element={
          <RequireAuth
            isAuthenticated={isAuthenticated}
            loading={loading}
            user={user}
            error={error}
            notRegistered={notRegistered}
          />
        }
      >
        <Route element={<AppShell user={user} isAdmin={isAdmin} />}>
          <Route path="/home" element={<DashboardPage user={user} />} />
          <Route path="/tickets" element={<TicketsLayout isAdmin={isAdmin} />}>
            <Route index element={null} />
            <Route path=":id" element={<TicketDetail user={user} isAdmin={isAdmin} />} />
          </Route>
          <Route element={<RequireAdmin isAdmin={isAdmin} />}>
            <Route path="/admin/users" element={<AdminUsersPage />} />
          </Route>
        </Route>
      </Route>
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  )
}

export default App
```

- [ ] **Step 6: Cleanup and page padding**

`git rm src/components/NavBar.tsx src/pages/QueuePage.tsx src/pages/HomePage.tsx`. In `src/pages/AdminUsersPage.tsx` change `<section className="px-8 py-10 text-left">` to `<section className="mx-auto max-w-5xl p-6 text-left">`. Confirm nothing still imports the deleted files (`grep -rn "NavBar\|QueuePage\|HomePage" src`).

- [ ] **Step 7: Verify and commit**

`npm run lint` and `npm run build`. Start `npm run dev` only if a browser check is possible; otherwise rely on the Task 7 manual pass. Commit: `feat: add icon-rail app shell and route guards`.

---

### Task 7: Documentation

**Files:**
- Modify: `CLAUDE.md`

- [ ] **Step 1: Update the Frontend section**

In the "### Frontend" paragraph, replace the sentence listing components used "across `NavBar`/`LandingPage`/`HomePage`/`AdminUsersPage`" with `NavRail`/`TicketsLayout`/`TicketDetail`/`DashboardPage`/`AdminUsersPage`. Add a paragraph "App shell (redesign done)": `RequireAuth` guard -> `AppShell` (56 px icon rail: Dashboard, Queue with unassigned badge and review dot, Mine, All and Users for admins, account menu with System/Light/Dark theme and Sign out) -> pages; `/home` is `DashboardPage` (Unassigned / Mine / Needs review cards and a "Needs your attention" list from the `queue` and `mine` lists, replied tickets never counted); `/tickets` is `TicketsLayout` (340 px list column with Queue/Mine/All tabs, `?filter=` preserved in ticket URLs, detail at `/tickets/:id`; below 1024 px only the list or the detail shows); `TicketsProvider` (src/context) is the only caller of `GET /api/tickets` and `TicketDetail` calls `refresh()` after claim/release/send; `ThemeProvider` follows the OS theme by default with a manual override stored in `localStorage` (key `helpdesk-theme`), applied as the `dark` class on `<html>` (an inline script in `index.html` prevents a light flash); the customer-email iframe stays white. A signed-in user without a registered record sees `AccessNotice` (with Sign out) instead of the shell. Spec: `docs/superpowers/specs/2026-09-27-app-shell-redesign-design.md`; plan: `docs/superpowers/plans/2026-09-27-app-shell-redesign.md`.

- [ ] **Step 2: Fix stale text and add the manual test**

In "### Agent queue and reply", replace `Frontend: \`QueuePage\` (\`/tickets\`) and \`TicketDetailPage\` (\`/tickets/:id\`) with \`src/api/tickets.ts\`` with `Frontend: see "App shell" above (\`TicketsLayout\`, \`TicketDetail\`) with \`src/api/tickets.ts\``. Then append to the App shell paragraph the manual check (needs both servers running and a few tickets): sign in as an agent and as an admin; rail icons, tooltips and admin-only items; Queue/Mine/All; select a ticket, reload `/tickets/:id?filter=mine`, browser Back; claim, edit and send update the row and rail count; dashboard counts and attention list match the queue; theme toggle (System/Light/Dark) with reload persistence; resize below 1024 px (list only, then detail with Back); an HTML email still renders on white in dark mode; `/admin/users` inside the shell; a non-admin opening `/admin/users` lands on `/home`.

- [ ] **Step 3: Verify and commit**

`npm run build` in `client/helpdesk-web` (docs only, must still pass). Commit: `docs: document the app shell redesign`.
