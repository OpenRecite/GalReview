import { request, json, query, collectPages } from './client'
import { createUuidV4 } from '../uuid'
import type {
  Chapter,
  GraphBuildJob,
  KnowledgeGraphSummary,
  KnowledgeGraphPage,
  KnowledgePoint,
  KnowledgePointPage,
  KnowledgeRelation,
  KnowledgeRelationPage,
  MasteryRecord,
  MasteryRecordPage,
  PlanGraph,
} from '../../types/api'

export function createGraphBuild(studyProjectId: string, materialId: string, subjectHint?: string): Promise<GraphBuildJob> {
  return request('/knowledge-graph-builds', {
    method: 'POST',
    headers: { 'Idempotency-Key': createUuidV4() },
    body: json({
      materialId,
      studyProjectId,
      subjectHint: subjectHint?.trim().toUpperCase() || undefined,
      segmentationMode: 'AUTO',
      extractorVersion: 'knowledge-extractor-v3',
    }),
  })
}

export function getGraphBuild(buildId: string): Promise<GraphBuildJob> {
  return request(`/knowledge-graph-builds/${encodeURIComponent(buildId)}`)
}

export function getKnowledgeGraph(graphId: string): Promise<KnowledgeGraphSummary> {
  return request(`/knowledge-graphs/${encodeURIComponent(graphId)}`)
}

export function listKnowledgeGraphs(studyProjectId: string, cursor?: string): Promise<KnowledgeGraphPage> {
  return request(`/knowledge-graphs${query({ studyProjectId, limit: 100, cursor })}`)
}

export function getAllKnowledgeGraphs(studyProjectId: string): Promise<KnowledgeGraphSummary[]> {
  return collectPages<KnowledgeGraphSummary>((cursor) =>
    request(`/knowledge-graphs${query({ studyProjectId, limit: 100, cursor })}`),
  )
}

export function getChapters(graphId: string): Promise<Chapter[]> {
  return request(`/knowledge-graphs/${encodeURIComponent(graphId)}/chapters`)
}

export function getPoints(graphId: string, cursor?: string): Promise<KnowledgePointPage> {
  return request(`/knowledge-graphs/${encodeURIComponent(graphId)}/points${query({ limit: 100, cursor })}`)
}

export function getAllPoints(graphId: string): Promise<KnowledgePoint[]> {
  return collectPages<KnowledgePoint>((cursor) =>
    request(`/knowledge-graphs/${encodeURIComponent(graphId)}/points${query({ limit: 100, cursor })}`),
  )
}

export function getKnowledgePoint(pointId: string): Promise<KnowledgePoint> {
  return request(`/knowledge-points/${encodeURIComponent(pointId)}`)
}

export function getRelations(graphId: string, cursor?: string): Promise<KnowledgeRelationPage> {
  return request(`/knowledge-graphs/${encodeURIComponent(graphId)}/relations${query({ limit: 100, cursor })}`)
}

export function getAllRelations(graphId: string): Promise<KnowledgeRelation[]> {
  return collectPages<KnowledgeRelation>((cursor) =>
    request(`/knowledge-graphs/${encodeURIComponent(graphId)}/relations${query({ limit: 100, cursor })}`),
  )
}

export function getMasteryRecords(graphId: string, cursor?: string): Promise<MasteryRecordPage> {
  return request(`/mastery-records${query({ graphId, limit: 100, cursor })}`)
}

export function getAllMasteryRecords(graphId: string): Promise<MasteryRecord[]> {
  return collectPages<MasteryRecord>((cursor) =>
    request(`/mastery-records${query({ graphId, limit: 100, cursor })}`),
  )
}

export function createAssessmentPlan(
  graphId: string,
  chapterIds: string[],
  options: { maxQuestions?: number; coverageTarget?: number; maximumInferenceDepth?: number } = {},
): Promise<PlanGraph> {
  return request('/assessment-plans', {
    method: 'POST',
    body: json({
      graphId,
      chapterIds,
      maxQuestions: options.maxQuestions ?? 6,
      coverageTarget: options.coverageTarget ?? 0.8,
      maximumInferenceDepth: options.maximumInferenceDepth ?? 3,
    }),
  })
}

export function createLearningPlan(
  graphId: string,
  chapterIds: string[],
  options: { maxPoints?: number; maximumDependencyDepth?: number } = {},
): Promise<PlanGraph> {
  return request('/learning-plans', {
    method: 'POST',
    body: json({
      graphId,
      chapterIds,
      maxPoints: options.maxPoints ?? 12,
      maximumDependencyDepth: options.maximumDependencyDepth ?? 5,
    }),
  })
}
