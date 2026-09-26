import { useEffect, useRef, useState } from 'react'
import { Link, useParams } from 'react-router'
import { useMsal } from '@azure/msal-react'
import { format } from 'date-fns'
import { apiFetch } from '../api/apiFetch'
import { errorMessage, type TicketDetail } from '../api/tickets'
import type { CurrentUser } from '../hooks/useCurrentUser'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Textarea } from '@/components/ui/textarea'

type TicketDetailPageProps = {
  user: CurrentUser | null
  isAdmin: boolean
}

function formatDate(value: string): string {
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '—' : format(date, 'PPp')
}

function TicketDetailPage({ user, isAdmin }: TicketDetailPageProps) {
  const { id } = useParams()
  const { instance } = useMsal()
  const [ticket, setTicket] = useState<TicketDetail | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [text, setText] = useState('')
  const [busy, setBusy] = useState(false)
  const draftSeeded = useRef(false)

  useEffect(() => {
    let cancelled = false
    draftSeeded.current = false
    async function load() {
      setLoadError(null)
      try {
        const response = await apiFetch(instance, `/api/tickets/${id}`)
        if (!response.ok) {
          throw new Error(await errorMessage(response))
        }
        const data = (await response.json()) as TicketDetail
        if (cancelled) {
          return
        }
        setTicket(data)
        // Seed the editor with the AI draft once; later reloads must never overwrite the agent's edits.
        if (!draftSeeded.current) {
          draftSeeded.current = true
          setText(data.draftReply ?? '')
        }
      } catch (err) {
        if (!cancelled) {
          setLoadError(err instanceof Error ? err.message : 'Unknown error')
        }
      }
    }
    load()
    return () => {
      cancelled = true
    }
  }, [instance, id])

  async function act(path: string, init: RequestInit, successNotice?: string): Promise<boolean> {
    setBusy(true)
    setActionError(null)
    setNotice(null)
    try {
      const response = await apiFetch(instance, path, init)
      if (!response.ok) {
        setActionError(await errorMessage(response))
        return false
      }
      setTicket((await response.json()) as TicketDetail)
      if (successNotice) {
        setNotice(successNotice)
      }
      return true
    } catch (err) {
      setActionError(err instanceof Error ? err.message : 'Unknown error')
      return false
    } finally {
      setBusy(false)
    }
  }

  const claim = () => act(`/api/tickets/${id}/claim`, { method: 'POST' })
  const release = () => act(`/api/tickets/${id}/release`, { method: 'POST' })
  const send = async () => {
    const ok = await act(
      `/api/tickets/${id}/reply`,
      {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ text }),
      },
      'Reply sent.',
    )
    if (ok) {
      setText('')
    }
  }

  if (loadError) {
    return (
      <main className="mx-auto max-w-4xl px-8 py-8">
        <Alert variant="destructive">
          <AlertDescription>{loadError}</AlertDescription>
        </Alert>
        <Link to="/tickets" className="mt-4 inline-block text-sm hover:underline">
          ← Back to tickets
        </Link>
      </main>
    )
  }

  if (!ticket) {
    return <main className="mx-auto max-w-4xl px-8 py-8 text-sm text-muted-foreground">Loading…</main>
  }

  const isAssignee = ticket.assignee !== null && ticket.assignee.id === user?.id
  const canAct = ticket.assignee !== null && (isAssignee || isAdmin)
  const canRelease = ticket.assignee !== null && (isAssignee || isAdmin)
  const canClaim = ticket.assignee === null

  return (
    <main className="mx-auto flex max-w-4xl flex-col gap-6 px-8 py-8">
      <Link to="/tickets" className="text-sm hover:underline">
        ← Back to tickets
      </Link>

      <header className="flex flex-col gap-2">
        <h1 className="font-heading text-2xl font-semibold text-text-h">{ticket.subject}</h1>
        <div className="flex flex-wrap items-center gap-2 text-sm text-muted-foreground">
          <span>{ticket.requesterEmail}</span>
          <Badge>{ticket.status}</Badge>
          {ticket.category && <Badge variant="outline">{ticket.category}</Badge>}
        </div>
      </header>

      {ticket.summary && (
        <Card>
          <CardHeader>
            <CardTitle>AI summary</CardTitle>
          </CardHeader>
          <CardContent className="text-sm">
            <p>{ticket.summary}</p>
            {ticket.confidence !== null && (
              <p className="mt-2 text-muted-foreground">Confidence {Math.round(ticket.confidence * 100)}%</p>
            )}
          </CardContent>
        </Card>
      )}

      <section className="flex flex-col gap-3">
        <h2 className="font-heading text-lg font-semibold text-text-h">Conversation</h2>
        {ticket.messages.map((message) => (
          <Card key={message.id}>
            <CardHeader>
              <CardTitle className="flex items-center gap-2 text-sm">
                <Badge variant={message.isFromUser ? 'outline' : 'secondary'}>
                  {message.isFromUser ? 'Customer' : 'Agent'}
                </Badge>
                <span>{message.sender}</span>
                <span className="font-normal text-muted-foreground">
                  {formatDate(message.receivedAt)}
                </span>
              </CardTitle>
            </CardHeader>
            <CardContent>
              <p className="whitespace-pre-wrap text-sm">{message.bodyText}</p>
            </CardContent>
          </Card>
        ))}
      </section>

      <section className="flex flex-col gap-3">
        <div className="flex flex-wrap items-center gap-3 text-sm">
          <span>
            Assignee: <strong>{ticket.assignee?.displayName ?? 'Unassigned'}</strong>
          </span>
          {canClaim && (
            <Button type="button" size="sm" disabled={busy} onClick={claim}>
              Assign to me
            </Button>
          )}
          {canRelease && (
            <Button type="button" size="sm" variant="outline" disabled={busy} onClick={release}>
              Release
            </Button>
          )}
        </div>

        {actionError && (
          <Alert variant="destructive">
            <AlertDescription>{actionError}</AlertDescription>
          </Alert>
        )}
        {notice && (
          <Alert>
            <AlertDescription>{notice}</AlertDescription>
          </Alert>
        )}

        <Card>
          <CardHeader>
            <CardTitle>Reply</CardTitle>
          </CardHeader>
          <CardContent className="flex flex-col gap-3">
            {!canAct && (
              <p className="text-sm text-muted-foreground">
                {ticket.assignee === null
                  ? 'Assign this ticket to yourself to edit and send a reply.'
                  : `Only ${ticket.assignee.displayName} (or an admin) can reply to this ticket.`}
              </p>
            )}
            <Textarea
              value={text}
              onChange={(event) => setText(event.target.value)}
              rows={10}
              disabled={!canAct || busy}
              placeholder={ticket.draftReply ? undefined : 'Write your reply…'}
            />
            <div>
              <Button type="button" disabled={!canAct || busy || text.trim().length === 0} onClick={send}>
                {busy ? 'Working…' : 'Send reply'}
              </Button>
            </div>
          </CardContent>
        </Card>
      </section>
    </main>
  )
}

export default TicketDetailPage
