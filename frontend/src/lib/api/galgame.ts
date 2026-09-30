import { request, requestRawJson, requestBlob, json } from './client'
import type {
  AnswerResult,
  GameGenerationJob,
  GamePackage,
  GamePackageManifest,
  GameStyle,
  Difficulty,
  PlanGraph,
  ProgressSnapshot,
  ReviewResult,
  ReviewSession,
  RuntimeManifest,
} from '../../types/api'

export function getReviewPlan(reviewPlanId: string): Promise<PlanGraph> {
  return request(`/review-plans/${encodeURIComponent(reviewPlanId)}`)
}

export function createGameGeneration(plan: PlanGraph, style: GameStyle, difficulty: Difficulty): Promise<GameGenerationJob> {
  return request('/game-generations', {
    method: 'POST',
    // 比 Gateway 超时略长，优先展示后端的结构化失败信息。
    timeoutMs: 65_000,
    body: json({
      reviewPlanId: plan.reviewPlanId,
      snapshotVersion: plan.snapshotVersion,
      style,
      difficulty,
      locale: 'zh-CN',
    }),
  })
}

export function getGameGeneration(generationId: string): Promise<GameGenerationJob> {
  return request(`/game-generations/${encodeURIComponent(generationId)}`)
}

export function getGamePackage(packageId: string): Promise<GamePackageManifest> {
  return request(`/game-packages/${encodeURIComponent(packageId)}`)
}

export function getGamePackageContent(contentUrl: string): Promise<GamePackage> {
  return requestRawJson<GamePackage>(contentUrl)
}

export function getGameAudio(audioUrl: string): Promise<Blob> {
  return requestBlob(audioUrl, true, 'audio/wav, audio/*', '语音')
}

export function getRuntimeManifest(): Promise<RuntimeManifest> {
  return request('/render-runtime/manifest', { authenticated: false })
}

export function createReviewSession(packageId: string, clientRuntimeVersion: string): Promise<ReviewSession> {
  return request('/review-sessions', {
    method: 'POST',
    body: json({ packageId, clientRuntimeVersion }),
  })
}

export function getReviewSession(sessionId: string): Promise<ReviewSession> {
  return request(`/review-sessions/${encodeURIComponent(sessionId)}`)
}

export function saveProgress(
  sessionId: string,
  input: { expectedVersion: number; currentSceneId: string; visitedSceneIds: string[]; runtimeState: Record<string, unknown> },
): Promise<ProgressSnapshot> {
  return request(`/review-sessions/${encodeURIComponent(sessionId)}/progress`, {
    method: 'PUT',
    body: json(input),
  })
}

export function appendEvents(sessionId: string, events: Array<{ clientEventId: string; type: string; occurredAt: string; payload: Record<string, unknown> }>): Promise<{ accepted: number; duplicates: number }> {
  return request(`/review-sessions/${encodeURIComponent(sessionId)}/events`, {
    method: 'POST',
    body: json({ events }),
  })
}

export function submitReviewResult(
  sessionId: string,
  input: {
    expectedProgressVersion: number
    idempotencyKey: string
    reviewPlanId: string
    snapshotVersion: string
    answerResults: AnswerResult[]
    durationSeconds: number
  },
): Promise<ReviewResult> {
  return request(`/review-sessions/${encodeURIComponent(sessionId)}/result`, {
    method: 'PUT',
    body: json(input),
  })
}
