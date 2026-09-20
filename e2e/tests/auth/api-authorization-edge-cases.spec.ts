import { test, expect, type APIRequestContext } from '@playwright/test'
import { BACKEND_URL } from '../../env'

// Malformed/tampered-credential edge cases against the real API (no MSAL/browser involved).
// Every one of these must be rejected by JWT bearer validation before it ever reaches
// HelpdeskUserClaimsTransformation or a controller — a syntactically-odd Authorization header
// should never crash the claims pipeline into a 500, and should never be treated as valid.

function base64url(json: unknown): string {
  return Buffer.from(JSON.stringify(json))
    .toString('base64')
    .replace(/=+$/, '')
    .replace(/\+/g, '-')
    .replace(/\//g, '_')
}

const malformedHeaders: Record<string, string> = {
  'wrong scheme (Basic)': 'Basic dXNlcjpwYXNz',
  'bearer with no token': 'Bearer',
  'bearer with empty token': 'Bearer ',
  'bearer with a plain non-JWT string': 'Bearer not-a-jwt-at-all',
  'bearer with only two dot-separated segments': 'Bearer eyJhbGciOiJub25lIn0.eyJzdWIiOiJ4In0',
  'bearer with structurally valid but wrong-signature JWT': `Bearer ${base64url({
    alg: 'HS256',
    typ: 'JWT',
  })}.${base64url({ sub: 'e2e-garbage', exp: 9999999999 })}.tamperedsignature`,
  'bearer with alg:none unsigned JWT (algorithm-confusion attempt)': `Bearer ${base64url({
    alg: 'none',
    typ: 'JWT',
  })}.${base64url({ sub: 'e2e-garbage', roles: ['Admin'], exp: 9999999999 })}.`,
  'bearer with a huge garbage token': `Bearer ${'a'.repeat(20000)}`,
}

async function expectRejected(request: APIRequestContext, path: string, header: string) {
  const response = await request.get(`${BACKEND_URL}${path}`, {
    headers: { Authorization: header },
    failOnStatusCode: false,
  })
  // Must be an auth rejection, never a crash and never treated as authenticated.
  expect([401]).toContain(response.status())
}

test.describe('malformed/tampered Authorization headers', () => {
  for (const [label, header] of Object.entries(malformedHeaders)) {
    test(`GET /api/auth/me — ${label} — is rejected with 401, not 500`, async ({ request }) => {
      await expectRejected(request, '/api/auth/me', header)
    })

    test(`GET /api/users (AdminOnly) — ${label} — is rejected with 401 before any role check`, async ({
      request,
    }) => {
      await expectRejected(request, '/api/users', header)
    })
  }
})
