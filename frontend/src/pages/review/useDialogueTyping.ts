/** 对白打字机与推进：逐字显示、手动跳过、滚动跟随。 */

import { useEffect, useMemo, useRef, useState } from 'react'
import type { RefObject } from 'react'
import type { GameScene } from '../../types/api'

export function useDialogueTyping(sceneId: string, scene: GameScene | undefined, locked: boolean) {
  const [dialogueIndex, setDialogueIndex] = useState(0)
  const [typedLength, setTypedLength] = useState(0)
  const dialogueScrollRef = useRef<HTMLDivElement | null>(null)

  const currentDialogue = scene?.dialogue[dialogueIndex]
  const dialogueCharacters = useMemo(() => Array.from(currentDialogue?.text || ''), [currentDialogue?.text])
  const typedDialogue = dialogueCharacters.slice(0, typedLength).join('')
  const dialogueTyping = typedLength < dialogueCharacters.length
  const dialogueCompleted = !scene || dialogueIndex >= scene.dialogue.length - 1

  useEffect(() => {
    setDialogueIndex(0)
  }, [sceneId])

  useEffect(() => {
    setTypedLength(0)
    if (!dialogueCharacters.length) return undefined
    const timer = window.setInterval(() => {
      setTypedLength((current) => {
        const next = current + 1
        if (next >= dialogueCharacters.length) window.clearInterval(timer)
        return Math.min(next, dialogueCharacters.length)
      })
    }, 52)
    return () => window.clearInterval(timer)
  }, [dialogueCharacters])

  // 长对白打字时保持可见底部；用户手动上滑后不强行抢回滚动
  useEffect(() => {
    const el = dialogueScrollRef.current
    if (!el) return
    const nearBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 48
    if (nearBottom || typedLength <= 1) {
      el.scrollTop = el.scrollHeight
    }
  }, [typedLength, dialogueIndex, dialogueScrollRef])

  function advanceDialogue() {
    if (!scene || locked) return
    if (dialogueTyping) {
      setTypedLength(dialogueCharacters.length)
      return
    }
    if (dialogueCompleted) return
    setDialogueIndex((current) => Math.min(current + 1, scene.dialogue.length - 1))
  }

  return { dialogueIndex, currentDialogue, typedDialogue, dialogueTyping, dialogueCompleted, dialogueScrollRef, advanceDialogue }
}
