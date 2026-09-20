import { Navigate, Route, Routes } from 'react-router'
import { useIsAuthenticated } from '@azure/msal-react'
import NavBar from './components/NavBar'
import LandingPage from './pages/LandingPage'
import HomePage from './pages/HomePage'
import AdminUsersPage from './pages/AdminUsersPage'
import { useCurrentUser } from './hooks/useCurrentUser'

function App() {
  const isAuthenticated = useIsAuthenticated()
  const { user, loading, error, notRegistered } = useCurrentUser()
  const isAdmin = user?.roles.includes('Admin') ?? false

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
    </Routes>
  )
}

export default App
