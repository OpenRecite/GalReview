/** BGM 循环与角色语音播放（含 BGM 压低/恢复与自动重试手势）。 */

import { useEffect, useRef, useState } from 'react'
import type { RefObject } from 'react'
import { api } from '../../lib/api'
import {
  BGM_DUCKED_VOLUME,
  BGM_VOLUME,
  CHARACTER_VOICE_PLAYBACK_RATE,
  CHARACTER_VOICE_VOLUME,
} from '../../lib/storyAssets'
import type { GamePackage, GameScene, ReviewResult, ReviewSession, WasmAdapter } from '../../types/api'
import { isSessionResumable, voiceAssetId } from './helpers'
import type { ChoiceFeedback } from './helpers'

interface AudioStageOptions {
  /** 场景、会话、adapter 与结果状态齐备且尚未结束时为 true；驱动 BGM 起停。 */
  gameplayActive: boolean
  adapterVersion: number
  adapterRef: RefObject<WasmAdapter | null>
  session: ReviewSession | undefined
  gamePackage: GamePackage | undefined
  scene: GameScene | undefined
  currentDialogue: GameScene['dialogue'][number] | undefined
  dialogueIndex: number
  choiceFeedback: ChoiceFeedback | undefined
  result: ReviewResult | undefined
  shellCompleted: boolean
}

export function useAudioStage(options: AudioStageOptions) {
  const {
    gameplayActive,
    adapterVersion,
    adapterRef,
    session,
    gamePackage,
    scene,
    currentDialogue,
    dialogueIndex,
    choiceFeedback,
    result,
    shellCompleted,
  } = options
  const bgmRef = useRef<HTMLAudioElement | null>(null)
  const voiceAudioRef = useRef<HTMLAudioElement | null>(null)
  const [voiceEnabled, setVoiceEnabled] = useState(true)
  const [voicePlaying, setVoicePlaying] = useState(false)

  useEffect(() => {
    if (!gameplayActive) return undefined
    const audio = new Audio('/bgm.mp3')
    let active = true
    audio.loop = true
    audio.preload = 'auto'
    audio.volume = BGM_VOLUME
    bgmRef.current = audio

    const startPlayback = () => {
      if (!active) return
      void audio.play().catch(() => {})
    }

    void audio.play().catch(() => {
      if (active) document.addEventListener('pointerdown', startPlayback, { once: true })
    })

    return () => {
      active = false
      document.removeEventListener('pointerdown', startPlayback)
      audio.pause()
      audio.currentTime = 0
      bgmRef.current = null
    }
  }, [gameplayActive])

  useEffect(() => {
    let active = true
    let objectUrl = ''
    let playback: HTMLAudioElement | null = null
    let retryOnGesture: (() => void) | null = null
    setVoicePlaying(false)

    if (!voiceEnabled
      || !adapterRef.current
      || !isSessionResumable(session)
      || !gamePackage
      || !scene
      || !currentDialogue
      || choiceFeedback
      || result
      || shellCompleted) {
      return undefined
    }

    const sceneIndex = gamePackage.scenes.findIndex((item) => item.sceneId === scene.sceneId)
    if (sceneIndex < 0) return undefined
    const assetId = voiceAssetId(sceneIndex, dialogueIndex)
    const asset = gamePackage.assets.find((item) => item.type === 'AUDIO' && item.assetId === assetId)
    if (!asset) return undefined

    const restoreBgm = () => {
      if (bgmRef.current) bgmRef.current.volume = BGM_VOLUME
    }
    const beginPlayback = () => {
      if (!active || !playback) return
      if (bgmRef.current) bgmRef.current.volume = BGM_DUCKED_VOLUME
      void playback.play().then(() => {
        if (active) setVoicePlaying(true)
      }).catch(() => {
        restoreBgm()
        if (active) setVoicePlaying(false)
      })
    }

    void api.getGameAudio(asset.uri).then((blob) => {
      if (!active) return
      objectUrl = URL.createObjectURL(blob)
      playback = new Audio(objectUrl)
      playback.preload = 'auto'
      playback.volume = CHARACTER_VOICE_VOLUME
      playback.playbackRate = CHARACTER_VOICE_PLAYBACK_RATE
      playback.preservesPitch = true
      voiceAudioRef.current = playback
      playback.addEventListener('ended', () => {
        restoreBgm()
        if (active) setVoicePlaying(false)
      }, { once: true })
      playback.addEventListener('error', () => {
        restoreBgm()
        if (active) setVoicePlaying(false)
      }, { once: true })
      void playback.play().then(() => {
        if (bgmRef.current) bgmRef.current.volume = BGM_DUCKED_VOLUME
        if (active) setVoicePlaying(true)
      }).catch(() => {
        restoreBgm()
        retryOnGesture = beginPlayback
        document.addEventListener('pointerdown', retryOnGesture, { once: true })
      })
    }).catch(() => {
      // Voice is optional. Text dialogue remains usable when audio cannot be loaded.
      restoreBgm()
    })

    return () => {
      active = false
      if (retryOnGesture) document.removeEventListener('pointerdown', retryOnGesture)
      playback?.pause()
      if (playback) playback.currentTime = 0
      if (voiceAudioRef.current === playback) voiceAudioRef.current = null
      if (objectUrl) URL.revokeObjectURL(objectUrl)
      restoreBgm()
    }
  }, [adapterVersion, choiceFeedback, currentDialogue, dialogueIndex, gamePackage, result, scene, session, shellCompleted, voiceEnabled, adapterRef])

  return { voiceEnabled, setVoiceEnabled, voicePlaying }
}
