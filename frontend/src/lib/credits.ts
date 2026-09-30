import { ApiClientError } from './api'

/**
 * 购买地址只来自两个可信来源：后端返回的 `purchaseUrl`（必须是 https）
 * 或构建期注入的 `VITE_CREDIT_SHOP_URL`。两者都没有时返回 null——
 * 调用方应据此隐藏购买入口，而不是退回任何硬编码地址。
 */
export function resolvePurchaseUrl(details?: Record<string, unknown>): string | null {
  const fromApi = details?.purchaseUrl
  if (typeof fromApi === 'string' && fromApi.startsWith('https://')) return fromApi
  const fromEnv = import.meta.env.VITE_CREDIT_SHOP_URL as string | undefined
  if (fromEnv && fromEnv.trim()) return fromEnv.trim()
  return null
}

export function handleCreditsRequired(reason: unknown): boolean {
  if (!(reason instanceof ApiClientError) || reason.code !== 'CREDITS_INSUFFICIENT') return false
  const purchaseUrl = resolvePurchaseUrl(reason.details)
  // 没有配置购买入口时不弹窗、不跳转，返回 false 让调用方展示普通错误提示。
  if (!purchaseUrl) return false
  const balance = typeof reason.details.balance === 'number' ? reason.details.balance : null
  const required = typeof reason.details.required === 'number' ? reason.details.required : null
  const detail = balance !== null && required !== null
    ? `当前可用 ${balance.toFixed(5)} credits，本次至少需要 ${required.toFixed(5)} credits。`
    : '当前 credits 不足。'
  if (window.confirm(`${detail}\n需要先兑换 credits。是否前往购买页面？`)) {
    window.location.assign(purchaseUrl)
  }
  return true
}
