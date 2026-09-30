import { describe, expect, it } from 'vitest'
import { createUuidV4 } from './uuid'

describe('createUuidV4', () => {
  it('生成小写 UUID v4', () => {
    const id = createUuidV4()
    expect(id).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/)
  })

  it('连续调用不重复', () => {
    const a = createUuidV4()
    const b = createUuidV4()
    expect(a).not.toBe(b)
  })
})
