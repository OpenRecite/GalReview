import type { AuthSessionResponse, TokenPair, UserProfile } from '../types/api'

const SESSION_KEY = 'galreview.session'
const PROFILE_KEY = 'galreview.profile'

/**
 * 会话（含 Access/Refresh Token）存 sessionStorage：
 * 关闭标签页即失效，降低 XSS 后长期驻留 localStorage 的风险。
 * 跨标签共享与「记住我」需后续迁移到 HttpOnly Cookie + Gateway 下发。
 */
const sessionStore = (): Storage => sessionStorage

export type StoredSession = AuthSessionResponse

export function readSession(): StoredSession | null {
  try {
    const raw = sessionStore().getItem(SESSION_KEY)
    return raw ? (JSON.parse(raw) as StoredSession) : null
  } catch {
    return null
  }
}

export function saveSession(session: StoredSession): void {
  sessionStore().setItem(SESSION_KEY, JSON.stringify(session))
  window.dispatchEvent(new Event('galreview:session'))
}

export function updateSessionTokens(tokens: TokenPair): void {
  const current = readSession()
  if (current) saveSession({ ...current, tokens })
}

export function clearSession(): void {
  sessionStore().removeItem(SESSION_KEY)
  localStorage.removeItem(PROFILE_KEY)
  // 兼容旧版本误写入 localStorage 的会话
  localStorage.removeItem(SESSION_KEY)
  window.dispatchEvent(new Event('galreview:session'))
}

export function readProfile(): UserProfile | null {
  try {
    const raw = localStorage.getItem(PROFILE_KEY)
    return raw ? (JSON.parse(raw) as UserProfile) : null
  } catch {
    return null
  }
}

export function saveProfile(profile: UserProfile): void {
  localStorage.setItem(PROFILE_KEY, JSON.stringify(profile))
}
