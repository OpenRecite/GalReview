/** 生成设置面板：风格/难度选择与生成、恢复入口。 */

import { Link } from 'react-router'
import { clearCompletedReview } from '../../lib/workflow'
import type { Difficulty, GameGenerationJob, GameStyle } from '../../types/api'

interface SetupPanelProps {
  style: GameStyle
  difficulty: Difficulty
  onStyleChange(style: GameStyle): void
  onDifficultyChange(difficulty: Difficulty): void
  estimatedQuestionCount: number
  nodeCount: number
  busy: boolean
  /** 已有游戏包且会话可恢复 → 显示“恢复会话”。 */
  hasResumableRun: boolean
  /** 上一次会话已结束 → 只能返回研习册。 */
  hasFinishedSession: boolean
  generation: GameGenerationJob | undefined
  projectId: string
  onGenerate(): void
  onResumeGeneration(): void
  onResumeRuntime(): void
}

export default function SetupPanel({
  style,
  difficulty,
  onStyleChange,
  onDifficultyChange,
  estimatedQuestionCount,
  nodeCount,
  busy,
  hasResumableRun,
  hasFinishedSession,
  generation,
  projectId,
  onGenerate,
  onResumeGeneration,
  onResumeRuntime,
}: SetupPanelProps) {
  return (
    <section className="review-setup workspace-card" data-style={style.toLowerCase()}>
      <span className="review-setup__orb review-setup__orb--one" aria-hidden="true" />
      <span className="review-setup__orb review-setup__orb--two" aria-hidden="true" />
      <div className="review-setup__heading">
        <span className="section-label">生成设置</span>
        <h2>准备 视觉小说</h2>
        <p>选择故事氛围与挑战难度，生成一段只属于本次计划的互动复习。</p>
      </div>
      <div className="review-setup__summary" aria-label="本次复习概况">
        <div><span>预计题目</span><strong>{estimatedQuestionCount}</strong><small>道</small></div>
        <div><span>知识范围</span><strong>{nodeCount}</strong><small>个节点</small></div>
      </div>
      <div className="review-setup__controls">
        <label><span>故事风格</span><select value={style} onChange={(event) => onStyleChange(event.target.value as GameStyle)}><option value="CAMPUS">校园</option><option value="FANTASY">幻想</option><option value="SCIENCE">科幻</option></select></label>
        <label><span>挑战难度</span><select value={difficulty} onChange={(event) => onDifficultyChange(event.target.value as Difficulty)}><option value="BASIC">基础</option><option value="STANDARD">标准</option><option value="ADVANCED">进阶</option></select></label>
      </div>
      {hasResumableRun
        ? <button className="primary-button" type="button" disabled={busy} onClick={onResumeRuntime}>恢复会话</button>
        : hasFinishedSession
          ? <Link className="primary-button" to={`/projects/${projectId}`} onClick={() => clearCompletedReview()}>返回研习册</Link>
        : generation && generation.status !== 'FAILED'
          ? <button className="primary-button" type="button" disabled={busy} onClick={onResumeGeneration}>{busy ? '恢复中…' : `继续生成（${generation.progress}%）`}</button>
          : <button className="primary-button" type="button" disabled={busy} onClick={onGenerate}>{busy ? '准备中…' : '生成并开始'}</button>}
    </section>
  )
}
