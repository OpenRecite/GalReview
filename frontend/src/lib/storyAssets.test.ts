import { describe, expect, it } from 'vitest'
import {
  BGM_VOLUME,
  CHARACTER_VOICE_VOLUME,
  fixedMockSceneBackgrounds,
  gameGenerationPollTimeoutMs,
} from './storyAssets'

describe('storyAssets constants', () => {
  it('故事资源常量在合理范围', () => {
    expect(BGM_VOLUME).toBeGreaterThan(0)
    expect(BGM_VOLUME).toBeLessThan(1)
    expect(CHARACTER_VOICE_VOLUME).toBeGreaterThan(0)
    expect(gameGenerationPollTimeoutMs).toBeGreaterThan(60_000)
    expect(fixedMockSceneBackgrounds.length).toBeGreaterThan(0)
  })
})
