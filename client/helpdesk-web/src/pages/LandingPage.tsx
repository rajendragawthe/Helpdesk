import { useMsal } from '@azure/msal-react'
import heroImg from '../assets/hero.png'
import { apiScopes } from '../authConfig'

function LandingPage() {
  const { instance } = useMsal()

  const login = () => instance.loginRedirect({ scopes: apiScopes })

  return (
    <section className="flex grow flex-col items-center gap-5 px-8 py-16">
      <img src={heroImg} className="w-[170px]" width="170" height="179" alt="" />
      <h1 className="my-8 text-[56px] leading-[120%] font-medium tracking-[-1.68px] text-text-h max-md:my-5 max-md:text-4xl">
        AI-assisted support, without losing the personal touch
      </h1>
      <p className="max-w-[560px] text-text">
        Helpdesk automatically classifies incoming support emails, drafts personalized
        replies from your knowledge base, and lets your agents review and send in
        seconds &mdash; so customers get faster answers and your team focuses on the
        issues that actually need a human.
      </p>
      <button
        type="button"
        onClick={login}
        className="cursor-pointer rounded-md border-2 border-transparent bg-accent px-6 py-2.5 text-base text-white transition-opacity hover:opacity-90 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent"
      >
        Sign in
      </button>
    </section>
  )
}

export default LandingPage
