/** 游戏舞台：场景背景、对白面板、选项列表与舞台操作。纯展示组件。 */

import type { RefObject } from 'react'
import LoadingIndicator from '../../components/LoadingIndicator'
import type { GameChoice, GameScene } from '../../types/api'
import type { ChoiceFeedback } from './helpers'

interface GameStageProps {
  stageRef: RefObject<HTMLElement | null>
  scene: GameScene
  backgroundsReady: boolean
  busy: boolean
  visitedCount: number
  metaLabel: string
  hasVoiceAssets: boolean
  voiceEnabled: boolean
  voicePlaying: boolean
  onToggleVoice(): void
  onEndReview(): void
  onToggleFullscreen(): void
  choiceFeedback: ChoiceFeedback | undefined
  currentDialogue: GameScene['dialogue'][number] | undefined
  typedDialogue: string
  dialogueTyping: boolean
  dialogueCompleted: boolean
  dialogueScrollRef: RefObject<HTMLDivElement | null>
  onAdvanceDialogue(): void
  onChoose(choice: GameChoice): void
  onContinueFeedback(): void
  onFinishScene(): void
}

export default function GameStage({
  stageRef,
  scene,
  backgroundsReady,
  busy,
  visitedCount,
  metaLabel,
  hasVoiceAssets,
  voiceEnabled,
  voicePlaying,
  onToggleVoice,
  onEndReview,
  onToggleFullscreen,
  choiceFeedback,
  currentDialogue,
  typedDialogue,
  dialogueTyping,
  dialogueCompleted,
  dialogueScrollRef,
  onAdvanceDialogue,
  onChoose,
  onContinueFeedback,
  onFinishScene,
}: GameStageProps) {
  return (
    <section className={`game-stage game-stage--${scene.sceneId}${backgroundsReady ? '' : ' game-stage--backgrounds-loading'}`} ref={stageRef}>
      {!backgroundsReady ? <LoadingIndicator className="game-stage__background-loading" label="场景加载中…" /> : null}
      <div className="game-stage__meta"><span>{metaLabel}</span><span>{visitedCount} 个场景已访问</span></div>
      <div className="game-stage__actions">
        <button className="game-stage__end" type="button" disabled={busy} onClick={onEndReview}>结束复习</button>
        {hasVoiceAssets ? <button
          className={`game-stage__voice${voicePlaying ? ' is-playing' : ''}`}
          type="button"
          onClick={onToggleVoice}
          aria-label={voiceEnabled ? '关闭角色语音' : '开启角色语音'}
          title={voiceEnabled ? '关闭角色语音' : '开启角色语音'}
          aria-pressed={voiceEnabled}
        >
          <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M5 10v4h4l5 4V6l-5 4H5Z" /><path d={voiceEnabled ? 'M17 9c1.3 1.6 1.3 4.4 0 6M19.5 6.5c3 3 3 8 0 11' : 'm17 9 5 6m0-6-5 6'} /></svg>
        </button> : null}
        <button className="game-stage__fullscreen" type="button" onClick={onToggleFullscreen} aria-label="切换全屏" title="切换全屏">
          <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 9V5h4M20 9V5h-4M4 15v4h4M20 15v4h-4" /></svg>
        </button>
      </div>
      <article
        className={`dialogue-panel${!choiceFeedback && (!dialogueCompleted || dialogueTyping) ? ' dialogue-panel--advance' : ''}${choiceFeedback ? ` dialogue-panel--feedback dialogue-panel--feedback-${choiceFeedback.correct ? 'correct' : 'incorrect'}` : ''}`}
        onClick={(event) => {
          if (choiceFeedback) return
          // 避免与滚动交互冲突：滚轮已 stopPropagation；点击仍推进剧情
          onAdvanceDialogue()
        }}
        onWheel={(event) => {
          // 阻止滚轮冒泡到外层，方便在对白框内上下滚动长文
          if ((event.target as HTMLElement).closest('.dialogue-line__text')) {
            event.stopPropagation()
          }
        }}
      >
        {scene.title ? <h2>{scene.title}</h2> : null}
        {choiceFeedback ? <div className="dialogue-line dialogue-line--feedback" role="status" aria-live="assertive">
          <strong>{choiceFeedback.correct ? '回答正确' : '回答错误'}</strong>
          <div className="dialogue-line__text" ref={dialogueScrollRef}>
            <p>{choiceFeedback.correct
              ? `你选择的「${choiceFeedback.selectedText}」是正确答案。这道题检验的是“${choiceFeedback.knowledgeTitle}”的关键判断。`
              : `你选择了「${choiceFeedback.selectedText}」。正确答案是「${choiceFeedback.correctText || '题目给出的正确选项'}」，请留意“${choiceFeedback.knowledgeTitle}”。`}</p>
          </div>
        </div> : currentDialogue ? <div className={`dialogue-line${currentDialogue.speakerId === '旁白' ? ' dialogue-line--narration' : ''}`}>
          {currentDialogue.speakerId !== '旁白' ? <strong>{currentDialogue.speakerId}</strong> : null}
          <div className="dialogue-line__text" ref={dialogueScrollRef}>
            <p className="dialogue-line__measure" aria-hidden="true">{currentDialogue.text}</p>
            <p className="dialogue-line__typed">{typedDialogue}</p>
          </div>
        </div> : null}
      </article>
      {choiceFeedback ? <div className="choice-list choice-list--feedback"><button disabled={busy} type="button" onClick={onContinueFeedback}>{choiceFeedback.nextSceneId ? '继续剧情' : '查看复习总结'}</button></div> : dialogueCompleted && !dialogueTyping ? (scene.choices.length ? <div className="choice-list">{scene.choices.map((choice) => <button key={choice.choiceId} disabled={busy} type="button" onClick={() => onChoose(choice)}>{choice.text}</button>)}</div> : <button className="primary-button" disabled={busy} type="button" onClick={onFinishScene}>前面的区域以后再来探索吧</button>) : null}
    </section>
  )
}
