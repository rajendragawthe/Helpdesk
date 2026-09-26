# Customer Email HTML View (sandboxed iframe) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The ticket detail page shows the customer's original email formatted (in a locked-down iframe), with plain text as a per-message fallback toggle.

**Architecture:** The API's `TicketMessageDto` gains `bodyHtml` (the customer's raw HTML, or null). The frontend builds a safe document (`buildEmailDocument`: DOM-based removal of active elements + strict CSP + `<base target="_blank">`) and renders it in a script-less, auto-sized `<iframe sandbox>` (`EmailHtmlViewer`); `MessageBody` adds a Formatted / Plain text toggle.

**Tech Stack:** .NET 10 (Application layer + xunit), React 19 + TypeScript + Tailwind v4 + shadcn/ui, browser `DOMParser`.

**Spec:** `docs/superpowers/specs/2026-09-27-email-html-view-design.md`

## Global Constraints

- Iframe `sandbox` tokens are EXACTLY `allow-same-origin allow-popups allow-popups-to-escape-sandbox`. NEVER add `allow-scripts` (especially never together with `allow-same-origin`), `allow-forms`, `allow-top-navigation*`, `allow-modals` or `allow-downloads`. No `dangerouslySetInnerHTML` anywhere. The iframe also has `referrerPolicy="no-referrer"`.
- The document CSP (exact): `default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src data:` in a `<meta http-equiv="Content-Security-Policy">` placed first in `<head>`; plus `<meta name="referrer" content="no-referrer">` and `<base target="_blank">`.
- Only customer messages (`IsFromUser`) can have `bodyHtml`; agent messages always `null`. `bodyHtml` is `null` when the body is blank, longer than `MaxBodyHtmlLength = 500_000` characters, or does not look like HTML (plain-text emails). `bodyText` is unchanged and remains the fallback.
- The server returns the HTML raw (no server-side sanitization); the iframe is the only trust boundary.
- No migration, no new endpoint, no new npm dependency (no test framework is added; frontend verification is `npm run lint` + `npm run build` + the manual test).
- Layering: `Helpdesk.Application` depends only on `Helpdesk.Core`. Follow existing test style (xunit, hand-written fakes, no mocking library). Prefer vendored shadcn components. No browser dialogs.
- Commit messages end with `Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>`. Work stays on branch `email-html-view` in worktree `D:/Rajendra/Claude/Learning/HELPDESK-emailview`; do not touch the main checkout; do not merge or push. Never put secrets in the chat, repo files or memory.
- Plan deviations from the spec (decided while planning, both safe): (1) `buildEmailDocument` uses the browser's inert `DOMParser` to drop active elements and re-serialize, instead of string surgery; it has no side effects. (2) `bodyHtml` is also `null` for plain-text emails (no HTML tags found), because an HTML iframe would collapse their line breaks; the existing plain-text view already shows them correctly.

## Review Focus

