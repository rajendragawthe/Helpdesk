import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router'
import { useMsal } from '@azure/msal-react'
import { formatDistanceToNow } from 'date-fns'
import { apiFetch } from '../api/apiFetch'
import { errorMessage, type TicketFilter, type TicketListItem, type TicketStatus } from '../api/tickets'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs'

type QueuePageProps = {
  isAdmin: boolean
}

const TAB_LABELS: Record<TicketFilter, string> = {
  queue: 'Queue',
  mine: 'Mine',
  all: 'All',
}

function statusVariant(status: TicketStatus): 'default' | 'secondary' | 'outline' {
  if (status === 'Replied') return 'secondary'
  if (status === 'InReview') return 'default'
  return 'outline'
}

function QueuePage({ isAdmin }: QueuePageProps) {
  const { instance } = useMsal()
  const [filter, setFilter] = useState<TicketFilter>('queue')
  const [tickets, setTickets] = useState<TicketListItem[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    async (which: TicketFilter) => {
      setError(null)
      setTickets(null)
      try {
        const response = await apiFetch(instance, `/api/tickets?filter=${which}`)
        if (!response.ok) {
          throw new Error(await errorMessage(response))
        }
        setTickets((await response.json()) as TicketListItem[])
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Unknown error')
      }
    },
    [instance],
  )

  useEffect(() => {
    load(filter)
  }, [filter, load])

  const filters: TicketFilter[] = isAdmin ? ['queue', 'mine', 'all'] : ['queue', 'mine']

  return (
    <main className="mx-auto max-w-6xl px-8 py-8">
      <Card>
        <CardHeader>
          <CardTitle>Tickets</CardTitle>
          <Tabs value={filter} onValueChange={(value) => setFilter(value as TicketFilter)}>
            <TabsList>
              {filters.map((f) => (
                <TabsTrigger key={f} value={f}>
                  {TAB_LABELS[f]}
                </TabsTrigger>
              ))}
            </TabsList>
          </Tabs>
        </CardHeader>
        <CardContent>
          {error && (
            <Alert variant="destructive">
              <AlertDescription>{error}</AlertDescription>
            </Alert>
          )}
          {!error && tickets === null && <p className="text-sm text-muted-foreground">Loading…</p>}
          {!error && tickets !== null && tickets.length === 0 && (
            <p className="text-sm text-muted-foreground">No tickets here.</p>
          )}
          {!error && tickets !== null && tickets.length > 0 && (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Subject</TableHead>
                  <TableHead>From</TableHead>
                  <TableHead>Status</TableHead>
                  <TableHead>Category</TableHead>
                  <TableHead>Summary</TableHead>
                  <TableHead>Assignee</TableHead>
                  <TableHead>Age</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {tickets.map((ticket) => (
                  <TableRow key={ticket.id}>
                    <TableCell className="font-medium">
                      <Link to={`/tickets/${ticket.id}`} className="hover:underline">
                        {ticket.subject}
                      </Link>
                    </TableCell>
                    <TableCell>{ticket.requesterEmail}</TableCell>
                    <TableCell>
                      <Badge variant={statusVariant(ticket.status)}>{ticket.status}</Badge>
                    </TableCell>
                    <TableCell>
                      {ticket.category ? <Badge variant="outline">{ticket.category}</Badge> : '—'}
                    </TableCell>
                    <TableCell className="max-w-xs truncate">{ticket.summary ?? '—'}</TableCell>
                    <TableCell>{ticket.assignee?.displayName ?? 'Unassigned'}</TableCell>
                    <TableCell>{formatDistanceToNow(new Date(ticket.createdAt), { addSuffix: true })}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </CardContent>
      </Card>
    </main>
  )
}

export default QueuePage
