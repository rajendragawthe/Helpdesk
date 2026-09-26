import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { useMsal } from '@azure/msal-react'
import { apiFetch } from '../api/apiFetch'
import { errorMessage, type TicketFilter, type TicketListItem } from '../api/tickets'

export type ListState = { items: TicketListItem[] | null; error: string | null }
type Lists = Record<TicketFilter, ListState>

const EMPTY: ListState = { items: null, error: null }
const INITIAL: Lists = { queue: EMPTY, mine: EMPTY, all: EMPTY }

type TicketsContextValue = {
  lists: Lists
  load: (filter: TicketFilter) => Promise<void>
  refresh: () => Promise<void>
}

// Outside a provider the hooks are inert, so a page that calls useTickets() still renders.
const INERT: TicketsContextValue = { lists: INITIAL, load: async () => {}, refresh: async () => {} }

const TicketsContext = createContext<TicketsContextValue>(INERT)

export function TicketsProvider({ children }: { children: ReactNode }) {
  const { instance } = useMsal()
  const [lists, setLists] = useState<Lists>(INITIAL)
  const requestIds = useRef<Record<TicketFilter, number>>({ queue: 0, mine: 0, all: 0 })
  const loaded = useRef(new Set<TicketFilter>())

  const load = useCallback(
    async (filter: TicketFilter) => {
      loaded.current.add(filter)
      const requestId = ++requestIds.current[filter]
      try {
        const response = await apiFetch(instance, `/api/tickets?filter=${filter}`)
        if (!response.ok) {
          throw new Error(await errorMessage(response))
        }
        const items = (await response.json()) as TicketListItem[]
        // A newer request for the same filter supersedes this one: ignore the stale response.
        if (requestId === requestIds.current[filter]) {
          setLists((prev) => ({ ...prev, [filter]: { items, error: null } }))
        }
      } catch (err) {
        if (requestId === requestIds.current[filter]) {
          const message = err instanceof Error ? err.message : 'Unknown error'
          setLists((prev) => ({ ...prev, [filter]: { items: prev[filter].items, error: message } }))
        }
      }
    },
    [instance],
  )

  const refresh = useCallback(async () => {
    await Promise.all([...loaded.current].map((filter) => load(filter)))
  }, [load])

  // The rail badge needs the queue as soon as the shell mounts.
  useEffect(() => {
    void load('queue')
  }, [load])

  const value = useMemo(() => ({ lists, load, refresh }), [lists, load, refresh])

  return <TicketsContext.Provider value={value}>{children}</TicketsContext.Provider>
}

export function useTickets(): TicketsContextValue {
  return useContext(TicketsContext)
}
