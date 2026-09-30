/** ReviewPage 拆分用的纯函数与共享类型。不包含 React 状态。 */

import { readSession } from '../../lib/session'
import { createUuidV4 } from '../../lib/uuid'
import type { AnswerResult, GamePackage, ReviewSession } from '../../types/api'

export interface AttemptState {
  answers: AnswerResult[]
  attemptsByQuestion: Record<string, number>
}

export interface ChoiceFeedback {
  correct: boolean
  selectedText: string
  correctText: string | null
  knowledgeTitle: string
  nextSceneId: string | null
  nextVisitedSceneIds: string[]
  answers: AnswerResult[]
  savedSession?: ReviewSession
}

export interface ReviewKnowledgeSummary {
  pointId: string
  title: string
  masteryScore: number
  nextReviewAt: string | null
}

export function generationError(error: { message: string } | null): Error {
  return new Error(error?.message || '游戏生成失败。')
}

export function attemptsFromAnswers(answers: AnswerResult[]): Record<string, number> {
  return Object.fromEntries(answers.map((answer) => [answer.questionId, answer.attemptNumber]))
}

export function runtimeEvent(events: Array<Record<string, unknown>>, type: string) {
  return events.find((event) => event.type === type)
}

export function voiceAssetId(sceneIndex: number, lineIndex: number): string {
  return `voice-${String(sceneIndex).padStart(3, '0')}-${String(lineIndex).padStart(3, '0')}`
}

export function isSessionResumable(session: ReviewSession | undefined): boolean {
  return session?.status === 'CREATED' || session?.status === 'RUNNING'
}

export function formatReviewTime(value: string | null): string {
  if (!value) return '暂无安排'
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return '暂无安排'
  return new Intl.DateTimeFormat('zh-CN', {
    year: 'numeric',
    month: 'long',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  }).format(date)
}

export function createShellSession(gamePackage: GamePackage): ReviewSession {
  const userId = readSession()?.session.userId
  if (!userId) throw new Error('登录会话已失效，请重新登录。')
  return {
    sessionId: createUuidV4(),
    userId,
    packageId: gamePackage.packageId,
    reviewPlanId: gamePackage.reviewPlanId,
    snapshotVersion: gamePackage.snapshotVersion,
    status: 'CREATED',
    currentSceneId: null,
    progressVersion: 0,
    startedAt: new Date().toISOString(),
    completedAt: null,
  }
}
