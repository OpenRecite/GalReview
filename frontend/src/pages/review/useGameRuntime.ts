/** WASM/JS 渲染运行时的加载、生命周期与渲染循环。 */

import { useEffect, useRef, useState } from 'react'
import { loadRuntime } from '../../lib/runtime'
import { errorMessage } from '../../lib/errorMessage'
import type { GamePackage, ReviewSession, RuntimeManifest, WasmAdapter } from '../../types/api'
import { isSessionResumable } from './helpers'

export function useGameRuntime(setRenderError: (message: string) => void) {
  const adapterRef = useRef<WasmAdapter | null>(null)
  const [adapterVersion, setAdapterVersion] = useState(0)
  const [runtimeManifest, setRuntimeManifest] = useState<RuntimeManifest>()

  useEffect(() => () => adapterRef.current?.dispose(), [])

  useEffect(() => {
    if (!adapterVersion) return
    let frameId = 0
    let previous = performance.now()
    const frame = (now: number) => {
      const adapter = adapterRef.current
      if (!adapter) return
      try {
        adapter.renderFrame(Math.min(100, Math.max(0, now - previous)))
      } catch (reason) {
        setRenderError(errorMessage(reason, '渲染运行时已停止。'))
        return
      }
      previous = now
      frameId = window.requestAnimationFrame(frame)
    }
    frameId = window.requestAnimationFrame(frame)
    return () => window.cancelAnimationFrame(frameId)
  }, [adapterVersion, setRenderError])

  /** 为给定会话挂载渲染运行时；会话不可恢复时抛错（不触碰旧 adapter）。 */
  async function loadRuntimeFor(manifest: RuntimeManifest, pack: GamePackage, reviewSession: ReviewSession) {
    if (!isSessionResumable(reviewSession)) {
      throw new Error('上一次复习会话已经结束，不能继续恢复，请重新生成故事。')
    }
    adapterRef.current?.dispose()
    adapterRef.current = await loadRuntime(manifest, pack, reviewSession)
    setAdapterVersion((value) => value + 1)
    setRuntimeManifest(manifest)
  }

  function disposeAdapter() {
    adapterRef.current?.dispose()
    adapterRef.current = null
  }

  return { adapterRef, adapterVersion, runtimeManifest, setRuntimeManifest, loadRuntimeFor, disposeAdapter }
}