Failure modes the spec implies but no obvious test would catch; each is covered by a test in the owning task unless noted:
1. Boundary and shape of `bodyHtml`: a body exactly at 500 000 chars is returned, one char over is `null`; blank and whitespace-only are `null`; a plain-text email (no tags) is `null`; agent text that happens to contain tags is never returned as HTML (Task 1).
2. `bodyText` keeps its Phase 7 behaviour for every message (script/tag stripping for customer text, agent text verbatim) (Task 1).
3. Iframe hardening is exactly as specified: the `sandbox` token list, `referrerPolicy`, CSP string, no `allow-scripts` anywhere in the diff (Task 2: reviewer checks the diff; not unit-testable without a browser).
4. `buildEmailDocument` neutralizes hostile markup: `<script>`, `<iframe>`, `<object>/<embed>`, `<form>`, `<link>`, `<meta http-equiv=refresh>`, `<base>` are removed; the email's own `<style>` survives; a `</style>` sequence cannot break out of the style block (Task 2; manual test with a hostile sample).
5. Height measuring never throws when `contentDocument` is null, shrinks as well as grows, and is bounded (Task 2).
6. Named gap: no automated test of the frontend viewer (no test framework by design); the manual test with a formatted + hostile HTML email is the evidence.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/Helpdesk.Application/Tickets/TicketDtos.cs` (mod) | `TicketMessageDto` + `BodyHtml` |
| `src/Helpdesk.Application/Tickets/TicketMapper.cs` (mod) | decide when `BodyHtml` is set |
| `src/Helpdesk.Application/Tickets/TicketWorkflowService.cs` (mod) | `public const int MaxBodyHtmlLength` |
| `tests/Helpdesk.Application.Tests/Tickets/TicketWorkflowServiceTests.cs` (mod) | mapping tests |
| `client/helpdesk-web/src/api/tickets.ts` (mod) | `bodyHtml: string \| null` |
| `client/helpdesk-web/src/lib/emailDocument.ts` (new) | `buildEmailDocument(html)` |
| `client/helpdesk-web/src/components/EmailHtmlViewer.tsx` (new) | sandboxed, auto-sized iframe |
| `client/helpdesk-web/src/components/MessageBody.tsx` (new) | Formatted / Plain text toggle per message |
| `client/helpdesk-web/src/pages/TicketDetailPage.tsx` (mod) | use `MessageBody` |
| `CLAUDE.md` (mod) | docs |

---

### Task 1: API — `bodyHtml` on ticket messages

**Files:**
- Modify: `src/Helpdesk.Application/Tickets/TicketDtos.cs`, `TicketMapper.cs`, `TicketWorkflowService.cs`
- Test: `tests/Helpdesk.Application.Tests/Tickets/TicketWorkflowServiceTests.cs` (append)

**Interfaces:**
- Consumes: existing `TicketMessageDto`, `TicketMapper.ToMessage`, `HtmlText.ToPlainText`, the test file's helpers `Customer(string body, DateTimeOffset at, string? externalId = "ext-1")`, `AgentMessage(string body, DateTimeOffset at)`, `AddTicket(TicketStatus status = InReview, User? assignedTo = null, string? draft = "AI draft", DateTimeOffset? createdAt = null, params Message[] messages)`, `Create()`, `AliceCaller`.
- Produces: `TicketMessageDto(Guid Id, string Sender, bool IsFromUser, DateTimeOffset ReceivedAt, string BodyText, string? BodyHtml)`; `public const int MaxBodyHtmlLength = 500_000;` on `TicketWorkflowService`.

- [ ] **Step 1: Write the failing tests**

Append inside `TicketWorkflowServiceTests` (before the closing brace of the class):
```csharp
    // ---- Customer email HTML ----

    [Fact]
    public async Task GetAsync_CustomerHtmlBody_IsReturnedRawAsBodyHtml_AndBodyTextIsStillPlain()
    {
        const string html = "<html><body><p>Hello <b>there</b></p><script>alert('x')</script></body></html>";
        var ticket = AddTicket(messages: [Customer(html, DateTimeOffset.UtcNow.AddMinutes(-20))]);

        var message = Assert.Single((await Create().GetAsync(ticket.Id)).Value!.Messages);

        Assert.Equal(html, message.BodyHtml);
        Assert.Equal("Hello there", message.BodyText);
    }

    [Fact]
    public async Task GetAsync_AgentMessageWithTags_NeverHasBodyHtml()
    {
        var ticket = AddTicket(messages:
        [
            Customer("<p>hi</p>", DateTimeOffset.UtcNow.AddMinutes(-20)),
            AgentMessage("<b>not html</b> we typed this", DateTimeOffset.UtcNow.AddMinutes(-10)),
        ]);

        var messages = (await Create().GetAsync(ticket.Id)).Value!.Messages;

        Assert.NotNull(messages[0].BodyHtml);
        Assert.Null(messages[1].BodyHtml);
        Assert.Equal("<b>not html</b> we typed this", messages[1].BodyText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    public async Task GetAsync_BlankCustomerBody_HasNoBodyHtml(string body)
    {
        var ticket = AddTicket(messages: [Customer(body, DateTimeOffset.UtcNow.AddMinutes(-5))]);

        var message = Assert.Single((await Create().GetAsync(ticket.Id)).Value!.Messages);

        Assert.Null(message.BodyHtml);
    }

    [Fact]
    public async Task GetAsync_PlainTextCustomerEmail_HasNoBodyHtml_SoTheTextViewKeepsItsLineBreaks()
    {
        var ticket = AddTicket(messages: [Customer("Hi team,\n\nplease refund me 5 < 10.\nThanks", DateTimeOffset.UtcNow.AddMinutes(-5))]);

        var message = Assert.Single((await Create().GetAsync(ticket.Id)).Value!.Messages);

        Assert.Null(message.BodyHtml);
        Assert.Contains("refund me", message.BodyText);
    }

    [Fact]
    public async Task GetAsync_BodyHtmlSizeCap_ExactlyAtTheLimitIsReturned_OneOverIsNull()
    {
        var atLimit = "<p>" + new string('x', TicketWorkflowService.MaxBodyHtmlLength - 3);
        var overLimit = atLimit + "y";
        Assert.Equal(TicketWorkflowService.MaxBodyHtmlLength, atLimit.Length);
        var ticket = AddTicket(messages:
        [
            Customer(atLimit, DateTimeOffset.UtcNow.AddMinutes(-20), "ext-a"),
            Customer(overLimit, DateTimeOffset.UtcNow.AddMinutes(-10), "ext-b"),
        ]);

        var messages = (await Create().GetAsync(ticket.Id)).Value!.Messages;

        Assert.Equal(atLimit, messages[0].BodyHtml);
        Assert.Null(messages[1].BodyHtml);
        Assert.False(string.IsNullOrEmpty(messages[1].BodyText));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Helpdesk.Application.Tests/Helpdesk.Application.Tests.csproj --filter "FullyQualifiedName~BodyHtml|FullyQualifiedName~PlainTextCustomerEmail|FullyQualifiedName~BlankCustomerBody"`
Expected: build FAIL — `BodyHtml` / `MaxBodyHtmlLength` do not exist.

- [ ] **Step 3: Implement**

In `TicketDtos.cs` change the message record to:
```csharp
public record TicketMessageDto(
    Guid Id,
    string Sender,
    bool IsFromUser,
    DateTimeOffset ReceivedAt,
    string BodyText,
    string? BodyHtml);
```
In `TicketWorkflowService.cs` next to `MaxReplyLength` add:
```csharp
    /// <summary>Largest customer email HTML returned to the client; bigger bodies fall back to plain text only.</summary>
    public const int MaxBodyHtmlLength = 500_000;
```
In `TicketMapper.cs` add `using System.Text.RegularExpressions;` and replace `ToMessage` and its comment with:
```csharp
    // Customer email bodies are arbitrary HTML: BodyText is always the stripped plain text. BodyHtml carries the
    // raw HTML for customer messages only (the client renders it inside a script-less sandboxed iframe, the only
    // trust boundary) and is null when there is nothing worth rendering as HTML. Agent replies are plain text we
    // stored ourselves: returned as written, never as HTML.
    private static TicketMessageDto ToMessage(Message message) => new(
        message.Id,
        message.Sender,
        message.IsFromUser,
        message.ReceivedAt,
        message.IsFromUser ? HtmlText.ToPlainText(message.Body) : message.Body,
        ToBodyHtml(message));

    private static string? ToBodyHtml(Message message)
    {
        if (!message.IsFromUser
            || string.IsNullOrWhiteSpace(message.Body)
            || message.Body.Length > TicketWorkflowService.MaxBodyHtmlLength)
        {
            return null;
        }

        // Plain-text emails have no tags: in an HTML iframe their line breaks would collapse, and the plain-text
        // view already renders them correctly.
        return LooksLikeHtml(message.Body) ? message.Body : null;
    }

    private static bool LooksLikeHtml(string body) =>
        HtmlTagRegex().IsMatch(body);

    [GeneratedRegex(
        @"<\s*(html|head|body|div|p|br|table|span|a|b|i|u|strong|em|ul|ol|li|h[1-6]|img|style|font|center|blockquote)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex HtmlTagRegex();
```
and change the class declaration to `internal static partial class TicketMapper`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Helpdesk.slnx`
Expected: PASS (all projects; the 5 new tests included; existing `BodyText` tests unchanged).

- [ ] **Step 5: Commit**

```bash
git add src/Helpdesk.Application tests/Helpdesk.Application.Tests
git commit -m "feat: return customer email HTML as bodyHtml on ticket messages

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 2: Frontend — sandboxed email viewer and toggle

**Files:**
- Modify: `client/helpdesk-web/src/api/tickets.ts`, `client/helpdesk-web/src/pages/TicketDetailPage.tsx`
- Create: `client/helpdesk-web/src/lib/emailDocument.ts`, `client/helpdesk-web/src/components/EmailHtmlViewer.tsx`, `client/helpdesk-web/src/components/MessageBody.tsx`

**Interfaces:**
- Consumes: the API's `bodyHtml` (Task 1), existing `TicketMessage` type, shadcn `Button`.
- Produces: `TicketMessage.bodyHtml: string | null`; `buildEmailDocument(html: string): string`; default-exported `EmailHtmlViewer` (`{ html: string }`) and `MessageBody` (`{ message: TicketMessage }`).

- [ ] **Step 1: Install dependencies (once)**

Run in `client/helpdesk-web`: `npm ci`
Expected: installs cleanly (`node_modules` is git-ignored and absent in this worktree).

- [ ] **Step 2: Add the API type**

In `client/helpdesk-web/src/api/tickets.ts` add to `TicketMessage` (after `bodyText: string`):
```ts
  bodyHtml: string | null
```

- [ ] **Step 3: Add the document builder**

`client/helpdesk-web/src/lib/emailDocument.ts`:
```ts
// Builds the document rendered inside the sandboxed email iframe. The iframe (no allow-scripts) and the CSP
// below are the security boundary; removing active elements here is defence in depth and keeps the layout sane.
const CSP = "default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src data:"

const BASE_STYLE = [
  'body{margin:0;padding:12px;font-family:system-ui,-apple-system,"Segoe UI",Roboto,sans-serif;',
  'font-size:14px;line-height:1.5;color:#1f2937;background:#ffffff;overflow-wrap:anywhere}',
  'img,table{max-width:100%}img{height:auto}a{color:#2563eb}',
].join('')

// Elements that can load, run or navigate: dropped before the HTML is re-serialized.
const ACTIVE_ELEMENTS = 'script, noscript, iframe, frame, frameset, object, embed, applet, form, link, meta, base, template'

export function buildEmailDocument(html: string): string {
  // DOMParser documents are inert: nothing in `html` runs or loads while it is parsed here.
  const parsed = new DOMParser().parseFromString(html, 'text/html')

  const styles = Array.from(parsed.querySelectorAll('style')).map((style) => style.textContent ?? '')
  parsed.querySelectorAll(`${ACTIVE_ELEMENTS}, style`).forEach((element) => element.remove())

  // The email's own CSS is kept; a closing-tag sequence can never appear in it, but strip it defensively.
  const emailCss = styles.join('\n').replace(/<\/style/gi, '')

  return [
    '<!doctype html><html><head><meta charset="utf-8">',
    `<meta http-equiv="Content-Security-Policy" content="${CSP}">`,
    '<meta name="referrer" content="no-referrer">',
    '<base target="_blank">',
    `<style>${BASE_STYLE}</style>`,
    `<style>${emailCss}</style>`,
    '</head><body>',
    parsed.body.innerHTML,
    '</body></html>',
  ].join('')
}
```

- [ ] **Step 4: Add the viewer**

`client/helpdesk-web/src/components/EmailHtmlViewer.tsx`:
```tsx
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { buildEmailDocument } from '@/lib/emailDocument'

const DEFAULT_HEIGHT = 240
const MAX_HEIGHT = 2000

type EmailHtmlViewerProps = {
  html: string
}

// Renders a customer's email HTML in a locked-down iframe. NEVER add `allow-scripts` (especially together with
// `allow-same-origin`), `allow-forms`, `allow-top-navigation*`, `allow-modals` or `allow-downloads` to `sandbox`:
// `allow-same-origin` is only acceptable because scripts stay disabled, and it lets us measure the content height.
function EmailHtmlViewer({ html }: EmailHtmlViewerProps) {
  const frameRef = useRef<HTMLIFrameElement>(null)
  const [height, setHeight] = useState(DEFAULT_HEIGHT)
  const srcDoc = useMemo(() => buildEmailDocument(html), [html])

  const measure = useCallback(() => {
    const body = frameRef.current?.contentDocument?.body
    if (!body) return
    const measured = Math.ceil(body.getBoundingClientRect().height) + 2
    if (measured > 2) {
      setHeight(Math.min(measured, MAX_HEIGHT))
    }
  }, [])

  useEffect(() => {
    window.addEventListener('resize', measure)
    return () => window.removeEventListener('resize', measure)
  }, [measure])

  return (
    <iframe
      ref={frameRef}
      title="Customer email"
      sandbox="allow-same-origin allow-popups allow-popups-to-escape-sandbox"
      referrerPolicy="no-referrer"
      srcDoc={srcDoc}
      onLoad={measure}
      style={{ height }}
      className="w-full rounded-md border bg-white"
    />
  )
}

export default EmailHtmlViewer
```

- [ ] **Step 5: Add the per-message body with the toggle**

`client/helpdesk-web/src/components/MessageBody.tsx`:
```tsx
import { useState } from 'react'
import type { TicketMessage } from '../api/tickets'
import EmailHtmlViewer from './EmailHtmlViewer'
import { Button } from '@/components/ui/button'

type MessageBodyProps = {
  message: TicketMessage
}

type View = 'formatted' | 'text'

// Customer emails with HTML default to the formatted (sandboxed) view; everything else stays plain text.
function MessageBody({ message }: MessageBodyProps) {
  const [view, setView] = useState<View>('formatted')

  if (!message.bodyHtml) {
    return <p className="whitespace-pre-wrap text-sm">{message.bodyText}</p>
  }

  return (
    <div className="flex flex-col gap-2">
      <div className="flex gap-1">
        <Button
          type="button"
          size="sm"
          variant={view === 'formatted' ? 'secondary' : 'ghost'}
          onClick={() => setView('formatted')}
        >
          Formatted
        </Button>
        <Button
          type="button"
          size="sm"
          variant={view === 'text' ? 'secondary' : 'ghost'}
          onClick={() => setView('text')}
        >
          Plain text
        </Button>
      </div>
      {view === 'formatted' ? (
        <EmailHtmlViewer html={message.bodyHtml} />
      ) : (
        <p className="whitespace-pre-wrap text-sm">{message.bodyText}</p>
      )}
    </div>
  )
}

export default MessageBody
```

- [ ] **Step 6: Use it on the detail page**

In `client/helpdesk-web/src/pages/TicketDetailPage.tsx` add `import MessageBody from '../components/MessageBody'` next to the other relative imports, and replace the line
```tsx
              <p className="whitespace-pre-wrap text-sm">{message.bodyText}</p>
```
with
```tsx
              <MessageBody message={message} />
```
(The surrounding `Card`/`CardContent` markup is unchanged.)

- [ ] **Step 7: Lint, build, commit**

Run in `client/helpdesk-web`: `npm run lint` then `npm run build`
Expected: 0 lint errors (report new warnings separately from the existing ~19), build succeeds.
```bash
git add client/helpdesk-web/src
git commit -m "feat: show customer emails as sandboxed HTML on the ticket page

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 3: Docs

**Files:**
- Modify: `CLAUDE.md`

**Interfaces:** consumes everything above; produces documentation only.

- [ ] **Step 1: Update `CLAUDE.md`**

In the `### Agent queue and reply (Phase 7 done)` section: the paragraph currently says the ticket page shows the customer's email as plain text ("Known UX gap found in that test: the ticket page shows the customer's email as plain text (by design for security); a sandboxed-iframe HTML view is planned."). Replace that sentence with:
```
Customer email HTML view (added after that test): `TicketMessageDto.bodyHtml` carries the customer's raw HTML (null for agent messages, blank/plain-text bodies, and bodies over `MaxBodyHtmlLength` = 500 000 chars); the ticket page renders it with `EmailHtmlViewer` in an `<iframe sandbox="allow-same-origin allow-popups allow-popups-to-escape-sandbox">` (NEVER add `allow-scripts`/`allow-forms`/`allow-top-navigation*`; `allow-same-origin` is only acceptable because scripts stay off) fed by `buildEmailDocument` (`src/lib/emailDocument.ts`: active elements removed, strict CSP `default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src data:`, `<base target="_blank">`), so remote images are blocked and links open in a new tab; each message has a Formatted / Plain text toggle (`MessageBody`). Manual check pending: send an HTML email with bold, a list, a link, a remote image and a `<script>`; it must render formatted, load no image, run no script, and the toggle must work.
```
If the exact old sentence is not found, insert the new text at the end of that section's first paragraph instead and report it.

- [ ] **Step 2: Verify and commit**

Run: `dotnet build Helpdesk.slnx` (sanity).
```bash
git add CLAUDE.md
git commit -m "docs: document the customer email HTML view

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Manual test (user's hands, not an implementer task)

1. Start the API (`OpenRouter__Model=nvidia/nemotron-3-super-120b-a12b:free`) and `npm run dev` in `client/helpdesk-web`; clear the mailbox first.
2. From your own address send an HTML email to the monitored mailbox containing bold text, a bullet list, a link, an inline style (colored text), a remote image, and (if your client allows raw HTML) a `<script>alert(1)</script>` and `<meta http-equiv="refresh" content="0;url=https://example.com">`.
3. Open the ticket: the email should look formatted; the remote image must not load; no alert; no redirect; the link opens in a new tab; the iframe height fits the content; the Plain text toggle shows the stripped text; a plain-text-only email shows as before with no toggle. Record the result in `CLAUDE.md` afterwards.

## Self-Review Notes

- **Spec coverage:** `bodyHtml` rules and cap (Task 1), sandbox tokens/CSP/base/referrer/auto-height/toggle (Task 2), docs and manual check (Task 3). Out-of-scope items (attachments, "Show images", server sanitization, quoted-reply collapsing, agent HTML, redesign) untouched.
- **Cross-task types:** `TicketMessageDto.BodyHtml` (Task 1) is the JSON field `bodyHtml` read by `TicketMessage.bodyHtml` (Task 2); `MaxBodyHtmlLength` is defined once (`TicketWorkflowService`) and used by the mapper and the tests.
- **Known gaps:** no automated frontend test (by design); hostile-markup handling is verified manually.
