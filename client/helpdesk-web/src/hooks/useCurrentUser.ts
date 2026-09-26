import { useEffect, useState } from 'react'
import { useMsal } from '@azure/msal-react'
import { apiFetch } from '../api/apiFetch'

export type CurrentUser = {
  id: string | null
  name: string | null
  email: string | null
  roles: string[]
}

type UseCurrentUserResult = {
  user: CurrentUser | null
  loading: boolean
  error: string | null
  notRegistered: boolean
}

export function useCurrentUser(): UseCurrentUserResult {
  const { instance, accounts } = useMsal()
  const [user, setUser] = useState<CurrentUser | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [notRegistered, setNotRegistered] = useState(false)

  useEffect(() => {
    let cancelled = false

    async function load() {
      if (!accounts[0]) {
        // MSAL hasn't hydrated an account yet — e.g. MsalProvider is still resolving the
        // cached session on a cold page load, briefly reporting no accounts even though a
        // session exists. This isn't "not signed in", just not ready yet: stay in the
        // loading state and let this effect re-run once `accounts` is populated, rather
        // than calling apiFetch (which throws "no active account") and reporting a false
        // error/not-registered state for what is actually a timing gap.
        setLoading(true)
        return
      }
      instance.setActiveAccount(accounts[0])
      setLoading(true)
      setError(null)
      setNotRegistered(false)
      try {
        const response = await apiFetch(instance, '/api/auth/me')
        if (!response.ok) {
          const body = await response.json().catch(() => null) as { message?: string } | null
          const message = body?.message ?? `Request failed: ${response.status} ${response.statusText}`
          if (!cancelled) {
            setNotRegistered(response.status === 403)
            setError(message)
          }
          return
        }
        const data = (await response.json()) as CurrentUser
        if (!cancelled) {
          setUser(data)
        }
      } catch (err) {
        if (!cancelled) {
          setError(err instanceof Error ? err.message : 'Unknown error')
        }
      } finally {
        if (!cancelled) {
          setLoading(false)
        }
      }
    }

    load()
    return () => {
      cancelled = true
    }
  }, [instance, accounts])

  return { user, loading, error, notRegistered }
}
