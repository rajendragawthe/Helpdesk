# Phase 7 — Agent Queue, Claim and Reply (Graph send) — Design

Date: 2026-09-27
Status: Approved in brainstorming (chat); awaiting written-spec review
Covers: `implementation-plan.md` Phase 7, tasks 40-45 (core loop only; see "Out of scope" for Phase 7b)
Plan: `docs/superpowers/plans/2026-09-27-phase7-agent-queue-reply.md` (to be written)
Runs in parallel with Phase 8 (low-confidence escalation); see "Contract with Phase 8".

## Goal

Close the MVP loop: an agent sees the queue of tickets (each with the AI category/summary and draft),
claims one, reviews and edits the AI draft, and sends it. The reply goes out through Microsoft Graph as
a threaded email reply, is stored on the ticket thread, and the ticket becomes `Replied`.

## Decisions (from brainstorming)

1. **Explicit claim.** An agent must claim a ticket ("Assign to me") before sending. Claim is atomic
   (409 if someone else holds it).
2. **Claim rules.** Only the assignee (or an Admin) may send. The assignee or any Admin may release.
   Anyone with the Agent or Admin role may VIEW any ticket (read-only when not theirs).
3. **Scope split.** This phase is the core loop only. Reopening a `Replied` ticket on a customer
   follow-up, keeping the assignee, and thread-aware AI re-drafting are Phase 7b (after Phase 8 merges,
   to avoid conflicting edits to ingestion). Until then customer follow-ups append to the thread and
   the status stays as today.
4. **Layering.** Logic lives in an Application-layer `TicketWorkflowService`; controllers stay thin and
   never call Infrastructure (per `CLAUDE.md`).
5. **Graph send.** Reply to the latest customer message with the Graph "reply to a message" call
   (deviation from the plan's "using conversationId": replying to a message keeps the thread by
   construction; `sendMail` with a new message threads unreliably).

## Component map

```
Helpdesk.Core/
  Interfaces/IMailClient.cs          — + SendReplyAsync(replyToExternalMessageId, plainTextBody, ct)
  Interfaces/ITicketRepository.cs    — + TryAssignAsync(ticketId, userId) [atomic], ReleaseAsync,
                                        list query (Classification + AssignedUser), detail query (Messages ordered)
  Models/                            — small result/DTO-shaped records only if needed by Application

Helpdesk.Application/Tickets/
  ITicketWorkflowService.cs, TicketWorkflowService.cs — list, get, claim, release, send reply
  TicketResults (result enums/records) — Success | NotFound | Forbidden | Conflict | SendFailed | Invalid

Helpdesk.Infrastructure/
  GraphApi/GraphMailClient.cs        — SendReplyAsync via Graph reply call on Users[mailbox].Messages[id]
  Repositories/TicketRepository.cs   — new queries; TryAssignAsync = conditional UPDATE (ExecuteUpdate)

Helpdesk.Api/
  Controllers/TicketsController.cs   — [Authorize(Policy = "AgentOnly")]; maps results to HTTP
  Validators/ReplyRequestValidator.cs — FluentValidation for the reply body
  Auth/HelpdeskUserClaimsTransformation.cs — + adds a `helpdesk_user_id` claim (the Users.Id) so the
                                        controller can identify the caller without another lookup

client/helpdesk-web/src/
  pages/QueuePage.tsx, pages/TicketDetailPage.tsx, components/NavBar.tsx (+ link), App.tsx (+ routes)
```

No migration in this phase (`AssignedUserId`, `Message.IsFromUser`, `Status` already exist). Phase 8
owns the `ReviewReasons` column.

## API

All endpoints require the `AgentOnly` policy (Admin or Agent). The caller's identity comes from the
`helpdesk_user_id` and role claims.

