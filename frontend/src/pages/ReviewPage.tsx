/** 回响（ReviewPage）：只负责编排——hooks 组合、生成/恢复流程与页面布局。 */

import { useEffect, useRef, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import AppShell, { PageHeader } from '../components/AppShell'
import LoadingIndicator from '../components/LoadingIndicator'
import { api } from '../lib/api'
import { handleCreditsRequired } from '../lib/credits'
import { pollUntil } from '../lib/poll'
import { errorMessage } from '../lib/errorMessage'
import { createUuidV4 } from '../lib/uuid'
import { clearCompletedReview, readWorkflow, updateWorkflow } from '../lib/workflow'
import { fixedMockSceneBackgrounds, gameGenerationPollTimeoutMs, preloadBackground } from '../lib/storyAssets'
import type {
  Difficulty,
  GameGenerationJob,
  GamePackage,
  GameStyle,
  ReviewSession,
  RuntimeManifest,
} from '../types/api'
import { useGameRuntime } from './review/useGameRuntime'
import { useStoryProgress } from './review/useStoryProgress'
import { useDialogueTyping } from './review/useDialogueTyping'
import { useAudioStage } from './review/useAudioStage'
import { createShellSession, generationError, isSessionResumable } from './review/helpers'
import GameStage from './review/GameStage'
import ResultPanel from './review/ResultPanel'
import SetupPanel from './review/SetupPanel'

export default function ReviewPage() {
  const { projectId = '' } = useParams()
  const [initial] = useState(() => {
    const saved = readWorkflow()
    // 兼容修复上线前已经留在 localStorage 中的结束会话，避免旧台词和语音复活。
    return saved.reviewSession && !isSessionResumable(saved.reviewSession)
      ? clearCompletedReview()
      : saved
  })
  const navigate = useNavigate()
  const gameStageRef = useRef<HTMLElement | null>(null)
  const [style, setStyle] = useState<GameStyle>(initial.gameStyle || 'CAMPUS')
  const [difficulty, setDifficulty] = useState<Difficulty>(initial.gameDifficulty || 'STANDARD')
  const [generation, setGeneration] = useState<GameGenerationJob | undefined>(initial.gameGeneration)
  const [busy, setBusy] = useState(false)
  const [progress, setProgress] = useState(initial.gamePackage ? '游戏包已准备好，正在等待渲染器。' : '选择风格后开始生成。')
  const [error, setError] = useState('')
  const [backgroundsReady, setBackgroundsReady] = useState(false)

  const runtime = useGameRuntime(setError)
  const story = useStoryProgress({
    initial,
    adapterRef: runtime.adapterRef,
    runtimeManifest: runtime.runtimeManifest,
    busy,
    setBusy,
    setError,
    setProgress,
  })
  const gameplayActive = Boolean(story.scene && story.gamePackage && isSessionResumable(story.session) && runtime.adapterVersion && !story.result && !story.shellCompleted)
  const dialogue = useDialogueTyping(story.sceneId, story.scene, Boolean(story.choiceFeedback))
  const audio = useAudioStage({
    gameplayActive,
    adapterVersion: runtime.adapterVersion,
    adapterRef: runtime.adapterRef,
    session: story.session,
    gamePackage: story.gamePackage,
    scene: story.scene,
    currentDialogue: dialogue.currentDialogue,
    dialogueIndex: dialogue.dialogueIndex,
    choiceFeedback: story.choiceFeedback,
    result: story.result,
    shellCompleted: story.shellCompleted,
  })

  useEffect(() => {
    let active = true
    void Promise.all(fixedMockSceneBackgrounds.map(preloadBackground)).then(() => {
      if (active) setBackgroundsReady(true)
    })
    return () => { active = false }
  }, [])

  async function attachRuntime(manifest: RuntimeManifest, pack: GamePackage, reviewSession: ReviewSession) {
    await runtime.loadRuntimeFor(manifest, pack, reviewSession)
    const nextSceneId = reviewSession.currentSceneId || pack.entrySceneId
    const savedVisited = readWorkflow().visitedSceneIds || []
    const nextVisited = savedVisited.includes(nextSceneId) ? savedVisited : [...savedVisited, nextSceneId]
    story.enterScene(nextSceneId, nextVisited)
  }

  async function completeGeneration(accepted: GameGenerationJob, plan = readWorkflow().plan) {
    if (!plan) throw new Error('当前复习计划不存在，请重新创建。')
    const completed = accepted.status === 'SUCCEEDED'
      ? accepted
      : await pollUntil(
        () => api.getGameGeneration(accepted.generationId),
        (job) => job.status === 'SUCCEEDED' || job.status === 'FAILED',
        (job) => {
          setGeneration(job)
          updateWorkflow({ gameGeneration: job })
          setProgress(`GalGame 正在生成… ${job.progress}%`)
        },
        gameGenerationPollTimeoutMs,
      )
    setGeneration(completed)
    updateWorkflow({ gameGeneration: completed })
    if (completed.status === 'FAILED') throw generationError(completed.error)
    if (!completed.packageId) throw new Error('生成任务完成但没有返回 packageId。')

    const manifest = await api.getGamePackage(completed.packageId)
    const pack = await api.getGamePackageContent(manifest.contentUrl)
    if (pack.reviewPlanId !== plan.reviewPlanId || pack.snapshotVersion !== plan.snapshotVersion) {
      throw new Error('游戏包与当前复习计划的不可变快照不一致。')
    }
    const renderManifest = await api.getRuntimeManifest()
    const reviewSession = renderManifest.reviewSessionsAvailable === false
      ? createShellSession(pack)
      : await api.createReviewSession(pack.packageId, renderManifest.wasmVersion)
    if (reviewSession.reviewPlanId !== pack.reviewPlanId || reviewSession.snapshotVersion !== pack.snapshotVersion) {
      throw new Error('复习会话与游戏包快照不一致。')
    }
    const resultIdempotencyKey = createUuidV4()
    story.resultKeyRef.current = resultIdempotencyKey
    story.beginFreshRun()
    story.setGamePackage(pack)
    story.setSession(reviewSession)
    updateWorkflow({ gameGeneration: completed, gameManifest: manifest, gamePackage: pack, reviewSession, visitedSceneIds: [reviewSession.currentSceneId || pack.entrySceneId], answerResults: [], resultIdempotencyKey })
    await attachRuntime(renderManifest, pack, reviewSession)
    setProgress('渲染器已加载，可以开始复习。')
    story.markRunStarted()
  }

  async function generateAndStart() {
    const workflow = readWorkflow()
    if (!workflow.plan) {
      setError('请先从资料页创建复习计划。')
      return
    }
    setBusy(true)
    setError('')
    try {
      setProgress('GalGame 正在根据计划组织剧情与题目…')
      const accepted = await api.createGameGeneration(workflow.plan, style, difficulty)
      setGeneration(accepted)
      updateWorkflow({ gameGeneration: accepted, gameStyle: style, gameDifficulty: difficulty, gameManifest: undefined, gamePackage: undefined, reviewSession: undefined, answerResults: [], resultIdempotencyKey: undefined })
      await completeGeneration(accepted, workflow.plan)
    } catch (reason) {
      if (!handleCreditsRequired(reason)) setError(errorMessage(reason, '游戏准备失败。'))
    } finally {
      setBusy(false)
    }
  }

  async function resumeGeneration() {
    const workflow = readWorkflow()
    if (!workflow.gameGeneration || !workflow.plan) return
    setBusy(true)
    setError('')
    setProgress('正在恢复游戏生成任务。')
    try {
      const current = await api.getGameGeneration(workflow.gameGeneration.generationId)
      await completeGeneration(current, workflow.plan)
    } catch (reason) {
      setError(errorMessage(reason, '生成任务恢复失败。'))
    } finally {
      setBusy(false)
    }
  }

  async function resumeRuntime() {
    const workflow = readWorkflow()
    if (!workflow.gamePackage || !workflow.reviewSession) return
    setBusy(true)
    setError('')
    try {
      const manifest = await api.getRuntimeManifest()
      const currentSession = manifest.reviewSessionsAvailable === false
        ? workflow.reviewSession
        : await api.getReviewSession(workflow.reviewSession.sessionId)
      if (!isSessionResumable(currentSession)) {
        resetGame()
        clearCompletedReview()
        navigate(projectId ? `/projects/${projectId}` : '/projects', { replace: true })
        return
      }
      await attachRuntime(manifest, workflow.gamePackage, currentSession)
      story.setSession(currentSession)
      setProgress('已恢复当前复习会话。')
    } catch (reason) {
      setError(errorMessage(reason, '会话恢复失败。'))
    } finally {
      setBusy(false)
    }
  }

  function resetGame() {
    runtime.disposeAdapter()
    runtime.setRuntimeManifest(undefined)
    story.resetProgress()
    setGeneration(undefined)
    setError('')
    setProgress('可以调整风格与难度，再生成一次。')
    updateWorkflow({ gameGeneration: undefined, gameManifest: undefined, gamePackage: undefined, reviewSession: undefined, visitedSceneIds: undefined, answerResults: undefined, resultIdempotencyKey: story.resultKeyRef.current })
  }

  async function restartReview() {
    if (busy || !initial.plan || !initial.graph) return
    setBusy(true); setError('')
    try {
      const nextPlan = await api.createAssessmentPlan(initial.graph.graphId, initial.plan.selectedChapterIds, {
        maxQuestions: Math.max(1, Math.min(50, initial.plan.estimatedQuestionCount || 6)), coverageTarget: 1, maximumInferenceDepth: 3,
      })
      resetGame()
      updateWorkflow({ plan: nextPlan, gameStyle: style, gameDifficulty: difficulty })
      setProgress(`正在按最新掌握度重新检索 ${nextPlan.selectedChapterIds.length} 个章节…`)
    } catch (reason) {
      setError(errorMessage(reason, '新一轮故事复习准备失败。')); setBusy(false); return
    }
    setBusy(false)
    await generateAndStart()
  }

  async function toggleGameFullscreen() {
    const stage = gameStageRef.current
    if (!stage) return
    try {
      if (document.fullscreenElement) await document.exitFullscreen()
      else await stage.requestFullscreen()
    } catch {
      setError('当前浏览器无法进入全屏模式。')
    }
  }

  if (!initial.plan || !projectId || initial.projectId !== projectId) {
    return <AppShell><main className="page review-page"><PageHeader title="回响" /><section className="empty-state"><h2>请从研习册开启故事复习</h2><Link className="button button--primary" to={projectId ? `/projects/${projectId}` : '/projects'}>返回研习册</Link></section></main></AppShell>
  }

  const showSetup = !story.gamePackage || !isSessionResumable(story.session) || !runtime.adapterRef.current

  return (
    <AppShell>
    <main className={`page review-page${showSetup ? ' review-page--setup' : ''}`}>
      <PageHeader title={initial.material?.displayName || '本次复习'} description={`${initial.plan.type === 'ASSESSMENT' ? '全面测试' : '章节学习'} · ${initial.plan.nodes.length} 个知识节点`} />

      {story.result || story.shellCompleted ? (
        <ResultPanel
          shellCompleted={story.shellCompleted}
          result={story.result}
          answerCount={story.attempt.answers.length}
          busy={busy}
          reviewKnowledge={story.reviewKnowledge}
          reviewKnowledgeLoading={story.reviewKnowledgeLoading}
          reviewKnowledgeError={story.reviewKnowledgeError}
          projectId={projectId}
          onRestart={() => void restartReview()}
        />
      ) : showSetup ? (
        <SetupPanel
          style={style}
          difficulty={difficulty}
          onStyleChange={setStyle}
          onDifficultyChange={setDifficulty}
          estimatedQuestionCount={initial.plan.estimatedQuestionCount}
          nodeCount={initial.plan.nodes.length}
          busy={busy}
          hasResumableRun={Boolean(story.gamePackage && isSessionResumable(story.session))}
          hasFinishedSession={Boolean(story.session && !isSessionResumable(story.session))}
          generation={generation}
          projectId={projectId}
          onGenerate={() => void generateAndStart()}
          onResumeGeneration={() => void resumeGeneration()}
          onResumeRuntime={() => void resumeRuntime()}
        />
      ) : story.scene ? (
        <GameStage
          stageRef={gameStageRef}
          scene={story.scene}
          backgroundsReady={backgroundsReady}
          busy={busy}
          visitedCount={story.visitedSceneIds.length}
          metaLabel={`${runtime.runtimeManifest?.wasmVersion} · ${runtime.adapterRef.current?.engine === 'wasm' ? 'WASM' : '兼容模式'}`}
          hasVoiceAssets={Boolean(story.gamePackage?.assets.some((asset) =>
            asset.type === 'AUDIO' && asset.assetId.startsWith('voice-'),
          ))}
          voiceEnabled={audio.voiceEnabled}
          voicePlaying={audio.voicePlaying}
          onToggleVoice={() => audio.setVoiceEnabled((enabled) => !enabled)}
          onEndReview={() => void story.finishReviewEarly()}
          onToggleFullscreen={() => void toggleGameFullscreen()}
          choiceFeedback={story.choiceFeedback}
          currentDialogue={dialogue.currentDialogue}
          typedDialogue={dialogue.typedDialogue}
          dialogueTyping={dialogue.dialogueTyping}
          dialogueCompleted={dialogue.dialogueCompleted}
          dialogueScrollRef={dialogue.dialogueScrollRef}
          onAdvanceDialogue={dialogue.advanceDialogue}
          onChoose={(choice) => void story.choose(choice)}
          onContinueFeedback={() => void story.continueAfterFeedback()}
          onFinishScene={() => void story.finishCurrentScene()}
        />
      ) : <section className="workspace-card"><h2>场景不存在</h2><p>游戏包没有找到当前场景，请重新生成。</p></section>}

      {busy && showSetup && !error
        ? <LoadingIndicator className="page-loading-transition" label={progress} compact />
        : <p className={error ? 'status-line status-line--error' : 'status-line'} aria-live="polite">{error || progress}</p>}
    </main>
    </AppShell>
  )
}
