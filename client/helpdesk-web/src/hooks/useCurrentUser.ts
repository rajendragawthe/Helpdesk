import { useEffect, useState } from 'react'
import { useMsal } from '@azure/msal-react'
import { apiFetch } from '../api/apiFetch'

export type CurrentUser = {
  name: string | null
  email: string | null
  roles: string[]
}

type UseCurrentUserResult = {
  user: CurrentUser | null
  loading: boolean
  error: string | null
}

export function useCurrentUser(): UseCurrentUserResult {
  const { instance, accounts } = useMsal()
  const [user, setUser] = useState<CurrentUser | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false

    async function load() {
      if (accounts[0]) {
        instance.setActiveAccount(accounts[0])
      }
      setLoading(true)
      try {
        const response = await apiFetch(instance, '/api/auth/me')
        if (!response.ok) {
          throw new Error(`Request failed: ${response.status} ${response.statusText}`)
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

  return { user, loading, error }
}
