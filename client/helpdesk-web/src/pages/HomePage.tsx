import { useMsal } from '@azure/msal-react'
import { useCurrentUser } from '../hooks/useCurrentUser'

function HomePage() {
  const { accounts } = useMsal()
  const { user, error } = useCurrentUser()

  const displayName = user?.name ?? accounts[0]?.name ?? accounts[0]?.username ?? ''

  return (
    <section className="flex grow flex-col items-center justify-center gap-3 px-8 py-16">
      <h1 className="my-8 text-[56px] leading-[120%] font-medium tracking-[-1.68px] text-text-h max-md:my-5 max-md:text-4xl">
        Welcome{displayName ? `, ${displayName}` : ''}
      </h1>
      <p>Your ticket queue will show up here in a later phase.</p>
      {error && <p role="alert" className="text-danger">{error}</p>}
    </section>
  )
}

export default HomePage
