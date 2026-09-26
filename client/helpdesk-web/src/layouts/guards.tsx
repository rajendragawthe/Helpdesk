import { Navigate, Outlet } from 'react-router'
import type { CurrentUser } from '../hooks/useCurrentUser'
import AccessNotice from '../components/AccessNotice'

type RequireAuthProps = {
  isAuthenticated: boolean
  loading: boolean
  user: CurrentUser | null
  error: string | null
  notRegistered: boolean
}

export function RequireAuth({ isAuthenticated, loading, user, error, notRegistered }: RequireAuthProps) {
  if (!isAuthenticated) {
    return <Navigate to="/" replace />
  }
  if (loading) {
    return null
  }
  if (!user) {
    return <AccessNotice error={error} notRegistered={notRegistered} />
  }
  return <Outlet />
}

export function RequireAdmin({ isAdmin }: { isAdmin: boolean }) {
  return isAdmin ? <Outlet /> : <Navigate to="/home" replace />
}
