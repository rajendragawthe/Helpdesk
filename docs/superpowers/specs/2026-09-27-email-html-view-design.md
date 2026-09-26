# Customer Email HTML View (sandboxed iframe) — Design

Date: 2026-09-27
Status: Approved in brainstorming (chat); awaiting written-spec review
Origin: manual full-loop test of Phase 7 (2026-09-27): the ticket page shows the customer's email as plain text and the user wants it formatted.
Plan: `docs/superpowers/plans/2026-09-27-email-html-view.md` (to be written)
Sub-project 1 of 2. Sub-project 2 (app-shell redesign) gets its own spec and plan afterwards.

## Goal

On the ticket detail page, show the customer's original email the way it looks in a mail client
(paragraphs, bold, lists, links, basic styling), while staying safe against hostile HTML. Plain text
remains available as a fallback and a per-message toggle. The agent's own replies stay plain text.

## Decisions (from brainstorming)

1. **Approach: sandboxed iframe** (not a sanitizer library). The customer's HTML is rendered inside an
   `<iframe srcDoc>` that cannot run scripts, submit forms, or navigate the app; the iframe is the only
   trust boundary, so the server returns the HTML raw.
2. **Remote images are blocked** by default (no tracking pixels, no external requests). Emails that rely
   on remote images may look sparse. A per-message "Show images" control is deliberately deferred.
3. **Links open in a new tab** (`allow-popups`); they are not disabled. A phishing link in a customer
   email is visible to the agent as ordinary link text and is a residual risk accepted for an agent tool.
4. **Formatted by default**, with a Formatted / Plain text toggle per message.
5. **Auto-sized iframe**: `allow-same-origin` (safe without `allow-scripts`) lets the parent read the
   content height; a maximum height with scrolling bounds very long emails.

## Component map

```
Helpdesk.Application/Tickets/
  TicketDtos.cs     — TicketMessageDto gains `string? BodyHtml` (customer messages only)
  TicketMapper.cs   — sets BodyHtml for IsFromUser messages: the stored HTML, or null when the body is
                      blank or longer than the size cap (MaxBodyHtmlLength = 500_000 characters);
                      agent messages always get null

client/helpdesk-web/src/
  api/tickets.ts                     — TicketMessage gains `bodyHtml: string | null`
  lib/emailDocument.ts (new)         — pure buildEmailDocument(html): string
  components/EmailHtmlViewer.tsx (new) — the sandboxed iframe + auto-height
  pages/TicketDetailPage.tsx         — per-message Formatted / Plain text toggle using the viewer
```

No migration, no new endpoint, no new npm dependency. `bodyText` is unchanged and stays the fallback.

## Behaviour

### API
- `GET /api/tickets/{id}` (and every response that returns `TicketDetail`) includes `bodyHtml` per message.
  Customer message: the stored `Message.Body` verbatim if it is not blank and `Length <= 500_000`, else
  `null`. Agent message: `null` (agent text is not HTML).
- The list endpoint is unchanged (it carries no message bodies).
- Because the value is HTML, JSON serialization is unchanged (it is just a string); nothing on the server
  interprets or rewrites it.

### `buildEmailDocument(html)` (pure, `lib/emailDocument.ts`)
Returns a complete HTML document string for `srcDoc`:
- `<meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src data:">`
  placed first in `<head>`: no scripts, no remote images/fonts/frames/forms, inline styles allowed,
  `data:` images/fonts allowed.
- `<base target="_blank">` and `<meta name="referrer" content="no-referrer">` so links open in a new tab
  without leaking the app URL.
- A small default stylesheet (readable font, `max-width: 100%` for images and tables, word-wrap for long
  lines) that email styles can override.
- The email HTML inside `<body>`. If the email is a full document (`<html>…`), its `<head>`/`<body>`
  wrappers are stripped so exactly one head (ours) and one body exist; the email's own `<style>` blocks
  are kept.

### `EmailHtmlViewer`
- Renders `<iframe title="Customer email" sandbox="allow-same-origin allow-popups allow-popups-to-escape-sandbox" referrerPolicy="no-referrer" srcDoc={buildEmailDocument(html)} />`.
  Deliberately NOT allowed: `allow-scripts`, `allow-forms`, `allow-top-navigation*`, `allow-modals`,
  `allow-downloads`.
- On load, sets the height to the content height (via `contentDocument`), clamped to a maximum (about 2000 px
  with scroll beyond it) and re-measured on window resize; if measuring fails it keeps a fixed default height.
- No `dangerouslySetInnerHTML` anywhere.

### Detail page
- For each customer message with `bodyHtml`: Formatted (viewer) by default with a Formatted / Plain text
  toggle; with `bodyHtml == null`: plain text as today, no toggle. Agent messages unchanged.
- State is local per message; no persistence.

## Security notes

- The sandbox has no `allow-scripts`, so no script runs even if the CSP were bypassed; the CSP also blocks
  script, remote loads and form actions. `allow-same-origin` is acceptable only because scripts are disabled;
  never add `allow-scripts` together with it.
- A hostile email can still: navigate its own iframe (meta refresh) to a remote page (still script-less and
  sandboxed), and show misleading link text. Neither reaches the app or the agent's session.
- Size cap prevents a multi-megabyte body (e.g. inline base64 images) from bloating the API response and the DOM.
- The plain-text path (`HtmlText.ToPlainText`) is unchanged and still used for `bodyText`.

## Error handling

| Case | Result |
|---|---|
| Body blank, oversize, or agent message | `bodyHtml = null`; plain text shown, no toggle |
| Height measurement fails | fixed default height with scroll |
| Malformed HTML | rendered best-effort by the browser inside the sandbox |

## Testing

- Application (xunit, hand-written fakes): `TicketMapper` / workflow tests: customer message returns the raw
  HTML; agent message returns `null`; blank body returns `null`; a body at the cap is returned and one
  character over is `null`; `bodyText` is unchanged for all.
- `Helpdesk.Api.Tests`: the controller returns a detail whose messages carry `bodyHtml` (mapping through JSON
  is a plain string).
- Frontend: `npm run lint` and `npm run build`. There is no component test framework in this repo, so
  `buildEmailDocument` stays a pure function for later unit tests and the viewer is checked manually.
- Manual (user's hands: secrets, a real formatted email): send an HTML email with bold, a list, a link, an
  inline style, a remote image and a `<script>` tag; confirm it renders formatted, the image is not loaded,
  the script does not run, links open in a new tab, the height fits, and the Plain text toggle works.

## Out of scope

Attachments and inline `cid:` images; a "Show images" control; server-side HTML sanitization; quoted-reply
collapsing and signature trimming; HTML for agent replies (they stay plain text, Phase 7 behaviour);
the app-shell redesign (sub-project 2).
