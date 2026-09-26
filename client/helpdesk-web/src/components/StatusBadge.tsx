import { Flag } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import type { TicketStatus } from '../api/tickets'

function statusVariant(status: TicketStatus): 'default' | 'secondary' | 'outline' {
  if (status === 'Replied') return 'secondary'
  if (status === 'InReview') return 'default'
  return 'outline'
}

export function StatusBadge({ status }: { status: TicketStatus }) {
  return <Badge variant={statusVariant(status)}>{status}</Badge>
}

export function ReviewBadge() {
  return (
    <Badge variant="destructive" className="gap-1">
      <Flag className="size-3" aria-hidden="true" />
      Review
    </Badge>
  )
}
