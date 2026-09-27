import { Component, type ErrorInfo, type ReactNode } from 'react'
import { reportClientError } from '@/lib/clientErrorReporter'

type Props = { children: ReactNode }
type State = { hasError: boolean }

/**
 * Catches render-time crashes anywhere below it in the tree, reports them, and shows a minimal
 * fallback instead of a blank white screen. Does not catch errors in event handlers or async
 * code (React error boundaries never do) - those are covered by the window-level handlers
 * registered in main.tsx.
 */
export class ErrorBoundary extends Component<Props, State> {
  state: State = { hasError: false }

  static getDerivedStateFromError(): State {
    return { hasError: true }
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    reportClientError({ message: error.message, stack: error.stack ?? info.componentStack ?? undefined })
  }

  render() {
    if (this.state.hasError) {
      return (
        <div style={{ padding: '2rem', textAlign: 'center' }}>
          <p>Something went wrong. Please reload the page.</p>
        </div>
      )
    }

    return this.props.children
  }
}
