import { useEffect, useState, useCallback } from 'react'
import { useMsal } from '@azure/msal-react'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { apiFetch } from '../api/apiFetch'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Badge } from '@/components/ui/badge'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'

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

      <Card className="mb-8">
        <CardHeader>
          <CardTitle>Add agent</CardTitle>
        </CardHeader>
        <CardContent>
          <form
            onSubmit={handleSubmit(onSubmit)}
            noValidate
            className="flex flex-wrap items-start gap-4"
          >
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="displayName">Display name</Label>
              <Input id="displayName" type="text" {...register('displayName')} />
              {errors.displayName && (
                <p role="alert" className="text-[13px] text-destructive">
                  {errors.displayName.message}
                </p>
              )}
            </div>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="email">Email</Label>
              <Input id="email" type="email" {...register('email')} />
              {errors.email && (
                <p role="alert" className="text-[13px] text-destructive">
                  {errors.email.message}
                </p>
              )}
            </div>
            <Button type="submit" disabled={isSubmitting} className="mt-5.5 self-end">
              {isSubmitting ? 'Adding…' : 'Add agent'}
            </Button>
          </form>
          {submitError && (
            <Alert variant="destructive" className="mt-4">
              <AlertDescription>{submitError}</AlertDescription>
            </Alert>
          )}
        </CardContent>
      </Card>

      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>Display name</TableHead>
            <TableHead>Email</TableHead>
            <TableHead>Role</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {users.map((u) => (
            <TableRow key={u.id}>
              <TableCell>{u.displayName}</TableCell>
              <TableCell>{u.email}</TableCell>
              <TableCell>
                <Badge variant={u.role === 0 ? 'default' : 'secondary'}>
                  {roleLabels[u.role] ?? u.role}
                </Badge>
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      {listError && (
        <Alert variant="destructive" className="mt-4">
          <AlertDescription>{listError}</AlertDescription>
        </Alert>
      )}
    </section>
  )
}

export default AdminUsersPage
