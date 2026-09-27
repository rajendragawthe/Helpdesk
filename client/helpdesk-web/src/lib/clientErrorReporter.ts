/**
 * Reports a frontend crash to the backend's anonymous client-error endpoint. Deliberately does
 * not use apiFetch (which requires a signed-in MSAL account and throws otherwise) - a crash can
 * happen before sign-in, and reporting it must never itself throw or block the caller.
 */
export function reportClientError(error: { message: string; stack?: string }): void {
  const body = {
    message: error.message,
    stack: error.stack,
    url: window.location.href,
    userAgent: navigator.userAgent,
  }

  fetch('/api/client-errors', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  }).catch(() => {
    // Reporting failures must never surface as a second crash.
  })
}
