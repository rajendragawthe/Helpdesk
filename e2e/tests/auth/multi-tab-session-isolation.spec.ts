import { test, expect } from '@playwright/test'
import { FRONTEND_URL } from '../../env'
import { mockAuthMe, seedFakeMsalSession } from './msal-mock'

// authConfig.ts sets `cache.cacheLocation: 'sessionStorage'` — deliberately not localStorage.
// sessionStorage is scoped per tab/browsing-context in real browsers (and per Playwright Page,
// even within one BrowserContext, since new pages here are not opened via window.open from an
// existing one). The practical consequence: each tab holds its own independent MSAL session, so
// "logging out" (or a token being revoked) in one tab cannot silently flip another, already-open
// tab into a logged-out state — that other tab only finds out on its *own* next API call. These
// tests confirm that is actually true here, rather than assumed.

test('two tabs in the same browser context do not share MSAL session state', async ({
  context,
}) => {
  const tabA = await context.newPage()
  const tabB = await context.newPage()

  await seedFakeMsalSession(tabA, { email: 'tab-a@example.com', name: 'Tab A' })
  await mockAuthMe(tabA, {
    status: 200,
    body: { name: 'Tab A', email: 'tab-a@example.com', roles: ['Agent'] },
  })

  // Tab B never gets a seeded session: it represents "a second tab that was never signed in".
  await tabB.goto(FRONTEND_URL + '/')
  await expect(tabB).toHaveURL('/')
  await expect(tabB.getByRole('button', { name: 'Sign in' })).toBeVisible()

  // Tab A independently has its own working session, unaffected by tab B's lack of one.
  await tabA.goto(FRONTEND_URL + '/')
  await expect(tabA).toHaveURL('/home', { timeout: 15_000 })
  await expect(tabA.getByText(/Welcome, Tab A/)).toBeVisible()

  await tabA.close()
  await tabB.close()
})

test("revoking one tab's session does not affect an independent second tab", async ({
  browser,
}) => {
  // Two fully independent browser contexts (the strongest form of "different tab/session") so
  // each can be driven to a different mocked backend state without their route handlers
  // colliding, reusing the same browser instance Playwright already launched for this project
  // (so this respects any custom launchOptions from playwright.config.ts, e.g.
  // E2E_CHROMIUM_EXECUTABLE_PATH, instead of spawning an unconfigured browser of its own).
  const contextA = await browser.newContext({ baseURL: FRONTEND_URL })
  const contextB = await browser.newContext({ baseURL: FRONTEND_URL })
  try {
    const pageA = await contextA.newPage()
    const pageB = await contextB.newPage()

    await seedFakeMsalSession(pageA, { email: 'revoked@example.com', name: 'Revoked Session' })
    await mockAuthMe(pageA, {
      status: 200,
      body: { name: 'Revoked Session', email: 'revoked@example.com', roles: ['Agent'] },
    })
    await seedFakeMsalSession(pageB, { email: 'still-valid@example.com', name: 'Still Valid' })
    await mockAuthMe(pageB, {
      status: 200,
      body: { name: 'Still Valid', email: 'still-valid@example.com', roles: ['Agent'] },
    })

    await pageA.goto('/')
    await pageB.goto('/')
    await expect(pageA.getByText(/Welcome, Revoked Session/)).toBeVisible()
    await expect(pageB.getByText(/Welcome, Still Valid/)).toBeVisible()

    // Simulate the backend revoking/expiring tab A's session (e.g. an admin removed the user,
    // or the token lapsed) by having its *next* auth/me call start failing, and reload it.
    await pageA.unroute('**/api/auth/me')
    await mockAuthMe(pageA, { status: 403, body: { message: 'revoked' } })
    await pageA.reload()
    await expect(pageA.getByText('Access not set up')).toBeVisible()

    // Tab B was never touched and must still be perfectly functional — one tab's revocation is
    // not a global client-side "log everyone out" event.
    await pageB.reload()
    await expect(pageB.getByText(/Welcome, Still Valid/)).toBeVisible()
  } finally {
    await contextA.close()
    await contextB.close()
  }
})
