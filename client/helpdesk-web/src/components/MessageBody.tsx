import { useState } from 'react'
import type { TicketMessage } from '../api/tickets'
import EmailHtmlViewer from './EmailHtmlViewer'
import { Button } from '@/components/ui/button'

type MessageBodyProps = {
  message: TicketMessage
}

type View = 'formatted' | 'text'

// Customer emails with HTML default to the formatted (sandboxed) view; everything else stays plain text.
function MessageBody({ message }: MessageBodyProps) {
  const [view, setView] = useState<View>('formatted')

  if (!message.bodyHtml) {
    return <p className="whitespace-pre-wrap text-sm">{message.bodyText}</p>
  }

  return (
    <div className="flex flex-col gap-2">
      <div className="flex gap-1">
        <Button
          type="button"
          size="sm"
          variant={view === 'formatted' ? 'secondary' : 'ghost'}
          onClick={() => setView('formatted')}
        >
          Formatted
        </Button>
        <Button
          type="button"
          size="sm"
          variant={view === 'text' ? 'secondary' : 'ghost'}
          onClick={() => setView('text')}
        >
          Plain text
        </Button>
      </div>
      {view === 'formatted' ? (
        <EmailHtmlViewer html={message.bodyHtml} />
      ) : (
        <p className="whitespace-pre-wrap text-sm">{message.bodyText}</p>
      )}
    </div>
  )
}

export default MessageBody
