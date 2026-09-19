## Problem
We receive several support emails daily. Human agents have to manually read, classify and respond to each ticket - which is slow and lead to impersonal, canned responses.

## Solution
Build a ticket management system that uses AI to automatically classify, respond to and route support tickets - delivering faster, more personalized responses to customer while freeing up agents for complex issues.

## Features
* Receive support emails (via Microsoft Graph API, dedicated inbox) and create tickets, with threading/multi-message history per ticket
* Auto-generate human-friendly draft responses using a knowledge base — always reviewed/approved by an agent before sending; agent replies go out via email only
* AI-powered ticket classification; low-confidence or unanswerable cases auto-escalate to a human queue
* Routing by fixed category-to-team mapping, with SLA due dates per category/priority
* Ticket status lifecycle: open / pending / resolved / closed, with SLA due-date tracking
* AI summaries
* AI-suggested replies
* Knowledge base: built into the system, CRUD-managed by admins/agents, used as grounding for AI replies
* Ticket list with filtering and sorting
* Ticket details view
* Notifications/alerts for agents (new ticket, SLA breach)
* Reporting/analytics: volume trends, AI accuracy, agent performance
* Dashboard to view and manage all tickets
* User management: Admin and Agent roles — agents see/act on tickets for their team; admins manage users, teams, and settings

## MVP Scope (build this first)
Core loop only:
1. Ingest emails from an Office 365 mailbox via Microsoft Graph API and create tickets
2. AI classifies and summarizes each ticket
3. AI drafts a reply using a simple hardcoded knowledge base (no KB ingestion/CRUD)
4. Agent sees a queue, reviews the AI draft, edits it, and sends via email
5. Auth: Microsoft/Entra SSO (same tenant as Graph API mailbox), Admin and Agent roles
6. User management: minimal — admin can add and list agents (no edit/delete/permissions UI)
7. Ticket states: New → In Review → Replied
8. Login: admin logs in first; admin creates agent accounts for other users

Explicitly skipped for MVP:
* KB ingestion / KB CRUD (KB is hardcoded)
* Full user management UI (edit/delete/roles beyond add+list)
* Dashboard / reporting / analytics
* Audit trail
* Auto-acknowledgement emails to customers
* Complex ticket states (SLA due dates, escalation queue, team routing, pending/closed/resolved distinctions)
* Notifications/alerts
* Filtering/sorting beyond a plain queue view
  
## Out of scope (v1)
* Multi-language support
* Formal data retention/PII policy work
* Integration with existing CRM/helpdesk systems

## Non-functional
* Expected volume: ~100 tickets/day
* LLM provider: OpenRouter