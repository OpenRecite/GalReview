import { request, json } from './client'
import type { UserProfile, UserPreferences, UserPreferencesInput } from '../../types/api'

export function getCurrentUser(): Promise<UserProfile> {
  return request('/users/me')
}

export function updateCurrentUser(input: { displayName?: string; locale?: string; preferredSubjectCodes?: string[] }): Promise<UserProfile> {
  return request('/users/me', { method: 'PATCH', body: json(input) })
}

export function getUserPreferences(): Promise<UserPreferences> {
  return request('/users/me/preferences')
}

export function updateUserPreferences(input: UserPreferencesInput): Promise<UserPreferences> {
  return request('/users/me/preferences', { method: 'PUT', body: json(input) })
}
