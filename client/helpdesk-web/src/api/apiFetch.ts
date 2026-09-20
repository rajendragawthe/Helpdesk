import type { IPublicClientApplication } from '@azure/msal-browser'
import { InteractionRequiredAuthError } from '@azure/msal-browser'
import { apiScopes } from '../authConfig'

export async function apiFetch(
  msalInstance: IPublicClientApplication,
  path: string,
  init: RequestInit = {},
): Promise<Response> {
  const account = msalInstance.getActiveAccount()
  if (!account) {
    throw new Error('No active account: user must be signed in before calling apiFetch')
  }

  let token: string
  try {
    const result = await msalInstance.acquireTokenSilent({ scopes: apiScopes, account })
    token = result.accessToken
  } catch (error) {
    if (error instanceof InteractionRequiredAuthError) {
      await msalInstance.acquireTokenRedirect({ scopes: apiScopes, account })
      throw new Error('Redirecting for interactive sign-in')
    }
    throw error
  }

  return fetch(path, {
    ...init,
    headers: {
      ...init.headers,
      Authorization: `Bearer ${token}`,
    },
  })
}
