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
    const frame = frameRef.current
    const root = frame?.contentDocument?.documentElement
    if (!frame || !root) return
    // Collapse the frame first so the viewport is 0px tall: an email with html,body{height:100%} would otherwise
    // report the (old) viewport height as its content height and the frame could never shrink.
    const previous = frame.style.height
    frame.style.height = '0px'
    const measured = Math.ceil(root.scrollHeight) + 2
    if (measured > 2) {
      const next = Math.min(measured, MAX_HEIGHT)
      frame.style.height = `${next}px`
      setHeight(next)
    } else {
      frame.style.height = previous
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
