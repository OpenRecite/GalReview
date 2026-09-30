import { describe, expect, it } from 'vitest'
import { errorMessage } from './errorMessage'

describe('errorMessage', () => {
  it('优先使用 Error.message', () => {
    expect(errorMessage(new Error('网络超时'))).toBe('网络超时')
  })

  it('接受非空字符串', () => {
    expect(errorMessage('自定义失败')).toBe('自定义失败')
  })

  it('回退到默认文案', () => {
    expect(errorMessage(undefined)).toBe('操作失败，请稍后重试。')
    expect(errorMessage(new Error('   '))).toBe('操作失败，请稍后重试。')
    expect(errorMessage(null, '备用')).toBe('备用')
  })
})
