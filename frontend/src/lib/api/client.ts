import { clearSession, readSession, updateSessionTokens } from '../session'
import { clearAdminSession, readAdminSession, updateAdminSessionTokens } from '../adminSession'
import { createUuidV4 } from '../uuid'
import type { ApiFailure, ApiSuccess, TokenPair } from '../../types/api'

const configuredBase = import.meta.env.VITE_API_BASE_URL?.trim() || '/api/v1'
const API_BASE_URL = configuredBase.replace(/\/$/, '')
const DEFAULT_TIMEOUT_MS = 30_000
export const UPLOAD_TIMEOUT_MS = 120_000
export const QUESTION_BANK_TIMEOUT_MS = 600_000

export class ApiClientError extends Error {
  readonly code: string
  readonly status: number
  readonly traceId?: string
  readonly details: Record<string, unknown>

  constructor(message: string, code: string, status: number, traceId?: string, details: Record<string, unknown> = {}) {
    super(message)
    this.name = 'ApiClientError'
    this.code = code
    this.status = status
    this.traceId = traceId
    this.details = details
  }
}

export interface RequestOptions extends RequestInit {
  authenticated?: boolean
  retryAfterRefresh?: boolean
  timeoutMs?: number
}

let refreshPromise: Promise<TokenPair> | null = null

const ERROR_MESSAGES: Record<string, string> = {
  AUTH_REQUIRED: '登录状态已失效，请重新登录。',
  TOKEN_EXPIRED: '登录状态已失效，请重新登录。',
  FORBIDDEN: '当前账户没有执行此操作的权限。',
  RESOURCE_NOT_FOUND: '请求的数据不存在或已被删除。',
  NOT_FOUND: '请求的数据不存在或已被删除。',
  VALIDATION_ERROR: '提交内容格式不正确，请检查后重试。',
  BUSINESS_RULE_VIOLATION: '当前内容不符合业务规则，请检查操作条件。',
  STATE_CONFLICT: '数据状态已经发生变化，请刷新后重试。',
  RATE_LIMITED: '操作过于频繁，请稍后再试。',
  FILE_TOO_LARGE: '文件大小超过允许上限。',
  MEDIA_TYPE_UNSUPPORTED: '暂不支持这种文件格式。',
  MATERIAL_TEXT_NOT_READY: '资料文字尚未提取完成，请稍后重试。',
  MATERIAL_TEXT_EXTRACTION_FAILED: '资料文字提取失败，请检查文件或 OCR 设置。',
  MATERIAL_ACCESS_DENIED: '当前账户无权访问这份资料。',
  FILE_SERVICE_UNAVAILABLE: '资料服务暂时不可用，请稍后重试。',
  REVIEW_PLAN_NOT_FOUND: '复习计划不存在或已失效。',
  REVIEW_PLAN_SNAPSHOT_MISMATCH: '复习计划版本已变化，请重新创建计划。',
  IDEMPOTENCY_KEY_REUSED: '请求标识已被用于不同操作，请重新发起。',
  CLIENT_CLOSED_REQUEST: '请求已取消。',
  SERVICE_UNAVAILABLE: '服务暂时不可用，请稍后重试。',
  UPSTREAM_CONTRACT_INVALID: '服务返回的数据格式异常，请联系维护人员。',
  PROFILE_DELETE_FAILED: '账户资料删除失败，账户尚未注销。',
  CREDITS_INSUFFICIENT: 'credits 不足，需要先兑换 credits。',
  REDEMPTION_CODE_UNAVAILABLE: '兑换码无效、已使用、已撤销或已过期。',
  INTERNAL_ERROR: '服务处理失败，请稍后重试。',
}

