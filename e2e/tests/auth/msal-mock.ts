import type { Page } from '@playwright/test'
import { AZURE_AD } from '../../env'

/**
 * Fabricates a working (but entirely fake) MSAL browser cache session, so a page loads with
 * `useIsAuthenticated()` already true and `apiFetch`'s `acquireTokenSilent` resolves an access
 * token straight from cache, with no real Entra ID login and no network call to Microsoft.
 *
 * There is no test identity provider for this app (see e2e/README.md's "Known gap: Entra ID
 * auth" section) — a real `loginRedirect()` requires an interactive login against
 * login.microsoftonline.com with real org credentials. This helper is the alternative the
 * qa-engineer brief calls out: seed the exact sessionStorage shape MSAL itself reads
 * (`authConfig.ts` sets `cache.cacheLocation: 'sessionStorage'`), verified empirically against
 * @azure/msal-browser's cache-key generation (BrowserCacheManager.generateAccountKey /
 * generateCredentialKey — msal.<schema>|<homeAccountId>|<environment>|... joined by "|") rather
 * than assumed. The access token's `secret` is just a placeholder string — MSAL's own cache
 * lookup never validates it (that's the server's job), so downstream requests are always routed
 * through `page.route()` mocks of `/api/*` for the tests that use this helper; the backend never
 * actually sees this fake token, and no production auth code is bypassed to make this work.
 *
 * IMPORTANT: this reaches into @azure/msal-browser's internal cache schema, which is not a
 * public contract. If a future MSAL upgrade changes the cache key format, tests using this
 * helper will start failing at the "isAuthenticated" assertion stage (not silently pass) — that
 * is the intended failure mode, and the fix is to re-derive the key format from
 * BrowserCacheManager.generateAccountKey/generateCredentialKey again.
 */
export type FakeMsalAccountOptions = {
  /** Becomes the MSAL account's username/preferred_username, and is not sent to any real IdP. */
  email: string
  name?: string
}

export async function seedFakeMsalSession(
  page: Page,
  { email, name = email }: FakeMsalAccountOptions,
): Promise<void> {
  const { tenantId, clientId, apiScope } = AZURE_AD
  // Environment alias MSAL's public-cloud instance discovery resolves login.microsoftonline.com
  // to, and therefore what real cache entries would use — kept as a literal here since this
  // helper never performs the real discovery network call.
  const environment = 'login.windows.net'

  await page.addInitScript(
    ({ tenantId, clientId, apiScope, environment, email, name }) => {
      const uid = crypto.randomUUID()
      const homeAccountId = `${uid}.${tenantId}`
      const nowSeconds = Math.floor(Date.now() / 1000)
      const expiresOn = String(nowSeconds + 3600)

      const accountEntity = {
        homeAccountId,
        environment,
        realm: tenantId,
        localAccountId: uid,
        username: email,
        authorityType: 'MSSTS',
        name,
        clientInfo: '',
        idTokenClaims: { preferred_username: email, name, oid: uid, tid: tenantId },
      }
      const accessTokenEntity = {
        homeAccountId,
        environment,
        credentialType: 'AccessToken',
        clientId,
        secret: 'e2e-fake-access-token-never-sent-to-a-real-idp',
        realm: tenantId,
        target: apiScope,
        tokenType: 'Bearer',
        cachedAt: String(nowSeconds),
        expiresOn,
        extendedExpiresOn: expiresOn,
      }

      const accountKey = `msal.3|${homeAccountId}|${environment}|${tenantId}`.toLowerCase()
      const accessTokenKey =
        `msal.3|${homeAccountId}|${environment}|AccessToken|${clientId}|${tenantId}|${apiScope}|`.toLowerCase()

      sessionStorage.setItem(accountKey, JSON.stringify(accountEntity))
      sessionStorage.setItem(accessTokenKey, JSON.stringify(accessTokenEntity))
      sessionStorage.setItem('msal.3.account.keys', JSON.stringify([accountKey]))
      sessionStorage.setItem(
        `msal.3.token.keys.${clientId}`,
        JSON.stringify({ idToken: [], accessToken: [accessTokenKey], refreshToken: [] }),
      )
    },
    { tenantId, clientId, apiScope, environment, email, name },
  )
}

/** Shape returned by `GET /api/auth/me` — see AuthController.Me(). */
export type FakeAuthMeResponse = {
  name: string | null
  email: string | null
  roles: string[]
}

/** Mocks `GET /api/auth/me` so the app can be driven through a given role/registration state. */
export async function mockAuthMe(
  page: Page,
  response:
    | { status: 200; body: FakeAuthMeResponse }
    | { status: 403 | 401 | number; body: { message: string } },
): Promise<void> {
  await page.route('**/api/auth/me', async (route) => {
    await route.fulfill({
      status: response.status,
      contentType: 'application/json',
      body: JSON.stringify(response.body),
    })
  })
}
