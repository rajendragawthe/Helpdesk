// client/helpdesk-web/src/components/AuthPanel.tsx
import { useState } from 'react'
import { useMsal, useIsAuthenticated } from '@azure/msal-react'
import { apiFetch } from '../api/apiFetch'

type MeResponse = {
  name: string | null
  email: string | null
  roles: string[]
}

function AuthPanel() {
  const { instance, accounts } = useMsal()
  const isAuthenticated = useIsAuthenticated()
  const [me, setMe] = useState<MeResponse | null>(null)
  const [error, setError] = useState<string | null>(null)

  const login = () => instance.loginRedirect()
  const logout = () => instance.logoutRedirect()

  const callMe = async () => {
    setError(null)
    try {
      if (accounts[0]) {
        instance.setActiveAccount(accounts[0])
      }
      const response = await apiFetch(instance, '/api/auth/me')
      if (!response.ok) {
        throw new Error(`Request failed: ${response.status}`)
      }
      setMe(await response.json())
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unknown error')
    }
  }

  return (
    <section id="auth-panel">
      {isAuthenticated ? (
        <>
          <button type="button" onClick={logout}>Sign out</button>
          <button type="button" onClick={callMe}>Call /api/auth/me</button>
        </>
      ) : (
        <button type="button" onClick={login}>Sign in</button>
      )}
      {me && <pre>{JSON.stringify(me, null, 2)}</pre>}
      {error && <p role="alert">{error}</p>}
    </section>
  )
}

export default AuthPanel
