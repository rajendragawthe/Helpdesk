import { useMsal } from '@azure/msal-react'
import { useCurrentUser } from '../hooks/useCurrentUser'
import { Alert, AlertTitle, AlertDescription } from '@/components/ui/alert'

function HomePage() {
  const { accounts } = useMsal()
  const { user, error, notRegistered } = useCurrentUser()

  const displayName = user?.name ?? accounts[0]?.name ?? accounts[0]?.username ?? ''

  if (notRegistered) {
    return (
      <section className="flex grow flex-col items-center justify-center gap-3 px-8 py-16">
        <Alert variant="destructive" className="max-w-[560px]">
          <AlertTitle>Access not set up</AlertTitle>
          <AlertDescription>{error}</AlertDescription>
        </Alert>
      </section>
    )
  }

  return (
    <section className="flex grow flex-col items-center justify-center gap-3 px-8 py-16">
      <h1 className="my-8 text-[56px] leading-[120%] font-medium tracking-[-1.68px] text-text-h max-md:my-5 max-md:text-4xl">
        Welcome{displayName ? `, ${displayName}` : ''}
      </h1>
      <p>Your ticket queue will show up here in a later phase.</p>
      {error && (
        <Alert variant="destructive" className="max-w-[560px]">
          <AlertDescription>{error}</AlertDescription>
        </Alert>
      )}
    </section>
  )
}

export default HomePage
