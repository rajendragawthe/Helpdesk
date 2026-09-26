import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { buildEmailDocument } from '@/lib/emailDocument'

const DEFAULT_HEIGHT = 240
const MAX_HEIGHT = 2000

type EmailHtmlViewerProps = {
  html: string
}

// Renders a customer's email HTML in a locked-down iframe. NEVER add `allow-scripts` (especially together with
// `allow-same-origin`), `allow-forms`, `allow-top-navigation*`, `allow-modals` or `allow-downloads` to `sandbox`:
// `allow-same-origin` is only acceptable because scripts stay disabled, and it lets us measure the content height.
function EmailHtmlViewer({ html }: EmailHtmlViewerProps) {
  const frameRef = useRef<HTMLIFrameElement>(null)
  const [height, setHeight] = useState(DEFAULT_HEIGHT)
  const srcDoc = useMemo(() => buildEmailDocument(html), [html])

  const measure = useCallback(() => {
    const body = frameRef.current?.contentDocument?.body
    if (!body) return
    const measured = Math.ceil(body.getBoundingClientRect().height) + 2
    if (measured > 2) {
      setHeight(Math.min(measured, MAX_HEIGHT))
    }
  }, [])

  useEffect(() => {
    window.addEventListener('resize', measure)
    return () => window.removeEventListener('resize', measure)
  }, [measure])

  return (
    <iframe
      ref={frameRef}
      title="Customer email"
      sandbox="allow-same-origin allow-popups allow-popups-to-escape-sandbox"
      referrerPolicy="no-referrer"
      srcDoc={srcDoc}
      onLoad={measure}
      style={{ height }}
      className="w-full rounded-md border bg-white"
    />
  )
}

export default EmailHtmlViewer
