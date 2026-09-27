/**
 * Reports a frontend crash to the backend's anonymous client-error endpoint. Deliberately does
 * not use apiFetch (which requires a signed-in MSAL account and throws otherwise) - a crash can
 * happen before sign-in, and reporting it must never itself throw or block the caller.
 */
import { buildApiUrl } from './apiUrl'

const MAX_MESSAGE_LENGTH = 2000
const MAX_STACK_LENGTH = 8000

export function reportClientError(error: { message: string; stack?: string }): void {
  const message = error.message?.trim() ? error.message.slice(0, MAX_MESSAGE_LENGTH) : '(no message)'

  const body = {
    message,
    stack: error.stack?.slice(0, MAX_STACK_LENGTH),
    // Origin + pathname only: the full href can carry MSAL redirect params (code/client_info/state)
    // in the hash, which can exceed the backend's 500-char limit and get silently dropped.
    url: window.location.origin + window.location.pathname,
    userAgent: navigator.userAgent,
  }

  fetch(buildApiUrl('/api/client-errors'), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  }).catch(() => {
    // Reporting failures must never surface as a second crash.
  })
}
