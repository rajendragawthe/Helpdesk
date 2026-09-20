import { Client } from 'pg'
import { TEST_DB } from './env'

// Reset the test database to a known-empty state before the run starts.
// Truncates app tables (not __EFMigrationsHistory) rather than dropping the
// database, so the schema/migrations stay in place and webServer's `dotnet
// run` (which starts before this global setup) never races a missing table.
export default async function globalSetup() {
  const client = new Client(TEST_DB)
  await client.connect()
  try {
    await client.query(
      'TRUNCATE TABLE "Classifications", "Messages", "Tickets", "Users" RESTART IDENTITY CASCADE;',
    )
  } finally {
    await client.end()
  }
}
