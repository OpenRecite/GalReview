/** 故事回响页面的静态资源常量与预加载。 */

export const fixedMockSceneBackgrounds = ['/bg.webp', '/bg_1.webp', '/bg2.webp', '/bg3.webp', '/bg4.webp']

export const BGM_VOLUME = 0.18
export const BGM_DUCKED_VOLUME = 0.06
export const CHARACTER_VOICE_VOLUME = 0.9
export const CHARACTER_VOICE_PLAYBACK_RATE = 1.3

/** 后端最多执行两次、每次 120 秒的叙事模型请求；为排队、校验与持久化预留充足余量。 */
export const gameGenerationPollTimeoutMs = 600_000

export function preloadBackground(source: string): Promise<void> {
  return new Promise((resolve) => {
    const image = new Image()
    image.decoding = 'async'
    const complete = () => {
      if (typeof image.decode === 'function') {
        void image.decode().catch(() => undefined).finally(resolve)
      } else {
        resolve()
      }
    }
    image.onload = complete
    image.onerror = () => resolve()
    image.src = source
    if (image.complete) complete()
  })
}
