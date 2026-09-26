import { Navigate, Route, Routes } from 'react-router'
import { useIsAuthenticated, useMsal } from '@azure/msal-react'
import { InteractionStatus } from '@azure/msal-browser'
import LandingPage from './pages/LandingPage'
import DashboardPage from './pages/DashboardPage'
import AdminUsersPage from './pages/AdminUsersPage'
import TicketDetail from './pages/TicketDetail'
import AppShell from './layouts/AppShell'
import TicketsLayout from './layouts/TicketsLayout'
import { RequireAdmin, RequireAuth } from './layouts/guards'
import { useCurrentUser } from './hooks/useCurrentUser'

function App() {
  const { inProgress } = useMsal()
  const isAuthenticated = useIsAuthenticated()
  const { user, loading, error, notRegistered } = useCurrentUser()
  const isAdmin = user?.roles.includes('Admin') ?? false

  // MsalProvider always mounts with inProgress "Startup" and empty accounts, even though
  // msalInstance.initialize() already resolved in main.tsx — it re-runs initialize()/
  // handleRedirectPromise() itself and only flips to "None" (with accounts populated from
  // cache) once that settles, one render tick after the first paint. useIsAuthenticated()
  // is hard-coded to return false during Startup, so deciding a route's Navigate off it at
  // this point would treat every cold page load as unauthenticated and redirect a deep link
  // (e.g. /admin/users) away before the real cached session is known — bouncing it through
  // "/" and landing on /home instead of the requested route. Wait out Startup first.
  if (inProgress === InteractionStatus.Startup) {
    return null
  }

  return (
    <Routes>
      <Route
        path="/"
        element={isAuthenticated ? <Navigate to="/home" replace /> : <LandingPage />}
      />
      <Route
        element={
          <RequireAuth
            isAuthenticated={isAuthenticated}
            loading={loading}
            user={user}
            error={error}
            notRegistered={notRegistered}
          />
        }
      >
        <Route element={<AppShell user={user} isAdmin={isAdmin} />}>
          <Route path="/home" element={<DashboardPage user={user} />} />
          <Route path="/tickets" element={<TicketsLayout isAdmin={isAdmin} />}>
            <Route index element={null} />
            <Route path=":id" element={<TicketDetail user={user} isAdmin={isAdmin} />} />
          </Route>
          <Route element={<RequireAdmin isAdmin={isAdmin} />}>
            <Route path="/admin/users" element={<AdminUsersPage />} />
          </Route>
        </Route>
      </Route>
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  )
}

export default App
