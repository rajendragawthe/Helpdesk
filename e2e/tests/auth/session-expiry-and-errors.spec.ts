import { test, expect } from '@playwright/test'
import { mockAuthMe, seedFakeMsalSession } from './msal-mock'

// apiFetch (client/helpdesk-web/src/api/apiFetch.ts) has no centralized 401 interceptor: it
// acquires a token silently and returns whatever the fetch response is, unchanged. Each caller
// (useCurrentUser, AdminUsersPage) is responsible for turning a non-ok response into its own
// error state. These tests confirm that "responsible for it" actually means "does it", not
// "silently ignores it" — a real gap worth flagging is that none of these produce a "please sign
// in again" prompt, just a generic request-failed message (see report).

test('mid-session 401 on a later API call shows a clear error, not a hang or silent failure', async ({
  page,
}) => {
  await seedFakeMsalSession(page, { email: 'admin-expiring@example.com', name: 'Admin Expiring' })
  await mockAuthMe(page, {
    status: 200,
    body: { name: 'Admin Expiring', email: 'admin-expiring@example.com', roles: ['Admin'] },
  })
  // Simulates the token having expired/been revoked partway through the session: the initial
  // /api/auth/me succeeds (above), but the next authenticated call the Admin page makes fails.
  await page.route('**/api/users', async (route) => {
    if (route.request().method() === 'GET') {
      await route.fulfill({
        status: 401,
        contentType: 'application/json',
        body: JSON.stringify({ message: 'Unauthorized' }),
      })
      return
    }
    await route.continue()
  })

  await page.goto('/')
  await expect(page).toHaveURL('/home')
  // Navigated to the admin page via the in-app link (client-side route change) rather than a
  // cold deep-link to /admin/users, since App.tsx's admin-route guard has a render race on a
  // fresh page load before MSAL's account list settles (a real bug found while writing this
  // suite — see the report) that is unrelated to this test's actual concern (401 handling).
  await page.getByRole('link', { name: 'Manage Agents' }).click()

  // AdminUsersPage.loadUsers() must surface this as a visible error, not leave the table
  // silently empty with no explanation and not crash the page.
  await expect(page.getByText(/Request failed: 401/)).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Manage Agents' })).toBeVisible()
})

test('network failure on /api/auth/me at load surfaces an error, not an infinite silent wait', async ({
  page,
}) => {
  await seedFakeMsalSession(page, { email: 'flaky-network@example.com', name: 'Flaky Network' })
  await page.route('**/api/auth/me', async (route) => {
    await route.abort('connectionreset')
  })

  await page.goto('/')
  await expect(page).toHaveURL('/home')

  // useCurrentUser's catch branch sets `error` to the thrown fetch error's message. We don't
  // assert the exact browser-specific wording (it varies), just that *some* visible error
  // surfaces within a bounded time — i.e. the page doesn't sit there forever giving no feedback.
  const errorAlert = page.getByRole('alert').first()
  await expect(errorAlert).toBeVisible({ timeout: 10_000 })
})

test('a slow (but not failing) /api/auth/me does not produce a broken interim state', async ({
  page,
}) => {
  await seedFakeMsalSession(page, { email: 'slow-network@example.com', name: 'Slow Network' })
  await page.route('**/api/auth/me', async (route) => {
    await new Promise((resolve) => setTimeout(resolve, 2000))
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        name: 'Slow Network',
        email: 'slow-network@example.com',
        roles: ['Agent'],
      }),
    })
  })

  await page.goto('/')
  await expect(page).toHaveURL('/home')
  // While the request is in flight there is no error and no crash — just eventually correct
  // content once it resolves.
  await expect(page.getByText(/Welcome, Slow Network/)).toBeVisible({ timeout: 10_000 })
})
