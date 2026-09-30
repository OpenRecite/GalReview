import { request, adminRequest, json } from './client'
import type { AdminUser, AuthSessionResponse } from '../../types/api'

export function adminLogin(username: string, password: string): Promise<AuthSessionResponse> {
  return request('/admin/sessions', {
    method: 'POST',
    authenticated: false,
    body: json({ username, password }),
  })
}

export function adminLogout(sessionId: string): Promise<void> {
  return adminRequest(`/auth/sessions/${encodeURIComponent(sessionId)}`, {
    method: 'DELETE',
  })
}

export function listAdminUsers(): Promise<AdminUser[]> {
  return adminRequest('/admin/users')
}

export function deleteAdminUser(userId: string): Promise<void> {
  return adminRequest(`/admin/users/${encodeURIComponent(userId)}`, {
    method: 'DELETE',
  })
}

export function resetAdminUserPassword(userId: string, newPassword: string): Promise<void> {
  return adminRequest(`/admin/users/${encodeURIComponent(userId)}/password`, {
    method: 'POST', body: json({ newPassword }),
  })
}
