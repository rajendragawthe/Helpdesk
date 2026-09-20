import { useEffect, useState, useCallback } from 'react'
import { useMsal } from '@azure/msal-react'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { apiFetch } from '../api/apiFetch'

const addAgentSchema = z.object({
  email: z.string().trim().min(1, 'Email is required').email('Enter a valid email address'),
  displayName: z.string().trim().min(1, 'Display name is required'),
})

type AddAgentForm = z.infer<typeof addAgentSchema>

type ApiUser = {
  id: string
  email: string
  displayName: string
  role: number
  createdAt: string
}

const roleLabels: Record<number, string> = { 0: 'Admin', 1: 'Agent' }

function AdminUsersPage() {
  const { instance } = useMsal()
  const [users, setUsers] = useState<ApiUser[]>([])
  const [listError, setListError] = useState<string | null>(null)
  const [submitError, setSubmitError] = useState<string | null>(null)

  const {
    register,
    handleSubmit,
    reset,
    formState: { errors, isSubmitting },
  } = useForm<AddAgentForm>({ resolver: zodResolver(addAgentSchema) })

  const loadUsers = useCallback(async () => {
    setListError(null)
    try {
      const response = await apiFetch(instance, '/api/users')
      if (!response.ok) {
        throw new Error(`Request failed: ${response.status} ${response.statusText}`)
      }
      setUsers(await response.json())
    } catch (err) {
      setListError(err instanceof Error ? err.message : 'Unknown error')
    }
  }, [instance])

  useEffect(() => {
    loadUsers()
  }, [loadUsers])

  const onSubmit = async (values: AddAgentForm) => {
    setSubmitError(null)
    try {
      const response = await apiFetch(instance, '/api/users', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(values),
      })
      if (response.status === 409) {
        setSubmitError(`A user with email '${values.email}' already exists.`)
        return
      }
      if (!response.ok) {
        throw new Error(`Request failed: ${response.status} ${response.statusText}`)
      }
      reset()
      await loadUsers()
    } catch (err) {
      setSubmitError(err instanceof Error ? err.message : 'Unknown error')
    }
  }

  return (
    <section className="px-8 py-10 text-left">
      <h1 className="mb-6 text-[32px]">Manage Agents</h1>

      <form
        onSubmit={handleSubmit(onSubmit)}
        noValidate
        className="mb-8 flex flex-wrap items-start gap-4"
      >
        <div className="flex flex-col gap-1">
          <label htmlFor="displayName" className="text-sm text-text">
            Display name
          </label>
          <input
            id="displayName"
            type="text"
            {...register('displayName')}
            className="rounded-md border border-border bg-bg px-2.5 py-2 text-[15px] text-text-h"
          />
          {errors.displayName && (
            <p role="alert" className="text-[13px] text-danger">
              {errors.displayName.message}
            </p>
          )}
        </div>
        <div className="flex flex-col gap-1">
          <label htmlFor="email" className="text-sm text-text">
            Email
          </label>
          <input
            id="email"
            type="email"
            {...register('email')}
            className="rounded-md border border-border bg-bg px-2.5 py-2 text-[15px] text-text-h"
          />
          {errors.email && (
            <p role="alert" className="text-[13px] text-danger">
              {errors.email.message}
            </p>
          )}
        </div>
        <button
          type="submit"
          disabled={isSubmitting}
          className="mt-5 cursor-pointer self-end rounded-md border-2 border-transparent bg-accent px-4.5 py-2.5 text-[15px] text-white disabled:cursor-default disabled:opacity-60"
        >
          {isSubmitting ? 'Adding…' : 'Add agent'}
        </button>
        {submitError && (
          <p role="alert" className="text-danger">
            {submitError}
          </p>
        )}
      </form>

      <table className="w-full border-collapse">
        <thead>
          <tr>
            <th className="border-b border-border px-3 py-2.5 text-left text-[13px] font-medium text-text">
              Display name
            </th>
            <th className="border-b border-border px-3 py-2.5 text-left text-[13px] font-medium text-text">
              Email
            </th>
            <th className="border-b border-border px-3 py-2.5 text-left text-[13px] font-medium text-text">
              Role
            </th>
          </tr>
        </thead>
        <tbody>
          {users.map((u) => (
            <tr key={u.id}>
              <td className="border-b border-border px-3 py-2.5">{u.displayName}</td>
              <td className="border-b border-border px-3 py-2.5">{u.email}</td>
              <td className="border-b border-border px-3 py-2.5">
                {roleLabels[u.role] ?? u.role}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {listError && (
        <p role="alert" className="text-danger">
          {listError}
        </p>
      )}
    </section>
  )
}

export default AdminUsersPage
