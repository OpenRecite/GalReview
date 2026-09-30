import { request, adminRequest, json } from './client'
import type { AdminCreditCode, CreditBalance, CreateCreditCodeBatchInput } from '../../types/api'

export function getCreditBalance(): Promise<CreditBalance> {
  return request('/credits/balance')
}

export function redeemCredits(code: string): Promise<CreditBalance> {
  return request('/credits/redemptions', { method: 'POST', body: json({ code }) })
}

export function listAdminCreditCodes(): Promise<{ items: AdminCreditCode[] }> {
  return adminRequest('/admin/credit-codes')
}

export function createAdminCreditCodeBatch(input: CreateCreditCodeBatchInput): Promise<{ items: AdminCreditCode[] }> {
  return adminRequest('/admin/credit-codes/batches', {
    method: 'POST', body: json(input),
  })
}

export function revokeAdminCreditCode(codeId: string): Promise<void> {
  return adminRequest(`/admin/credit-codes/${encodeURIComponent(codeId)}`, {
    method: 'DELETE',
  })
}
