import { useMsal } from '@azure/msal-react'
import { Link } from 'react-router'
import { Button } from '@/components/ui/button'

type NavBarProps = {
  isAdmin: boolean
}

function NavBar({ isAdmin }: NavBarProps) {
  const { instance } = useMsal()

  const logout = () => instance.logoutRedirect()

  return (
    <nav className="flex items-center justify-between border-b border-border px-8 py-4">
      <div className="flex items-center gap-6">
        <span className="font-heading text-xl font-semibold text-text-h">Helpdesk</span>
        <Link to="/tickets" className="text-[15px] text-text hover:text-text-h">
          Tickets
        </Link>
        {isAdmin && (
          <Link to="/admin/users" className="text-[15px] text-text hover:text-text-h">
            Manage Agents
          </Link>
        )}
      </div>
      <Button type="button" variant="ghost" onClick={logout}>
        Sign out
      </Button>
    </nav>
  )
}

export default NavBar
