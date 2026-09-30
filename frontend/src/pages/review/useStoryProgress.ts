/** 复习剧情推进：会话/场景/作答/反馈/结果状态，以及选择、保存进度与结算。 */

import { useEffect, useMemo, useRef, useState } from 'react'
import type { RefObject } from 'react'
import { api } from '../../lib/api'
import { errorMessage } from '../../lib/errorMessage'
import { createUuidV4 } from '../../lib/uuid'
import { readWorkflow, updateWorkflow, clearCompletedReview } from '../../lib/workflow'
import type {
  AnswerResult,
  GameChoice,
  GamePackage,
  MasteryRecord,
  ReviewResult,
  ReviewSession,
  RuntimeManifest,
  WasmAdapter,
} from '../../types/api'
import type { StudyWorkflow } from '../../lib/workflow'
import { attemptsFromAnswers, runtimeEvent } from './helpers'
import type { AttemptState, ChoiceFeedback, ReviewKnowledgeSummary } from './helpers'

interface StoryProgressOptions {
  initial: StudyWorkflow
  adapterRef: RefObject<WasmAdapter | null>
  runtimeManifest: RuntimeManifest | undefined
  busy: boolean
  setBusy(value: boolean): void
  setError(message: string): void
  setProgress(message: string): void
}

export function useStoryProgress(options: StoryProgressOptions) {
  const { initial, adapterRef, runtimeManifest, busy, setBusy, setError, setProgress } = options
  const resultKeyRef = useRef(initial.resultIdempotencyKey || createUuidV4())
  const startedAtRef = useRef(Date.now())
  const sceneStartedAtRef = useRef(Date.now())
  const [gamePackage, setGamePackage] = useState<GamePackage | undefined>(initial.gamePackage)
  const [session, setSession] = useState<ReviewSession | undefined>(initial.reviewSession)
  const initialSceneId = initial.reviewSession?.currentSceneId || initial.gamePackage?.entrySceneId || ''
  const [sceneId, setSceneId] = useState(initialSceneId)
  const [visitedSceneIds, setVisitedSceneIds] = useState<string[]>(initial.visitedSceneIds?.length ? initial.visitedSceneIds : initialSceneId ? [initialSceneId] : [])
  const [attempt, setAttempt] = useState<AttemptState>({
    answers: initial.answerResults || [],
    attemptsByQuestion: attemptsFromAnswers(initial.answerResults || []),
  })
  const [result, setResult] = useState<ReviewResult>()
  const [shellCompleted, setShellCompleted] = useState(false)
  const [choiceFeedback, setChoiceFeedback] = useState<ChoiceFeedback>()
  const [reviewKnowledge, setReviewKnowledge] = useState<ReviewKnowledgeSummary[]>([])
  const [reviewKnowledgeLoading, setReviewKnowledgeLoading] = useState(false)
  const [reviewKnowledgeError, setReviewKnowledgeError] = useState('')

  const scene = useMemo(
    () => gamePackage?.scenes.find((item) => item.sceneId === sceneId),
    [gamePackage, sceneId],
  )

  useEffect(() => {
    if (initial.plan && !initial.resultIdempotencyKey) updateWorkflow({ resultIdempotencyKey: resultKeyRef.current })
  }, [initial.plan, initial.resultIdempotencyKey])

  /** 恢复/挂载渲染运行时后进入目标场景，并记录访问轨迹。 */
  function enterScene(nextSceneId: string, nextVisited: string[]) {
    setSceneId(nextSceneId)
    setChoiceFeedback(undefined)
    setVisitedSceneIds(nextVisited)
    updateWorkflow({ visitedSceneIds: nextVisited })
    sceneStartedAtRef.current = Date.now()
  }

  /** 开始新一轮生成前清空上次的作答与反馈。 */
  function beginFreshRun() {
    setAttempt({ answers: [], attemptsByQuestion: {} })
    setShellCompleted(false)
    setChoiceFeedback(undefined)
    setReviewKnowledge([])
    setReviewKnowledgeError('')
  }

  /** 重新开始：清空剧情/会话/作答状态并滚动新的结果幂等键。 */
  function resetProgress() {
    resultKeyRef.current = createUuidV4()
    setGamePackage(undefined)
    setSession(undefined)
    setSceneId('')
    setVisitedSceneIds([])
    setAttempt({ answers: [], attemptsByQuestion: {} })
    setResult(undefined)
    setShellCompleted(false)
    setChoiceFeedback(undefined)
    setReviewKnowledge([])
    setReviewKnowledgeLoading(false)
    setReviewKnowledgeError('')
  }

  function markRunStarted() {
    startedAtRef.current = Date.now()
  }

  function createAnswer(choice: GameChoice, attemptId: string, occurredAt: string, event?: Record<string, unknown>): AnswerResult | null {
    if (choice.answerKind !== 'CHOICE' || typeof choice.correct !== 'boolean') return null
    const responseTimeMs = Math.max(0, Date.now() - sceneStartedAtRef.current)
    const attemptNumber = Math.max((attempt.attemptsByQuestion[choice.questionId] || 0) + 1, typeof event?.attemptNumber === 'number' ? event.attemptNumber : 1)
    const correct = typeof event?.correct === 'boolean' ? event.correct : choice.correct
    const quality = correct ? (attemptNumber > 1 ? 3 : 5) : 0
    return {
      attemptId,
      questionId: choice.questionId,
      knowledgePointId: choice.knowledgePointId,
      answerKind: 'CHOICE',
      choiceId: choice.choiceId,
      correct,
      quality,
      scoreDelta: choice.scoreDelta,
      responseTimeMs,
      hintsUsed: 0,
      attemptNumber,
      occurredAt,
    }
  }

  async function saveSceneProgress(nextSceneId: string, nextVisited: string[]): Promise<ReviewSession | undefined> {
    if (!session || !adapterRef.current) return undefined
    if (runtimeManifest?.reviewSessionsAvailable === false) {
      const nextSession: ReviewSession = {
        ...session,
        currentSceneId: nextSceneId,
        progressVersion: session.progressVersion + 1,
        status: 'RUNNING',
      }
      setSession(nextSession)
      updateWorkflow({ reviewSession: nextSession })
      return nextSession
    }
    const saved = await api.saveProgress(session.sessionId, {
      expectedVersion: session.progressVersion,
      currentSceneId: nextSceneId,
      visitedSceneIds: nextVisited,
      runtimeState: adapterRef.current.serializeState(),
    })
    const nextSession = { ...session, currentSceneId: saved.currentSceneId, progressVersion: saved.version, status: 'RUNNING' as const }
    setSession(nextSession)
    updateWorkflow({ reviewSession: nextSession })
    return nextSession
  }

  async function choose(choice: GameChoice) {
    if (!scene || !session || busy || choiceFeedback) return
    setBusy(true)
    setError('')
    try {
      const attemptId = createUuidV4()
      const occurredAt = new Date().toISOString()
      const events = adapterRef.current?.dispatchInput({ type: 'CHOICE_SELECTED', choiceId: choice.choiceId, attemptId, occurredAt }) || []
      if (adapterRef.current?.engine === 'wasm' && !events.length) {
        const runtimeError = adapterRef.current.lastError?.()
        throw new Error(runtimeError?.message || '渲染运行时拒绝了当前选择。')
      }
      const answer = createAnswer(choice, attemptId, occurredAt, runtimeEvent(events, 'ANSWER_RECORDED'))
      const nextAnswers = answer ? [...attempt.answers, answer] : attempt.answers
      if (answer) {
        setAttempt((current) => ({
          answers: [...current.answers, answer],
          attemptsByQuestion: { ...current.attemptsByQuestion, [answer.questionId]: answer.attemptNumber },
        }))
        updateWorkflow({ answerResults: nextAnswers })
      }
      if (runtimeManifest?.reviewSessionsAvailable !== false) {
        void api.appendEvents(session.sessionId, [{
          clientEventId: createUuidV4(),
          type: 'CHOICE_SELECTED',
          occurredAt,
          payload: {
            sceneId: scene.sceneId,
            choiceId: choice.choiceId,
            questionId: choice.questionId,
            knowledgePointId: choice.knowledgePointId,
          },
        }]).catch(() => undefined)
      }
      const entered = runtimeEvent(events, 'SCENE_ENTERED')
      const nextSceneId = typeof entered?.sceneId === 'string' ? entered.sceneId : choice.nextSceneId
      const completedByRuntime = Boolean(runtimeEvent(events, 'SESSION_COMPLETED'))
      let nextVisited = visitedSceneIds
      let savedSession: ReviewSession | undefined
      if (nextSceneId && !completedByRuntime) {
        nextVisited = visitedSceneIds.includes(nextSceneId) ? visitedSceneIds : [...visitedSceneIds, nextSceneId]
        savedSession = await saveSceneProgress(nextSceneId, nextVisited)
        setVisitedSceneIds(nextVisited)
        updateWorkflow({ visitedSceneIds: nextVisited })
      } else {
        savedSession = await saveSceneProgress(scene.sceneId, visitedSceneIds)
      }

      if (answer) {
        const correctChoice = scene.choices.find((item) => item.correct === true)
        const knowledgeTitle = initial.plan?.nodes.find((node) => node.pointId === answer.knowledgePointId)?.title || '当前知识点'
        setChoiceFeedback({
          correct: answer.correct,
          selectedText: choice.text,
          correctText: correctChoice?.text || null,
          knowledgeTitle,
          nextSceneId: nextSceneId && !completedByRuntime ? nextSceneId : null,
          nextVisitedSceneIds: nextVisited,
          answers: nextAnswers,
          savedSession,
        })
      } else if (nextSceneId && !completedByRuntime) {
        setSceneId(nextSceneId)
        sceneStartedAtRef.current = Date.now()
      } else {
        await finish(nextAnswers, savedSession)
      }
    } catch (reason) {
      setError(errorMessage(reason, '选择保存失败。'))
    } finally {
      setBusy(false)
    }
  }

  async function continueAfterFeedback() {
    if (!choiceFeedback || busy) return
    if (choiceFeedback.nextSceneId) {
      setVisitedSceneIds(choiceFeedback.nextVisitedSceneIds)
      setSceneId(choiceFeedback.nextSceneId)
      setChoiceFeedback(undefined)
      sceneStartedAtRef.current = Date.now()
      return
    }
    await finish(choiceFeedback.answers, choiceFeedback.savedSession)
  }

  async function loadReviewKnowledge(answers: AnswerResult[]) {
    const workflow = readWorkflow()
    const plan = workflow.plan || initial.plan
    if (!plan) return

    const reviewedPointIds = new Set(answers.map((answer) => answer.knowledgePointId))
    const visited = new Set(workflow.visitedSceneIds || visitedSceneIds)
    for (const reviewedScene of gamePackage?.scenes || []) {
      if (!visited.has(reviewedScene.sceneId)) continue
      for (const binding of reviewedScene.knowledgeBindings) reviewedPointIds.add(binding.knowledgePointId)
    }
    const reviewedNodes = plan.nodes.filter((node) => reviewedPointIds.has(node.pointId))
    const summaryNodes = reviewedNodes.length ? reviewedNodes : plan.nodes
    const fallback = summaryNodes.map((node) => ({
      pointId: node.pointId,
      title: node.title,
      masteryScore: node.masteryScore,
      nextReviewAt: null,
    }))
    setReviewKnowledge(fallback)
    setReviewKnowledgeLoading(true)
    setReviewKnowledgeError('')
    try {
      const records = await api.getAllMasteryRecords(plan.graphId)
      const recordByPointId = new Map<string, MasteryRecord>(records.map((record) => [record.pointId, record]))
      setReviewKnowledge(fallback.map((item) => {
        const record = recordByPointId.get(item.pointId)
        return record ? { ...item, masteryScore: record.score, nextReviewAt: record.nextReviewAt } : item
      }))
    } catch {
      setReviewKnowledgeError('最新熟练度获取失败，当前显示本次计划生成时的熟练度。')
    } finally {
      setReviewKnowledgeLoading(false)
    }
  }

  async function finish(answers = attempt.answers, sessionOverride?: ReviewSession) {
    const activeSession = sessionOverride || session
    if (!activeSession || !gamePackage) return
    setBusy(true)
    setError('')
    try {
      if (runtimeManifest?.reviewSessionsAvailable === false) {
        const completedSession = { ...activeSession, status: 'COMPLETED' as const, completedAt: new Date().toISOString() }
        setSession(completedSession)
        updateWorkflow({ reviewSession: completedSession })
        setShellCompleted(true)
        setProgress('C++/JS 基础壳已完成本地体验；本次结果未提交，掌握度不会更新。')
        clearCompletedReview()
        await loadReviewKnowledge(answers)
        return
      }
      const completed = await api.submitReviewResult(activeSession.sessionId, {
        expectedProgressVersion: activeSession.progressVersion,
        idempotencyKey: resultKeyRef.current,
        reviewPlanId: gamePackage.reviewPlanId,
        snapshotVersion: gamePackage.snapshotVersion,
        answerResults: answers,
        durationSeconds: Math.max(0, Math.round((Date.now() - startedAtRef.current) / 1_000)),
      })
      setResult(completed)
      const completedSession = { ...activeSession, status: 'COMPLETED' as const, completedAt: new Date().toISOString() }
      setSession(completedSession)
      updateWorkflow({ reviewSession: completedSession })
      setProgress(completed.status === 'DUPLICATE' ? '这次结果已经提交过。' : '本次复习结果已提交。')
      clearCompletedReview()
      await loadReviewKnowledge(answers)
    } catch (reason) {
      setError(errorMessage(reason, '结果提交失败。'))
    } finally {
      setBusy(false)
    }
  }

  async function finishCurrentScene() {
    if (!scene) return
    setBusy(true)
    try {
      const events = adapterRef.current?.dispatchInput({ type: 'ADVANCE' }) || []
      if (adapterRef.current?.engine === 'wasm' && !runtimeEvent(events, 'SESSION_COMPLETED')) {
        const runtimeError = adapterRef.current.lastError?.()
        throw new Error(runtimeError?.message || '渲染运行时无法结束当前场景。')
      }
      const savedSession = await saveSceneProgress(scene.sceneId, visitedSceneIds)
      await finish(attempt.answers, savedSession)
    } catch (reason) {
      setError(errorMessage(reason, '进度保存失败。'))
      setBusy(false)
    }
  }

  async function finishReviewEarly() {
    if (!session || !gamePackage) return
    await finish(attempt.answers, session)
  }

  return {
    gamePackage, setGamePackage,
    session, setSession,
    scene, sceneId,
    visitedSceneIds,
    attempt,
    result,
    shellCompleted,
    choiceFeedback,
    reviewKnowledge, reviewKnowledgeLoading, reviewKnowledgeError,
    resultKeyRef,
    enterScene, beginFreshRun, resetProgress, markRunStarted,
    choose, continueAfterFeedback, finish, finishCurrentScene, finishReviewEarly, loadReviewKnowledge,
  }
}
