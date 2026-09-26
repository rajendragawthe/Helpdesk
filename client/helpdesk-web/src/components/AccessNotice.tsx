import { useMsal } from '@azure/msal-react'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'

type AccessNoticeProps = {
  error: string | null
  notRegistered: boolean
}

function AccessNotice({ error, notRegistered }: AccessNoticeProps) {
  const { instance } = useMsal()

  return (
    <main className="flex grow flex-col items-center justify-center gap-4 px-8 py-16 text-center">
      <Alert variant="destructive" className="max-w-[560px] text-left">
        <AlertTitle>{notRegistered ? 'Access not set up' : 'Something went wrong'}</AlertTitle>
        <AlertDescription>{error ?? 'Your account could not be loaded.'}</AlertDescription>
      </Alert>
      <Button type="button" variant="outline" onClick={() => instance.logoutRedirect()}>
        Sign out
      </Button>
    </main>
  )
}

export default AccessNotice
