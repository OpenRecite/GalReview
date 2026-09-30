import { request, json } from './client'
import type { AuthSession, AuthSessionResponse } from '../../types/api'

export function login(email: string, password: string): Promise<AuthSessionResponse> {
  return request('/auth/sessions', {
    method: 'POST',
    authenticated: false,
    body: json({ email, password, deviceName: navigator.userAgent.slice(0, 120) }),
  })
}

export function register(input: { email: string; password: string; displayName: string }): Promise<AuthSessionResponse> {
  return request('/auth/registrations', {
    method: 'POST',
    authenticated: false,
    body: json({
      ...input,
      deviceName: navigator.userAgent.slice(0, 120),
    }),
  })
}

export function requestPasswordReset(email: string): Promise<void> {
  return request('/auth/password-reset-requests', {
    method: 'POST',
    authenticated: false,
    body: json({ email }),
  })
}

export function resetPassword(resetToken: string, newPassword: string): Promise<void> {
  return request('/auth/password-resets', {
    method: 'POST',
    authenticated: false,
    body: json({ resetToken, newPassword }),
  })
}

export function logout(sessionId: string): Promise<void> {
  return request(`/auth/sessions/${encodeURIComponent(sessionId)}`, { method: 'DELETE' })
}

export function getSession(sessionId: string): Promise<AuthSession> {
  return request(`/auth/sessions/${encodeURIComponent(sessionId)}`)
}

export function changePassword(currentPassword: string, newPassword: string): Promise<void> {
  return request('/auth/password-changes', { method: 'POST', body: json({ currentPassword, newPassword }) })
}

export function deleteAccount(currentPassword: string): Promise<void> {
  return request('/auth/account', { method: 'DELETE', body: json({ currentPassword }) })
}