const STATUS_MESSAGES: Record<number, string> = {
  402: 'credits 不足，需要先兑换 credits。',
  400: '提交内容格式不正确，请检查后重试。',
  401: '登录状态已失效，请重新登录。',
  403: '当前账户没有执行此操作的权限。',
  404: '请求的数据不存在或已被删除。',
  409: '数据状态已经发生变化，请刷新后重试。',
  413: '文件大小超过允许上限。',
  415: '暂不支持这种文件格式。',
  422: '当前操作条件不满足，请检查输入内容。',
  429: '操作过于频繁，请稍后再试。',
  502: '上游服务响应异常，请稍后重试。',
  503: '服务暂时不可用，请稍后重试。',
  504: '服务响应超时，请稍后重试。',
}

function localizedErrorMessage(code: string, message: string, status: number): string {
  if (/[\u3400-\u9fff]/.test(message)) return message
  return ERROR_MESSAGES[code] || STATUS_MESSAGES[status] || '请求失败，请稍后重试。'
}

function resolveUrl(path: string): string {
  if (/^https?:\/\//i.test(path)) return path
  if (path.startsWith('/api/')) return path
  return `${API_BASE_URL}${path.startsWith('/') ? path : `/${path}`}`
}

/**
 * 该错误是否表示"服务端明确认定凭证无效"。只有这种情况才应清除本地会话；
 * 网络中断、超时、网关重启等瞬时故障（503/502）下刷新令牌通常仍然有效。
 */
function isAuthRejection(error: unknown): boolean {
  if (!(error instanceof ApiClientError)) return false
  if (error.status === 401 || error.status === 403) return true
  return error.code === 'AUTH_REQUIRED' || error.code === 'TOKEN_EXPIRED'
}

function errorFromPayload(payload: unknown, status: number, responseTraceId?: string): ApiClientError {
  if (payload && typeof payload === 'object' && 'error' in payload) {
    const failure = payload as ApiFailure
    if (failure.error && typeof failure.error.code === 'string' && typeof failure.error.message === 'string') {
      return new ApiClientError(
        localizedErrorMessage(failure.error.code, failure.error.message, status),
        failure.error.code,
        status,
        failure.traceId || responseTraceId,
        failure.error.details ?? {},
      )
    }
  }
  return new ApiClientError(STATUS_MESSAGES[status] || '请求失败，请稍后再试。', 'HTTP_ERROR', status, responseTraceId)
}

function traceIdFromResponse(response: Response): string | undefined {
  return response.headers.get('X-Correlation-Id')?.trim() || undefined
}

function nonJsonHttpError(response: Response, bodyText: string): ApiClientError {
  const status = response.status
  const traceId = traceIdFromResponse(response)
  let code = 'HTTP_ERROR'
  let message = `请求失败（HTTP ${status}）。`

  if (status === 413) {
    code = 'PAYLOAD_TOO_LARGE'
    message = '请求体超过入口代理允许的大小（HTTP 413）。'
  } else if (status === 502) {
    code = 'UPSTREAM_HTTP_ERROR'
    message = '入口代理无法连接上游服务（HTTP 502）。'
  } else if (status === 503) {
    code = 'SERVICE_UNAVAILABLE'
    message = '服务暂时不可用（HTTP 503）。'
  } else if (status === 504) {
    code = 'SERVICE_UNAVAILABLE'
    message = '入口代理等待上游服务超时（HTTP 504）。'
  }

  const normalizedBody = bodyText.replace(/\s+/g, ' ').trim()
  return new ApiClientError(message, code, status, traceId, {
    contentType: response.headers.get('Content-Type') || 'unknown',
    responseKind: /bad gateway/i.test(normalizedBody) ? 'BAD_GATEWAY' : 'NON_JSON',
  })
}

function parseResponseJson(response: Response, bodyText: string): unknown {
  try {
    return JSON.parse(bodyText) as unknown
  } catch {
    if (!response.ok) throw nonJsonHttpError(response, bodyText)
    throw new ApiClientError(
      '服务响应格式不符合契约。',
      'UPSTREAM_CONTRACT_INVALID',
      502,
      traceIdFromResponse(response),
      { contentType: response.headers.get('Content-Type') || 'unknown' },
    )
  }
}

async function refreshAccessToken(): Promise<TokenPair> {
  const refreshToken = readSession()?.tokens.refreshToken
  if (!refreshToken) throw new ApiClientError('登录状态已失效，请重新登录。', 'AUTH_REQUIRED', 401)
  if (!refreshPromise) {
    refreshPromise = request<TokenPair>('/auth/tokens', {
      method: 'POST',
      body: JSON.stringify({ refreshToken }),
      authenticated: false,
      retryAfterRefresh: false,
    }).then((tokens) => {
      updateSessionTokens(tokens)
      return tokens
    }).catch((error) => {
      // 只有服务端明确拒绝刷新令牌才清除会话。网关重启、超时等瞬时故障会被
      // 映射成 503，此时刷新令牌通常仍然有效，清除会话等于把用户无故踢下线。
      if (isAuthRejection(error)) clearSession()
      throw error
    }).finally(() => {
      refreshPromise = null
    })
  }
  return refreshPromise
}

export async function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const {
    authenticated = true,
    retryAfterRefresh = true,
    timeoutMs = DEFAULT_TIMEOUT_MS,
    ...init
  } = options
  const controller = new AbortController()
  const timeout = window.setTimeout(() => controller.abort(), timeoutMs)
  const headers = new Headers(init.headers)
  headers.set('Accept', 'application/json')
  headers.set('X-Correlation-Id', createUuidV4())
  if (init.body && !(init.body instanceof FormData)) headers.set('Content-Type', 'application/json')
  const token = readSession()?.tokens.accessToken
  if (authenticated && token) headers.set('Authorization', `Bearer ${token}`)

  try {
    const response = await fetch(resolveUrl(path), { ...init, headers, signal: controller.signal })
    const bodyText = response.status === 204 ? '' : await response.text()
    const payload = bodyText
      ? (parseResponseJson(response, bodyText) as ApiSuccess<T> | ApiFailure)
      : null

    if (response.status === 401 && authenticated && retryAfterRefresh) {
      await refreshAccessToken()
      return request<T>(path, { ...options, retryAfterRefresh: false })
    }
    if (!response.ok) {
      if (!payload) throw nonJsonHttpError(response, bodyText)
      throw errorFromPayload(payload, response.status, traceIdFromResponse(response))
    }
    if (!bodyText) return undefined as T
    if (!payload || typeof payload !== 'object' || !('data' in payload)) {
      throw new ApiClientError('服务响应格式不符合契约。', 'UPSTREAM_CONTRACT_INVALID', 502)
    }
    return payload.data as T
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') {
      throw new ApiClientError('请求超时，请稍后重试。', 'SERVICE_UNAVAILABLE', 503)
    }
    if (error instanceof TypeError) {
      throw new ApiClientError('无法连接服务，请确认后端已经启动。', 'SERVICE_UNAVAILABLE', 503)
    }
    throw error
  } finally {
    window.clearTimeout(timeout)
  }
}

