import { test, expect } from '@playwright/test'
import { mockAuthMe, seedFakeMsalSession } from './msal-mock'

// Drives the "authenticated in Entra ID but no matching Users row" flow end to end at the
// frontend: AuthController.Me() returns 403 with a specific message in this case (see
// src/Helpdesk.Api/Controllers/AuthController.cs), and useCurrentUser/HomePage must surface it
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

  // The "welcome" happy-path content must not also render alongside the not-registered alert.
  await expect(page.getByText(/Your ticket queue will show up here/)).not.toBeVisible()
})

test('a not-registered user gets no admin affordance even if somehow on /admin/users', async ({
  page,
}) => {
  await seedFakeMsalSession(page, { email: 'ghost.user2@example.com', name: 'Ghost User 2' })
  await mockAuthMe(page, { status: 403, body: { message: NOT_REGISTERED_MESSAGE } })

  await page.goto('/admin/users')
  // App.tsx only allows /admin/users through when `user?.roles.includes('Admin')` — a
  // not-registered caller has no `user` at all, so it must bounce to /home, not render the
  // admin page or hang on it.
  await expect(page).toHaveURL('/home')
  await expect(page.getByText('Access not set up')).toBeVisible()
})
