import type { ReactNode } from 'react'
import { Link, useLocation, useSearchParams } from 'react-router'
import { useMsal } from '@azure/msal-react'
import {
  CircleUser,
  Inbox,
  Layers,
  LayoutDashboard,
  LogOut,
  UserCheck,
  UserCog,
  type LucideIcon,
} from 'lucide-react'
import { cn } from '@/lib/utils'
import type { CurrentUser } from '../hooks/useCurrentUser'
import { useTickets } from '../context/TicketsProvider'
import { useTheme } from './ThemeProvider'
import { hasNeedsReview, parseFilter, unassignedCount } from '../lib/ticketLogic'
import type { ThemePreference } from '../lib/theme'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuRadioGroup,
  DropdownMenuRadioItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'

type RailItem = {
  to: string
  label: string
  icon: LucideIcon
  active: boolean
  badge?: ReactNode
}

const ITEM_CLASS =
  'relative flex size-10 items-center justify-center rounded-lg text-muted-foreground hover:bg-accent hover:text-accent-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring'

type NavRailProps = {
  user: CurrentUser | null
  isAdmin: boolean
}

function NavRail({ user, isAdmin }: NavRailProps) {
  const { instance } = useMsal()
  const { pathname } = useLocation()
  const [searchParams] = useSearchParams()
  const { lists } = useTickets()
  const { preference, setPreference } = useTheme()

  const onTickets = pathname.startsWith('/tickets')
  const activeFilter = parseFilter(searchParams.get('filter'), isAdmin)
  const queueItems = lists.queue.items ?? []
  const unassigned = unassignedCount(queueItems)
  const anyReview = hasNeedsReview(queueItems)

  const items: RailItem[] = [
    { to: '/home', label: 'Dashboard', icon: LayoutDashboard, active: pathname === '/home' },
    {
      to: '/tickets?filter=queue',
      label: `Queue, ${unassigned} unassigned${anyReview ? ', some need review' : ''}`,
      icon: Inbox,
      active: onTickets && activeFilter === 'queue',
      badge: (
        <>
          {unassigned > 0 && (
            <span className="absolute -top-0.5 -right-0.5 min-w-4 rounded-full bg-primary px-1 text-center text-[10px] leading-4 text-primary-foreground">
              {unassigned}
            </span>
          )}
          {anyReview && <span className="absolute right-0.5 bottom-0.5 size-2 rounded-full bg-destructive" aria-hidden="true" />}
        </>
      ),
    },
    { to: '/tickets?filter=mine', label: 'Mine', icon: UserCheck, active: onTickets && activeFilter === 'mine' },
    ...(isAdmin
      ? [
          { to: '/tickets?filter=all', label: 'All tickets', icon: Layers, active: onTickets && activeFilter === 'all' },
          { to: '/admin/users', label: 'Manage agents', icon: UserCog, active: pathname === '/admin/users' },
        ]
      : []),
  ]

  return (
    <nav aria-label="Main" className="flex w-14 shrink-0 flex-col items-center gap-2 border-r border-border bg-sidebar py-3">
      <span className="mb-2 flex size-10 items-center justify-center rounded-lg bg-primary font-heading text-lg font-semibold text-primary-foreground" aria-hidden="true">
        H
      </span>

      {items.map((item) => (
        <Tooltip key={item.to}>
          <TooltipTrigger asChild>
            <Link
              to={item.to}
              aria-label={item.label}
              aria-current={item.active ? 'page' : undefined}
              className={cn(ITEM_CLASS, item.active && 'bg-accent text-accent-foreground')}
            >
              <item.icon className="size-5" aria-hidden="true" />
              {item.badge}
            </Link>
          </TooltipTrigger>
          <TooltipContent side="right">{item.label.split(',')[0]}</TooltipContent>
        </Tooltip>
      ))}

      <div className="mt-auto">
        <DropdownMenu>
          <Tooltip>
            <TooltipTrigger asChild>
              <DropdownMenuTrigger aria-label="Account and theme" className={ITEM_CLASS}>
                <CircleUser className="size-5" aria-hidden="true" />
              </DropdownMenuTrigger>
            </TooltipTrigger>
            <TooltipContent side="right">Account</TooltipContent>
          </Tooltip>
          <DropdownMenuContent side="right" align="end" className="w-52">
            <DropdownMenuLabel className="truncate">{user?.name ?? user?.email ?? 'Signed in'}</DropdownMenuLabel>
            <DropdownMenuSeparator />
            <DropdownMenuLabel className="text-xs font-normal text-muted-foreground">Theme</DropdownMenuLabel>
            <DropdownMenuRadioGroup value={preference} onValueChange={(value) => setPreference(value as ThemePreference)}>
              <DropdownMenuRadioItem value="system">System</DropdownMenuRadioItem>
              <DropdownMenuRadioItem value="light">Light</DropdownMenuRadioItem>
              <DropdownMenuRadioItem value="dark">Dark</DropdownMenuRadioItem>
            </DropdownMenuRadioGroup>
            <DropdownMenuSeparator />
            <DropdownMenuItem onSelect={() => instance.logoutRedirect()}>
              <LogOut className="size-4" aria-hidden="true" />
              Sign out
            </DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
      </div>
    </nav>
  )
}

export default NavRail
