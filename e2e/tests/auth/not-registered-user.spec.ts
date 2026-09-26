import { test, expect } from '@playwright/test'
import { mockAuthMe, seedFakeMsalSession } from './msal-mock'

// Drives the "authenticated in Entra ID but no matching Users row" flow end to end at the
// frontend: AuthController.Me() returns 403 with a specific message in this case (see
// src/Helpdesk.Api/Controllers/AuthController.cs), and useCurrentUser/AccessNotice must surface it
// as a clear message, not hang on a blank/loading page. The real backend claims-transformation
// logic that produces this 403 (HelpdeskUserClaimsTransformation) cannot be driven from here
// without a real Entra-issued token — see the note in README/report about that gap; this test
// covers the frontend's handling of the response, mocked at the network boundary.

const NOT_REGISTERED_MESSAGE =
  "Your account isn't registered in Helpdesk yet. Ask an administrator to add you as a user."

test('shows the "not registered" message instead of a blank or crashed page', async ({ page }) => {
  await seedFakeMsalSession(page, { email: 'ghost.user@example.com', name: 'Ghost User' })
  await mockAuthMe(page, { status: 403, body: { message: NOT_REGISTERED_MESSAGE } })

  await page.goto('/')
  await expect(page).toHaveURL('/home')

  await expect(page.getByText('Access not set up')).toBeVisible()
  await expect(page.getByText(NOT_REGISTERED_MESSAGE)).toBeVisible()

  // The app shell must not mount for an unregistered user: no nav rail alongside the alert.
  await expect(page.locator('nav[aria-label="Main"]')).toHaveCount(0)
})

test('a not-registered user gets no admin affordance even if somehow on /admin/users', async ({
  page,
}) => {
  await seedFakeMsalSession(page, { email: 'ghost.user2@example.com', name: 'Ghost User 2' })
  await mockAuthMe(page, { status: 403, body: { message: NOT_REGISTERED_MESSAGE } })

  await page.goto('/admin/users')
  // RequireAuth renders a standalone AccessNotice on the requested path (no redirect) when
  // there is no `user`, so the admin page and the app shell never mount.
  await expect(page).toHaveURL('/admin/users')
  await expect(page.getByText('Access not set up')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible()
})
