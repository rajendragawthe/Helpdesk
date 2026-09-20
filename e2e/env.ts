export const FRONTEND_URL = process.env.E2E_FRONTEND_URL ?? 'http://localhost:5173'
export const BACKEND_URL = process.env.E2E_BACKEND_URL ?? 'http://localhost:5080'

export const TEST_DB = {
  host: process.env.E2E_DB_HOST ?? 'localhost',
  port: Number(process.env.E2E_DB_PORT ?? 5432),
  database: process.env.E2E_DB_NAME ?? 'helpdesk_test',
  user: process.env.E2E_DB_USER ?? 'helpdesk',
  password: process.env.E2E_DB_PASSWORD ?? 'helpdesk',
}

// .NET connection-string form, for the API's ConnectionStrings__DefaultConnection.
// Derived from TEST_DB so the values the app connects with and the values
// global-setup truncates can never drift apart.
export const TEST_DB_CONNECTION_STRING = `Host=${TEST_DB.host};Port=${TEST_DB.port};Database=${TEST_DB.database};Username=${TEST_DB.user};Password=${TEST_DB.password}`

// Real Entra ID app registration values from client/helpdesk-web/.env (see authConfig.ts).
// There is no test identity provider, so these are never used to talk to Entra directly in
// tests — the auth suite (tests/auth/) only uses them to shape the *keys* of a fabricated
// MSAL cache entry (see tests/auth/msal-mock.ts) so a page loads with `useIsAuthenticated()`
// already true, without a real interactive login. Override via E2E_AZURE_AD_* if the app
// registration ever changes, to keep this in sync with the frontend's .env.
export const AZURE_AD = {
  tenantId: process.env.E2E_AZURE_AD_TENANT_ID ?? 'fdcd3b62-88bb-4c4c-bc7e-fdf7f96d8eab',
  clientId: process.env.E2E_AZURE_AD_CLIENT_ID ?? '7136d9f7-9b51-43ab-b709-169766875c22',
  apiScope:
    process.env.E2E_AZURE_AD_API_SCOPE ??
    'api://d585cb8a-9620-4428-8d24-79d6abaf989d/access_as_user',
}
