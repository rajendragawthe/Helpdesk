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