| Endpoint | Behaviour |
|---|---|
| `GET /api/tickets?filter=queue\|mine\|all` | `queue` (default): unassigned + assigned to me, excluding `Replied`. `mine`: assigned to me, any status. `all`: everything, Admin only (403 otherwise). Newest first. Row: id, subject, requesterEmail, status, category, summary, confidence, assignee {id, displayName} or null, createdAt, updatedAt, hasDraft, reviewReasons, needsReview. |
| `GET /api/tickets/{id}` | Full detail: the row fields plus `draftReply`, and `messages` [{id, sender, isFromUser, receivedAt, bodyText}] oldest first. `bodyText` is plain text produced with the existing `HtmlText.ToPlainText`; raw customer HTML is never returned. 404 if unknown. |
| `POST /api/tickets/{id}/claim` | Atomic claim of an unassigned ticket. 200 with the ticket if it was unassigned or already mine (idempotent); 409 if held by someone else; 404 unknown. |
| `POST /api/tickets/{id}/release` | Assignee or Admin only (403 otherwise); sets `AssignedUserId = null`; 404 unknown. Releasing an unassigned ticket is a no-op 200. |
| `PUT /api/tickets/{id}/reply` body `{ "text": "..." }` | Validator: `text` not blank, at most 10 000 characters (400 `ValidationProblemDetails` otherwise). 404 unknown; 403 unless the caller is the assignee or an Admin; 409 if the ticket is unassigned; 422 if the ticket has no customer message with an `ExternalMessageId` to reply to. Sends via Graph; on a Graph failure returns 502 and changes nothing. On success stores the outbound `Message` (`IsFromUser=false`, `Sender` = the sending agent's email, `Body` = the sent text, `ReceivedAt` = now, `ExternalMessageId` null), sets `Status = Replied`, `UpdatedAt`, keeps `DraftReply` as the AI's original, returns the updated detail. |

Reply order and failure semantics: validate -> Graph send -> persist message + status. If the email is
sent but persistence then fails, the API returns 500 with a body stating that the email WAS sent, and
logs it as an error with the ticket id (accepted rare case at MVP; no outbox). The UI shows that message
so the agent does not re-send.

## Behaviour details

- **Reply target:** the ticket's most recent `IsFromUser` message that has an `ExternalMessageId`.
- **Graph send:** `GraphMailClient.SendReplyAsync` calls the Graph reply endpoint on the monitored
  mailbox (`Users[MailboxAddress].Messages[id]`) with the agent's text as the comment. The sent mail lands
  in Sent Items, so the Inbox poller does not re-ingest it. Needs the `Mail.Send` application permission
  already granted in Phase 4. Errors surface as exceptions; the service maps them to `SendFailed`.
- **Statuses:** sending is allowed from any status for the assignee (`New`, `InReview`, `Replied`); the
  result is always `Replied`. A `New` ticket with no draft (AI off/failed) can still be answered.
- **Concurrency:** claim uses a conditional UPDATE so two simultaneous claims cannot both succeed.
  Duplicate submits of the same reply are prevented by the UI (button disabled while in flight); the
  server does not de-duplicate.
- **Security:** authorization is enforced server-side per endpoint (never trusting the UI); customer content
  is only ever returned as plain text; the reply text is length-bounded and sent as plain text.

## Frontend (React + shadcn, `apiFetch`, `react-router`)

- **`/tickets` QueuePage:** tabs Queue / Mine / All (All only for Admin), a shadcn `Table` with subject,
  requester, status `Badge`, category `Badge`, summary, assignee, relative age; rows link to the detail
  page. Loading/empty/error states. The review-flag badge (Phase 8, task 48) is added afterwards.
- **`/tickets/:id` TicketDetailPage:** the thread (oldest first, plain text, customer vs agent styled),
  an AI summary `Card` (category, confidence), assignee line with Claim / Release buttons, a `Textarea`
  pre-filled with the AI draft, and a Send button. The textarea and Send are enabled only when the caller
  is the assignee (or Admin) and the ticket is claimed; Send is disabled while in flight and while the
  text is blank. 409/403/422/502/500 map to inline `Alert`s with the server message (no browser dialogs);
  success updates the page to the `Replied` state and shows the sent message in the thread.
- **Routing/nav:** routes `/tickets` and `/tickets/:id` guarded like `/home` (authenticated and registered);
  `NavBar` gets a "Tickets" link.
- Frontend checks in this repo are `npm run lint` and `npm run build`; there is no component test
  framework, so behaviour is verified by the manual full-loop test and later Playwright E2E.

## Contract with Phase 8 (parallel work)

- Phase 8 owns `Ticket.ReviewReasons` (`[Flags]` `Helpdesk.Core.Enums.ReviewReasons`, int column) and its
  migration; Phase 7 adds no migration.
- Phase 7's list and detail DTOs expose `reviewReasons` (the int flags) and `needsReview`
  (`reviewReasons != None`). Because the column arrives with Phase 8, those two fields are added in the
  LAST backend task, after this branch is rebased onto Phase 8's merged backend.
- Merge order: Phase 8 backend first, Phase 7 rebases and merges, then the queue badge (task 48) follows.

## Error handling

| Failure | Result |
|---|---|
| Not registered / wrong role | 401/403 from the existing auth pipeline / policy |
| Unknown ticket | 404 |
| Claim on someone else's ticket | 409 |
| Reply by a non-assignee (non-Admin) | 403; on an unassigned ticket 409 |
| Blank or oversize reply | 400 validation problem |
| No replyable customer message | 422 |
| Graph send fails (auth, throttling, network) | 502; ticket unchanged; error logged |
| Sent but persistence failed | 500 with a "the email was sent" message; error logged |

## Testing

- `TicketWorkflowService` unit tests with hand-written fakes: list filters (queue/mine/all, Admin-only all),
  detail mapping and plain-text bodies, claim (free, already mine, held by another, unknown), release
  (assignee, Admin, other agent, unassigned), reply (success stores message + `Replied` + keeps draft, target
  message choice, no assignee, not the assignee, Admin override, blank text, Graph failure leaves everything
  untouched, persistence failure after send reports "sent", cancellation).
- `ReplyRequestValidator` tests (blank, whitespace, max length boundary).
- `GraphMailClient.SendReplyAsync` test if the Graph client can be faked with a request adapter; otherwise
  covered by the manual test and a named gap in the plan.
- Repository atomic claim verified against the model/SQL shape in a focused test; concurrency correctness
  relies on the conditional UPDATE.
- Claims transformation test for the new `helpdesk_user_id` claim.
- Manual (user's hands: secrets, real email, real mailbox): email in -> queue shows it with a draft -> claim
  -> edit -> Send -> the reply arrives in the sender's inbox as a threaded reply and the ticket shows
  `Replied`.

## Out of scope

Phase 7b (reopen on customer reply, keep assignee, thread-aware AI re-draft, review-flag rules for reopened
tickets); saving a draft without sending; an outbox/retry for sends; attachments and HTML/rich-text
replies; reassigning a ticket to a named agent; SLA or notifications; the review-flag badge (Phase 8 task
48); pagination and search on the queue; Playwright E2E (added by `qa-engineer` after both phases merge).
