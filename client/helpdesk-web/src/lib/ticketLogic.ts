import type { TicketFilter, TicketListItem } from '../api/tickets'

export const ATTENTION_LIMIT = 8

/** Reads the queue filter from the URL; `all` is admin-only, anything unknown means `queue`. */
export function parseFilter(value: string | null, isAdmin: boolean): TicketFilter {
  if (value === 'mine') return 'mine'
  if (value === 'all' && isAdmin) return 'all'
  return 'queue'
}

export function unassignedCount(queue: TicketListItem[]): number {
  return queue.filter((ticket) => ticket.assignee === null).length
}

export function hasNeedsReview(tickets: TicketListItem[]): boolean {
  return tickets.some((ticket) => ticket.needsReview)
}

export type DashboardData = {
  unassigned: number
  mine: number
  needsReview: number
  attention: TicketListItem[]
}

function timeOf(value: string): number {
  const parsed = Date.parse(value)
  return Number.isNaN(parsed) ? 0 : parsed
}

/**
 * Dashboard numbers from the `queue` and `mine` lists. Replied tickets never count; tickets present in both
 * lists are counted once; attention = flagged first, then oldest, capped at ATTENTION_LIMIT.
 */
export function buildDashboard(queue: TicketListItem[], mine: TicketListItem[]): DashboardData {
  const open = (ticket: TicketListItem) => ticket.status !== 'Replied'
  const mineOpen = mine.filter(open)

  const unique = new Map<string, TicketListItem>()
  for (const ticket of [...queue, ...mineOpen]) {
    if (open(ticket) && !unique.has(ticket.id)) {
      unique.set(ticket.id, ticket)
    }
  }
  const all = [...unique.values()]

  const attention = all
    .slice()
    .sort(
      (a, b) =>
        Number(b.needsReview) - Number(a.needsReview) || timeOf(a.createdAt) - timeOf(b.createdAt),
    )
    .slice(0, ATTENTION_LIMIT)

  return {
    unassigned: unassignedCount(queue),
    mine: mineOpen.length,
    needsReview: all.filter((ticket) => ticket.needsReview).length,
    attention,
  }
}
