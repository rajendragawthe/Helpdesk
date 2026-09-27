/**
 * Prefixes a relative API path (e.g. "/api/tickets") with VITE_API_BASE_URL when set. In dev,
 * VITE_API_BASE_URL is unset and the Vite proxy (vite.config.ts) handles same-origin "/api"
 * requests, so this returns the path unchanged. In production the SPA (Static Web Apps) and the
 * API (App Service) are different origins, so every call needs the full API URL.
 */
export function buildApiUrl(path: string): string {
  const baseUrl = import.meta.env.VITE_API_BASE_URL
  if (!baseUrl) {
    return path
  }
  return `${baseUrl.replace(/\/$/, '')}${path}`
}
