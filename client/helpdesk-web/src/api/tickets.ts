export type TicketFilter = 'queue' | 'mine' | 'all'

export type TicketStatus = 'New' | 'InReview' | 'Replied'

export type TicketAssignee = {
  id: string
  displayName: string
}

export type TicketListItem = {
  id: string
  subject: string
  requesterEmail: string
  status: TicketStatus
  category: string | null
  summary: string | null
  confidence: number | null
  assignee: TicketAssignee | null
  createdAt: string
  updatedAt: string
  hasDraft: boolean
  reviewReasons: number
  needsReview: boolean
}

export type TicketMessage = {
  id: string
  sender: string
  isFromUser: boolean
  receivedAt: string
  bodyText: string
}

export type TicketDetail = TicketListItem & {
  draftReply: string | null
  messages: TicketMessage[]
}

/** Reads the most useful error text from a failed API response ({ message } or a validation problem). */
export async function errorMessage(response: Response): Promise<string> {
  const body = (await response.json().catch(() => null)) as {
    message?: string
    title?: string
    errors?: Record<string, string[]>
  } | null
  if (body?.message) {
    return body.message
  }
  const firstValidation = body?.errors ? Object.values(body.errors).flat()[0] : undefined
  return firstValidation ?? body?.title ?? `Request failed: ${response.status} ${response.statusText}`
}
