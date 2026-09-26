import { useCallback, useEffect } from 'react'
import { Outlet, useMatch, useNavigate, useSearchParams } from 'react-router'
import { cn } from '@/lib/utils'
import type { TicketFilter } from '../api/tickets'
import { useTickets } from '../context/TicketsProvider'
import { parseFilter } from '../lib/ticketLogic'
import { EmptyState, ListSkeleton } from '../components/EmptyState'
import { TicketListRow } from '../components/TicketListRow'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs'

const TAB_LABELS: Record<TicketFilter, string> = {
  queue: 'Queue',
  mine: 'Mine',
  all: 'All',
}

type TicketsLayoutProps = {
  isAdmin: boolean
}

function TicketsLayout({ isAdmin }: TicketsLayoutProps) {
  const [searchParams] = useSearchParams()
  const navigate = useNavigate()
  // TicketsLayout is the parent of the ":id" route, so useParams() would not see the id here.
  const match = useMatch('/tickets/:id')
  const selectedId = match?.params.id
  const filter = parseFilter(searchParams.get('filter'), isAdmin)
  const { lists, load } = useTickets()
  const list = lists[filter]

  const reload = useCallback(() => {
    void load(filter)
  }, [load, filter])

  useEffect(() => {
    reload()
  }, [reload])

  const filters: TicketFilter[] = isAdmin ? ['queue', 'mine', 'all'] : ['queue', 'mine']

  const changeFilter = (next: TicketFilter) => {
    navigate(`/tickets${selectedId ? `/${selectedId}` : ''}?filter=${next}`)
  }

  return (
    <div className="flex h-full min-h-0">
      <section
        aria-label="Tickets"
        className={cn(
          'flex w-full min-w-0 flex-col border-r border-border lg:w-[340px] lg:shrink-0',
          selectedId && 'hidden lg:flex',
        )}
      >
        <div className="border-b border-border p-3">
          <Tabs value={filter} onValueChange={(value) => changeFilter(value as TicketFilter)}>
            <TabsList className="w-full">
              {filters.map((f) => (
                <TabsTrigger key={f} value={f} className="flex-1">
                  {TAB_LABELS[f]}
                </TabsTrigger>
              ))}
            </TabsList>
          </Tabs>
        </div>

        <div className="min-h-0 flex-1 overflow-y-auto">
          {list.error && (
            <div className="p-3">
              <Alert variant="destructive">
                <AlertDescription className="flex items-center justify-between gap-3">
                  <span>{list.error}</span>
                  <Button type="button" size="sm" variant="outline" onClick={reload}>
                    Retry
                  </Button>
                </AlertDescription>
              </Alert>
            </div>
          )}
          {list.items === null && !list.error && <ListSkeleton />}
          {list.items !== null && list.items.length === 0 && (
            <EmptyState title="No tickets here" description="New tickets will show up in this list." />
          )}
          {list.items?.map((ticket) => (
            <TicketListRow
              key={ticket.id}
              ticket={ticket}
              to={`/tickets/${ticket.id}?filter=${filter}`}
              selected={ticket.id === selectedId}
            />
          ))}
        </div>
      </section>

      <div className={cn('min-w-0 flex-1 overflow-y-auto', !selectedId && 'hidden lg:block')}>
        {selectedId ? (
          <Outlet />
        ) : (
          <EmptyState title="Select a ticket" description="Pick a ticket from the list to read it and reply." />
        )}
      </div>
    </div>
  )
}

export default TicketsLayout
