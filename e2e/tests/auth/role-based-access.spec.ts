import { test, expect } from '@playwright/test'
import { BACKEND_URL } from '../../env'
import { mockAuthMe, seedFakeMsalSession } from './msal-mock'

// Role gating must hold at both the UI (hides/redirects) and the API (independently rejects) —
// defense in depth. The UI checks are driven with a mocked /api/auth/me (see msal-mock.ts); the
// API check goes straight at the real backend with no valid token, since we cannot mint a real
// Entra token for an Agent/Admin account without a test IdP (documented gap).

test.describe('Agent role — cannot reach admin-only UI', () => {
  test.beforeEach(async ({ page }) => {
    await seedFakeMsalSession(page, { email: 'agent@example.com', name: 'Agent Example' })
    await mockAuthMe(page, {
      status: 200,
      body: { name: 'Agent Example', email: 'agent@example.com', roles: ['Agent'] },
    })
  })

  test('NavBar does not show "Manage Agents" for an Agent', async ({ page }) => {
    await page.goto('/')
    await expect(page).toHaveURL('/home')
    await expect(page.getByRole('link', { name: 'Manage Agents' })).toHaveCount(0)
  })

  test('direct navigation to /admin/users redirects an Agent back to /home', async ({ page }) => {
    await page.goto('/admin/users')
    await expect(page).toHaveURL('/home')
    await expect(page.getByText(/Welcome/)).toBeVisible()
  })
})

test.describe('Admin role — can reach admin-only UI', () => {
  test.beforeEach(async ({ page }) => {
    await seedFakeMsalSession(page, { email: 'admin@example.com', name: 'Admin Example' })
    await mockAuthMe(page, {
      status: 200,
      body: { name: 'Admin Example', email: 'admin@example.com', roles: ['Admin'] },
    })
    await page.route('**/api/users', async (route) => {
      if (route.request().method() === 'GET') {
        await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' })
        return
      }
      await route.continue()
    })
  })

  test('NavBar shows "Manage Agents" for an Admin', async ({ page }) => {
    await page.goto('/')
    await expect(page).toHaveURL('/home')
    await expect(page.getByRole('link', { name: 'Manage Agents' })).toBeVisible()
  })

  test('an Admin can open /admin/users and see the Add Agent form (via in-app navigation)', async ({
    page,
  }) => {
    await page.goto('/')
    await expect(page).toHaveURL('/home')
    await page.getByRole('link', { name: 'Manage Agents' }).click()
    await expect(page).toHaveURL('/admin/users')
    await expect(page.getByRole('heading', { name: 'Manage Agents' })).toBeVisible()
    await expect(page.getByRole('button', { name: 'Add agent' })).toBeVisible()
  })

  test('a cold deep-link straight to /admin/users as Admin lands on /admin/users, not /home', async ({
    page,
  }) => {
    // Regression test for a real bug: MsalProvider always mounts with inProgress "Startup" and
    // empty accounts (it re-runs initialize()/handleRedirectPromise() itself regardless of
    // main.tsx already having awaited msalInstance.initialize()), so useIsAuthenticated() is
    // forced false for at least one render on every cold page load. App.tsx used to make
    // Navigate decisions off that too-early value, so a deep link to /admin/users bounced
    // through "/" and landed on /home once the real (authenticated, Admin) state settled —
    // losing the originally requested route. Fixed by having App render nothing until MSAL
    // leaves the Startup phase, before any auth-based routing decision is made.
    await page.goto('/admin/users')
    await expect(page).toHaveURL('/admin/users')
    await expect(page.getByRole('heading', { name: 'Manage Agents' })).toBeVisible()
  })
})

test.describe('defense in depth — UI role claims never substitute for real API authorization', () => {
  test('a browser-side "Admin" session with no real Entra token still gets 401 from the real API', async ({
    request,
  }) => {
    // The fake MSAL session above only ever talks to page.route mocks — it is never a real,
    // signed Entra token. Hitting the real backend directly (no Authorization header at all,
    // matching what an attacker who only controls browser storage could produce) proves the API
    // does its own, independent authorization and does not trust anything client-side.
    const response = await request.get(`${BACKEND_URL}/api/users`)
    expect(response.status()).toBe(401)
  })
})
