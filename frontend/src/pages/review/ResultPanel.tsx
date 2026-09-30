/** 复习完成结算面板：知识点熟练度汇总与后续动作。 */

import { Link } from 'react-router'
import LoadingIndicator from '../../components/LoadingIndicator'
import type { ReviewResult } from '../../types/api'
import { formatReviewTime } from './helpers'
import type { ReviewKnowledgeSummary } from './helpers'

function CompletionActionIcon({ kind }: { kind: 'restart' | 'graph' | 'plan' }) {
  if (kind === 'restart') return <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4.5 9A8 8 0 1 1 5 16.2" /><path d="M4.5 4.5V9H9" /></svg>
  if (kind === 'graph') return <svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="5" r="2.3" /><circle cx="6" cy="17" r="2.3" /><circle cx="18" cy="17" r="2.3" /><path d="m10.9 7-3.8 7.9M13.1 7l3.8 7.9M8.3 17h7.4" /></svg>
  return <svg viewBox="0 0 24 24" aria-hidden="true"><rect x="4" y="4" width="16" height="16" rx="4" /><path d="M12 8v8M8 12h8" /></svg>
}

interface ResultPanelProps {
  shellCompleted: boolean
  result: ReviewResult | undefined
  answerCount: number
  busy: boolean
  reviewKnowledge: ReviewKnowledgeSummary[]
  reviewKnowledgeLoading: boolean
  reviewKnowledgeError: string
  projectId: string
  onRestart(): void
}

export default function ResultPanel({
  shellCompleted,
  result,
  answerCount,
  busy,
  reviewKnowledge,
  reviewKnowledgeLoading,
  reviewKnowledgeError,
  projectId,
  onRestart,
}: ResultPanelProps) {
  return (
    <section className="review-complete workspace-card">
      <p>复习完成</p>
      <h2>{shellCompleted ? '本地体验已完成' : result?.status === 'ACCEPTED' ? '结果已提交' : '结果已去重'}</h2>
      <p>{shellCompleted ? `本地记录 ${answerCount} 条作答。当前 RenderService 仅提供基础壳，本次结果没有提交，熟练度不会更新。` : `共记录 ${answerCount} 条作答证据。`}</p>
      <div className="review-summary">
        <div className="review-summary__heading"><h3>本次复习知识点</h3>{reviewKnowledgeLoading ? <LoadingIndicator label="正在同步最新熟练度…" compact /> : null}</div>
        {reviewKnowledge.length ? <ul>{reviewKnowledge.map((item) => <li key={item.pointId}>
          <div><strong>{item.title}</strong><span>熟练度 {Math.round(item.masteryScore)} / 100</span></div>
          <p>下次复习：{formatReviewTime(item.nextReviewAt)}</p>
        </li>)}</ul> : <p>本轮没有记录到知识点。</p>}
        {reviewKnowledgeError ? <p className="review-summary__error">{reviewKnowledgeError}</p> : null}
      </div>
      <div className="completion-actions" aria-label="复习完成后操作">
        <button className="completion-action completion-action--restart" type="button" disabled={busy} onClick={onRestart}>
          <span className="completion-action__icon"><CompletionActionIcon kind="restart" /></span>
          <span className="completion-action__copy"><strong>{busy ? '正在准备新一轮…' : '重新复习'}</strong><small>沿用当前章节，重新生成剧情</small></span>
        </button>
        <Link className="completion-action completion-action--graph" to="/knowledge-graph">
          <span className="completion-action__icon"><CompletionActionIcon kind="graph" /></span>
          <span className="completion-action__copy"><strong>查看知识图谱</strong><small>回到知识结构，查看掌握情况</small></span>
        </Link>
        <Link className="completion-action completion-action--plan" to={`/projects/${projectId}`}>
          <span className="completion-action__icon"><CompletionActionIcon kind="plan" /></span>
          <span className="completion-action__copy"><strong>返回研习册</strong><small>改选章节或换一种温习方式</small></span>
        </Link>
      </div>
    </section>
  )
}
