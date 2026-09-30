import { request, requestBlob, json, collectPages, UPLOAD_TIMEOUT_MS, QUESTION_BANK_TIMEOUT_MS } from './client'
import { createUuidV4 } from '../uuid'
import type {
  StudyProject,
  PracticeProjectDetails,
  PracticeQuestion,
  PracticeQuestionKind,
  PracticeSession,
  PracticeAnswer,
  ExamPaper,
  PracticeJob,
  QuestionHelp,
  SharedPracticePackage,
} from '../../types/api'

export function listPracticeProjects(): Promise<{ items: StudyProject[]; nextCursor: string | null }> {
  return request('/practice-projects')
}

export function createPracticeProject(input: { name: string; subjectCode?: string; materialIds: string[]; graphId?: null }): Promise<StudyProject> {
  return request('/practice-projects', { method: 'POST', body: json(input) })
}

export function updatePracticeProject(project: StudyProject, input: { name?: string; subjectCode?: string; materialIds?: string[]; graphId?: string }): Promise<StudyProject> {
  return request(`/practice-projects/${encodeURIComponent(project.projectId)}`, {
    method: 'PATCH', body: json({ ...input, version: project.version }),
  })
}

export function getPracticeProject(projectId: string): Promise<PracticeProjectDetails> {
  return request(`/practice-projects/${encodeURIComponent(projectId)}`)
}

export function listPracticeQuestions(projectId: string): Promise<{ items: PracticeQuestion[]; nextCursor: string | null }> {
  return request(`/practice-projects/${encodeURIComponent(projectId)}/questions`)
}

export function createPracticeQuestion(projectId: string, input: {
  kind: PracticeQuestionKind; prompt: string; options: Array<{ id: string; text: string }>; correctAnswers: string[]
  explanation?: string; score: number; difficulty: number; knowledgePointId?: string; status: 'DRAFT' | 'READY'
}): Promise<PracticeQuestion> {
  return request(`/practice-projects/${encodeURIComponent(projectId)}/questions`, { method: 'POST', body: json({ ...input, sourceReferences: [] }) })
}

export function updatePracticeQuestion(question: PracticeQuestion, status: 'DRAFT' | 'READY'): Promise<PracticeQuestion> {
  return request(`/practice-questions/${encodeURIComponent(question.questionId)}`, {
    method: 'PATCH', body: json({ kind: question.kind, prompt: question.prompt, options: question.options,
      correctAnswers: question.correctAnswers, explanation: question.explanation, score: question.score,
      difficulty: question.difficulty, knowledgePointId: question.knowledgePointId,
      sourceReferences: question.sourceReferences, status, version: question.version }),
  })
}

export function createPracticeSession(input: { projectId: string; mode: 'RANDOM' | 'SMART_REVIEW' | 'EXAM'; questionCount?: number; examPaperId?: string; reviewPlanId?: string; snapshotVersion?: string }): Promise<PracticeSession> {
  return request('/practice-sessions', { method: 'POST', body: json({ ...input, kinds: [] }) })
}

export function getPracticeSession(sessionId: string): Promise<PracticeSession> {
  return request(`/practice-sessions/${encodeURIComponent(sessionId)}`)
}

export function savePracticeAnswer(sessionId: string, questionId: string, answer: string[], responseTimeMs: number, attemptNumber = 1): Promise<PracticeAnswer> {
  return request(`/practice-sessions/${encodeURIComponent(sessionId)}/answers/${encodeURIComponent(questionId)}`, {
    method: 'PUT', body: json({ answer, responseTimeMs, attemptNumber, idempotencyKey: createUuidV4() }),
  })
}

export function completePracticeSession(sessionId: string): Promise<{ session: PracticeSession; evidence: unknown }> {
  return request(`/practice-sessions/${encodeURIComponent(sessionId)}/completion`, { method: 'POST', body: json({ idempotencyKey: createUuidV4() }) })
}

export function createExamPaper(projectId: string, input: { title: string; questionCount: number; durationSeconds: number; reviewPlanId: string; snapshotVersion: string }): Promise<ExamPaper> {
  return request(`/practice-projects/${encodeURIComponent(projectId)}/exam-papers`, { method: 'POST', body: json(input) })
}

export function generatePracticeQuestions(projectId: string, input: { reviewPlanId: string; snapshotVersion: string; kinds: PracticeQuestionKind[]; targetCount?: number }): Promise<PracticeJob> {
  return request(`/practice-projects/${encodeURIComponent(projectId)}/question-generations`, {
    method: 'POST', body: json({ ...input, idempotencyKey: createUuidV4(), generatorVersion: 'recite-question-v2' }), timeoutMs: QUESTION_BANK_TIMEOUT_MS,
  })
}

export function importExam(projectId: string, materialId: string): Promise<PracticeJob> {
  return request('/exam-import-jobs', { method: 'POST', body: json({ projectId, materialId, idempotencyKey: createUuidV4() }), timeoutMs: UPLOAD_TIMEOUT_MS })
}

export function getQuestionHelp(questionId: string, generateExplanation = false): Promise<QuestionHelp> {
  return request(`/practice-questions/${encodeURIComponent(questionId)}/help`, { method: 'POST', body: json({ generateExplanation }) })
}

export function importPracticePackage(file: File, materialIds: string[]): Promise<{ project: StudyProject; importedQuestionCount: number; importedFromSchema: string; diagnostics: string[] }> {
  const body = new FormData(); body.append('file', file); materialIds.forEach((id) => body.append('materialIds', id))
  return request('/practice-packages/imports', { method: 'POST', body, timeoutMs: UPLOAD_TIMEOUT_MS })
}

export function exportPracticePackage(projectId: string): Promise<Blob> {
  return requestBlob(`/practice-projects/${encodeURIComponent(projectId)}/package`)
}

export function publishPracticePackage(projectId: string, version: string, visibility: 'PRIVATE' | 'UNLISTED' | 'PUBLIC'): Promise<SharedPracticePackage> {
  return request(`/practice-projects/${encodeURIComponent(projectId)}/publications`, { method: 'POST', body: json({ version, visibility }) })
}

export function listSharedPracticePackages(queryParam?: string): Promise<{ items: SharedPracticePackage[]; nextCursor: string | null }> {
  const params = new URLSearchParams(); if (queryParam?.trim()) params.set('query', queryParam.trim())
  return request(`/shared-practice-packages${params.size ? `?${params}` : ''}`)
}

export function getSharedPracticePackageContent(packageId: string): Promise<Blob> {
  return requestBlob(`/shared-practice-packages/${encodeURIComponent(packageId)}/content`)
}
