import { Outlet } from 'react-router'
import type { CurrentUser } from '../hooks/useCurrentUser'
import { TicketsProvider } from '../context/TicketsProvider'
import NavRail from '../components/NavRail'

type AppShellProps = {
  user: CurrentUser | null
  isAdmin: boolean
}

function AppShell({ user, isAdmin }: AppShellProps) {
  return (
    <TicketsProvider>
      <div className="flex h-svh grow text-left">
        <NavRail user={user} isAdmin={isAdmin} />
        <main className="min-h-0 min-w-0 flex-1 overflow-y-auto">
          <Outlet />
        </main>
      </div>
    </TicketsProvider>
  )
}

export default AppShell
