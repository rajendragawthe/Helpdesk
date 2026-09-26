import { Link } from 'react-router'
import { formatDistanceToNow } from 'date-fns'
import { cn } from '@/lib/utils'
import { Badge } from '@/components/ui/badge'
import type { TicketListItem } from '../api/tickets'
import { ReviewBadge, StatusBadge } from './StatusBadge'

function formatAge(value: string): string {
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '—' : formatDistanceToNow(date, { addSuffix: true })
}

type TicketListRowProps = {
  ticket: TicketListItem
  to: string
  selected?: boolean
}

export function TicketListRow({ ticket, to, selected = false }: TicketListRowProps) {
  return (
    <Link
      to={to}
      aria-current={selected ? 'true' : undefined}
      className={cn(
        'flex flex-col gap-1 border-b border-border px-4 py-3 text-left text-sm hover:bg-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
        selected && 'bg-accent',
      )}
    >
      <span className="truncate font-medium text-foreground">{ticket.subject}</span>
      <span className="truncate text-xs text-muted-foreground">{ticket.requesterEmail}</span>
      <span className="flex flex-wrap items-center gap-1.5">
        <StatusBadge status={ticket.status} />
        {ticket.needsReview && <ReviewBadge />}
        {ticket.category && <Badge variant="outline">{ticket.category}</Badge>}
      </span>
      <span className="flex justify-between gap-2 text-xs text-muted-foreground">
        <span className="truncate">{ticket.assignee?.displayName ?? 'Unassigned'}</span>
        <span className="shrink-0">{formatAge(ticket.createdAt)}</span>
      </span>
    </Link>
  )
}