export async function requestRawJson<T>(path: string): Promise<T> {
  const controller = new AbortController()
  const timeout = window.setTimeout(() => controller.abort(), DEFAULT_TIMEOUT_MS)
  const headers = new Headers({
    Accept: 'application/json',
    'X-Correlation-Id': createUuidV4(),
  })
  const token = readSession()?.tokens.accessToken
  if (token) headers.set('Authorization', `Bearer ${token}`)
  try {
    const response = await fetch(resolveUrl(path), { headers, signal: controller.signal })
    const bodyText = response.status === 204 ? '' : await response.text()
    const payload = bodyText ? parseResponseJson(response, bodyText) : null
    if (!response.ok) {
      if (!payload) throw nonJsonHttpError(response, bodyText)
      throw errorFromPayload(payload, response.status, traceIdFromResponse(response))
    }
    if (!payload || typeof payload !== 'object') {
      throw new ApiClientError('游戏包响应格式不符合契约。', 'UPSTREAM_CONTRACT_INVALID', 502)
    }
    return payload as T
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') {
      throw new ApiClientError('请求超时，请稍后重试。', 'SERVICE_UNAVAILABLE', 503)
    }
    if (error instanceof TypeError) {
      throw new ApiClientError('无法连接服务，请确认后端已经启动。', 'SERVICE_UNAVAILABLE', 503)
    }
    throw error
  } finally {
    window.clearTimeout(timeout)
  }
}

