import { useMsal } from '@azure/msal-react'
import { Link } from 'react-router'

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
        {isAdmin && (
          <Link to="/admin/users" className="text-[15px] text-text hover:text-text-h">
            Manage Agents
          </Link>
        )}
      </div>
      <button
        type="button"
        onClick={logout}
        className="cursor-pointer rounded-md border-2 border-transparent bg-accent-bg px-4 py-2 text-[15px] text-accent transition-colors hover:border-accent-border focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent"
      >
        Sign out
      </button>
    </nav>
  )
}

export default NavBar
