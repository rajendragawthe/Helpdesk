import { useCallback, useEffect } from 'react'
import { Link } from 'react-router'
import { useMsal } from '@azure/msal-react'
import type { CurrentUser } from '../hooks/useCurrentUser'
import { useTickets } from '../context/TicketsProvider'
import { buildDashboard } from '../lib/ticketLogic'
import { TicketListRow } from '../components/TicketListRow'
import { EmptyState, ListSkeleton } from '../components/EmptyState'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'

type DashboardPageProps = {
  user: CurrentUser | null
}

function DashboardPage({ user }: DashboardPageProps) {
  const { accounts } = useMsal()
  const { lists, load } = useTickets()
  const displayName = user?.name ?? accounts[0]?.name ?? accounts[0]?.username ?? ''

  const reload = useCallback(() => {
    void load('queue')
    void load('mine')
  }, [load])

  useEffect(() => {
    reload()
  }, [reload])

  const error = lists.queue.error ?? lists.mine.error
  const queue = lists.queue.items
  const mine = lists.mine.items
  const data = queue && mine ? buildDashboard(queue, mine) : null
  const loading = !error && data === null

  const cards = data
    ? [
        { label: 'Unassigned', value: data.unassigned, to: '/tickets?filter=queue', danger: false },
        { label: 'Mine', value: data.mine, to: '/tickets?filter=mine', danger: false },
        { label: 'Needs review', value: data.needsReview, to: '/tickets?filter=queue', danger: data.needsReview > 0 },
      ]
    : []

  return (
    <section className="mx-auto flex max-w-4xl flex-col gap-6 p-6 text-left">
      <h1 className="font-heading text-2xl font-semibold text-text-h">
        Welcome{displayName ? `, ${displayName}` : ''}
      </h1>

      {error && (
        <Alert variant="destructive">
          <AlertDescription className="flex items-center justify-between gap-4">
            <span>{error}</span>
            <Button type="button" size="sm" variant="outline" onClick={reload}>
              Retry
            </Button>
          </AlertDescription>
        </Alert>
      )}

      {loading && <ListSkeleton rows={4} />}

      {data && (
        <>
          <div className="grid gap-4 sm:grid-cols-3">
            {cards.map((card) => (
              <Link key={card.label} to={card.to} className="block focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring rounded-xl">
                <Card className={card.danger ? 'border-destructive/50' : undefined}>
                  <CardHeader>
                    <CardTitle className="text-sm font-medium text-muted-foreground">{card.label}</CardTitle>
                  </CardHeader>
                  <CardContent className="text-3xl font-semibold">{card.value}</CardContent>
                </Card>
              </Link>
            ))}
          </div>

          <Card>
            <CardHeader>
              <CardTitle>Needs your attention</CardTitle>
            </CardHeader>
            <CardContent className="p-0">
              {data.attention.length === 0 ? (
                <EmptyState title="Nothing needs your attention" description="New and flagged tickets will appear here." />
              ) : (
                data.attention.map((ticket) => (
                  <TicketListRow key={ticket.id} ticket={ticket} to={`/tickets/${ticket.id}?filter=queue`} />
                ))
              )}
            </CardContent>
          </Card>
        </>
      )}
    </section>
  )
}

export default DashboardPage