export async function requestBlob(path: string, retryAfterRefresh = true, accept = 'application/octet-stream', label = '文件'): Promise<Blob> {
  const controller = new AbortController()
  const timeout = window.setTimeout(() => controller.abort(), DEFAULT_TIMEOUT_MS)
  const headers = new Headers({
    Accept: accept,
    'X-Correlation-Id': createUuidV4(),
  })
  const token = readSession()?.tokens.accessToken
  if (token) headers.set('Authorization', `Bearer ${token}`)
  try {
    const response = await fetch(resolveUrl(path), { headers, signal: controller.signal })
    if (response.status === 401 && retryAfterRefresh) {
      await refreshAccessToken()
      return requestBlob(path, false, accept, label)
    }
    if (!response.ok) {
      const bodyText = await response.text()
      const contentType = response.headers.get('Content-Type') || ''
      const payload = bodyText && contentType.includes('json')
        ? parseResponseJson(response, bodyText)
        : null
      if (!payload) throw nonJsonHttpError(response, bodyText)
      throw errorFromPayload(payload, response.status, traceIdFromResponse(response))
    }
    return await response.blob()
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') {
      throw new ApiClientError(`${label}加载超时，请稍后重试。`, 'SERVICE_UNAVAILABLE', 503)
    }
    if (error instanceof TypeError) {
      throw new ApiClientError(`无法连接${label}服务。`, 'SERVICE_UNAVAILABLE', 503)
    }
    throw error
  } finally {
    window.clearTimeout(timeout)
  }
}

export function json(body: unknown): string {
  return JSON.stringify(body)
}

export function query(params: Record<string, string | number | undefined>): string {
  const search = new URLSearchParams()
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined) search.set(key, String(value))
  }
  const result = search.toString()
  return result ? `?${result}` : ''
}

function adminHeaders(): HeadersInit {
  const token = readAdminSession()?.tokens.accessToken
  return token ? { Authorization: `Bearer ${token}` } : {}
}

export async function adminRequest<T>(path: string, options: RequestOptions = {}, retry = true): Promise<T> {
  try {
    return await request<T>(path, {
      ...options,
      authenticated: false,
      retryAfterRefresh: false,
      headers: { ...adminHeaders(), ...options.headers },
    })
  } catch (reason) {
    const refreshToken = readAdminSession()?.tokens.refreshToken
    if (!(reason instanceof ApiClientError) || reason.status !== 401 || !retry || !refreshToken) throw reason
    try {
      const tokens = await request<TokenPair>('/auth/tokens', {
        method: 'POST', authenticated: false, retryAfterRefresh: false, body: json({ refreshToken }),
      })
      updateAdminSessionTokens(tokens)
      return adminRequest<T>(path, options, false)
    } catch (refreshError) {
      // 同 refreshAccessToken：瞬时故障不等于刷新令牌失效，不能清除管理端会话
      if (isAuthRejection(refreshError)) clearAdminSession()
      throw refreshError
    }
  }
}

export async function collectPages<T>(load: (cursor?: string) => Promise<{ items: T[]; nextCursor: string | null }>): Promise<T[]> {
  const items: T[] = []
  const seenCursors = new Set<string>()
  let cursor: string | undefined

  do {
    const page = await load(cursor)
    items.push(...page.items)
    if (!page.nextCursor) break
    if (seenCursors.has(page.nextCursor)) {
      throw new ApiClientError('分页游标重复，无法继续读取。', 'UPSTREAM_CONTRACT_INVALID', 502)
    }
    seenCursors.add(page.nextCursor)
    cursor = page.nextCursor
  } while (cursor)

  return items
}
