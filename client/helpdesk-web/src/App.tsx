import { Navigate, Route, Routes } from 'react-router'
import { useIsAuthenticated, useMsal } from '@azure/msal-react'
import { InteractionStatus } from '@azure/msal-browser'
import NavBar from './components/NavBar'
import LandingPage from './pages/LandingPage'
import HomePage from './pages/HomePage'
import AdminUsersPage from './pages/AdminUsersPage'
import QueuePage from './pages/QueuePage'
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
        path="/home"
        element={
          isAuthenticated ? (
            <>
              <NavBar isAdmin={isAdmin} />
              <HomePage user={user} error={error} notRegistered={notRegistered} />
            </>
          ) : (
            <Navigate to="/" replace />
          )
        }
      />
      <Route
        path="/admin/users"
        element={
          !isAuthenticated ? (
            <Navigate to="/" replace />
          ) : loading ? null : isAdmin ? (
            <>
              <NavBar isAdmin={isAdmin} />
              <AdminUsersPage />
            </>
          ) : (
            <Navigate to="/home" replace />
          )
        }
      />
      <Route
        path="/tickets"
        element={
          !isAuthenticated ? (
            <Navigate to="/" replace />
          ) : loading ? null : user ? (
            <>
              <NavBar isAdmin={isAdmin} />
              <QueuePage isAdmin={isAdmin} />
            </>
          ) : (
            <Navigate to="/home" replace />
          )
        }
      />
    </Routes>
  )
}

export default App
