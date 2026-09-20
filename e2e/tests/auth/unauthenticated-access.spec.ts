import { test, expect } from '@playwright/test'
import { BACKEND_URL } from '../../env'

// An unauthenticated caller (no MSAL account in the browser cache, no bearer token on the API)
// must be turned back cleanly at both layers: the SPA never renders a protected route, and the
// API never leaks data behind a missing/absent Authorization header.

test.describe('unauthenticated access — frontend routing', () => {
  test('landing page renders sign-in, not a blank/broken page', async ({ page }) => {
    await page.goto('/')
    await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible()
    await expect(page.getByRole('heading', { name: /AI-assisted support/i })).toBeVisible()
  })

  test('direct navigation to /home redirects to the landing page', async ({ page }) => {
    await page.goto('/home')
    await expect(page).toHaveURL('/')
    await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible()
  })

  test('direct navigation to /admin/users redirects to the landing page', async ({ page }) => {
    await page.goto('/admin/users')
    await expect(page).toHaveURL('/')
    await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible()
  })

  test('clicking "Sign in" starts a real Entra ID redirect, not a client-side no-op', async ({
    page,
  }) => {
    await page.goto('/')
    // We deliberately do not let this reach a real login form (no test IdP — see
    // e2e/README.md). Intercepting the authorize request is enough to prove loginRedirect()
    // actually attempts a real MSAL redirect rather than silently doing nothing or crashing.
    const authorizeRequest = page.waitForRequest((req) =>
      req.url().includes('login.microsoftonline.com') && req.url().includes('/oauth2/v2.0/authorize'),
    )
    await page.getByRole('button', { name: 'Sign in' }).click()
    const request = await authorizeRequest
    expect(request.url()).toContain('client_id=')
  })
})

test.describe('unauthenticated access — API', () => {
  test('GET /api/auth/me with no Authorization header is rejected, not served', async ({
    request,
  }) => {
    const response = await request.get(`${BACKEND_URL}/api/auth/me`)
    expect(response.status()).toBe(401)
  })

  test('GET /api/users (AdminOnly) with no Authorization header is rejected', async ({
    request,
  }) => {
    const response = await request.get(`${BACKEND_URL}/api/users`)
    expect(response.status()).toBe(401)
  })

  test('POST /api/users (AdminOnly) with no Authorization header is rejected', async ({
    request,
  }) => {
    const response = await request.post(`${BACKEND_URL}/api/users`, {
      data: { email: 'nobody@example.com', displayName: 'Nobody' },
    })
    expect(response.status()).toBe(401)
  })
})
