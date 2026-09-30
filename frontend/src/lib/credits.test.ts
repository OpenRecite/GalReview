import { afterEach, describe, expect, it, vi } from 'vitest'

vi.mock('./api', () => ({ ApiClientError: class ApiClientError extends Error {} }))

describe('resolvePurchaseUrl', () => {
  afterEach(() => {
    vi.resetModules()
    vi.unstubAllEnvs()
  })

  it('优先使用 API 返回的 https purchaseUrl', async () => {
    const { resolvePurchaseUrl } = await import('./credits')
    expect(resolvePurchaseUrl({ purchaseUrl: 'https://shop.example/a' })).toBe('https://shop.example/a')
  })

  it('拒绝非 https 的 API 地址', async () => {
    vi.stubEnv('VITE_CREDIT_SHOP_URL', '')
    const { resolvePurchaseUrl } = await import('./credits')
    expect(resolvePurchaseUrl({ purchaseUrl: 'http://evil.example' })).toBeNull()
  })

  it('无 API 地址且未配置环境变量时返回 null（购买入口应隐藏）', async () => {
    vi.stubEnv('VITE_CREDIT_SHOP_URL', '')
    const { resolvePurchaseUrl } = await import('./credits')
    expect(resolvePurchaseUrl({})).toBeNull()
    expect(resolvePurchaseUrl()).toBeNull()
  })

  it('回退到 VITE_CREDIT_SHOP_URL 环境变量', async () => {
    vi.stubEnv('VITE_CREDIT_SHOP_URL', 'https://shop.example/from-env')
    const { resolvePurchaseUrl } = await import('./credits')
    expect(resolvePurchaseUrl({})).toBe('https://shop.example/from-env')
  })

  it('API 地址优先于环境变量', async () => {
    vi.stubEnv('VITE_CREDIT_SHOP_URL', 'https://shop.example/from-env')
    const { resolvePurchaseUrl } = await import('./credits')
    expect(resolvePurchaseUrl({ purchaseUrl: 'https://shop.example/a' })).toBe('https://shop.example/a')
  })
})
