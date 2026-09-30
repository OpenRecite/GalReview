import type { Page, Route } from '@playwright/test'

/**
 * T11 E2E 的 API mock 层。
 *
 * 全部请求走 `page.route('** /api/v1/**')` 拦截，返回与
 * frontend/src/lib/api/client.ts 契约一致的成功信封
 * `{ data, meta, traceId }`，因此冒烟不依赖本机起后端。
 *
 * 状态是可变的（create 项目后列表会变、答题后 session 会带上 answers），
 * 以便「创建项目 → 答题 → 看结果」全链路在同一次浏览器会话内连贯通过。
 */

const TRACE_ID = 'e2e-smoke-trace'

export const ids = {
  userId: '11111111-1111-4111-8111-111111111111',
  authSessionId: '22222222-2222-4222-8222-222222222222',
  materialId: '33333333-3333-4333-8333-333333333333',
  existingProjectId: '44444444-4444-4444-8444-444444444444',
  createdProjectId: '55555555-5555-4555-8555-555555555555',
  graphId: '66666666-6666-4666-8666-666666666666',
  buildId: '77777777-7777-4777-8777-777777777777',
  chapterId: '88888888-8888-4888-8888-888888888888',
  pointId: '99999999-9999-4999-8999-999999999999',
  questionId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
  planId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
  practiceSessionId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
  attemptId: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
}

const NOW = '2026-09-30T08:00:00.000Z'
const PROJECT_NAME = 'E2E 冒烟研习册'
const QUESTION_PROMPT = '冒烟测试用题：正确的选项是哪一个？'
const OPTION_A_TEXT = '正确答案选项'
const OPTION_B_TEXT = '干扰选项'

export const fixtures = {
  projectName: PROJECT_NAME,
  questionPrompt: QUESTION_PROMPT,
  optionAText: OPTION_A_TEXT,
  optionBText: OPTION_B_TEXT,
  /** 选项 A 是正确答案 */
  correctOptionId: 'A',
}

function envelope(data: unknown): string {
  return JSON.stringify({ data, meta: {}, traceId: TRACE_ID })
}

function json(data: unknown, status = 200) {
  return { status, contentType: 'application/json', body: envelope(data) }
}

function failure(status: number, code: string, message: string) {
  return {
    status,
    contentType: 'application/json',
    body: JSON.stringify({ data: null, error: { code, message, details: {} }, traceId: TRACE_ID }),
  }
}

function makeMaterial() {
  return {
    materialId: ids.materialId,
    ownerUserId: ids.userId,
    displayName: 'E2E 冒烟资料',
    originalFileName: 'e2e-smoke.md',
    mediaType: 'text/markdown',
    sizeBytes: 256,
    checksum: 'sha256-e2e',
    status: 'READY',
    latestIngestionJobId: null,
    ocrUsed: false,
    createdAt: NOW,
    updatedAt: NOW,
  }
}

function makeProject(projectId: string, withGraph: boolean) {
  return {
    projectId,
    ownerUserId: ids.userId,
    name: projectId === ids.createdProjectId ? 'E2E 新建研习册' : PROJECT_NAME,
    subjectCode: 'CS_DS',
    materialIds: [ids.materialId],
    graphId: withGraph ? ids.graphId : null,
    questionBankId: `qb-${projectId}`,
    status: 'ACTIVE',
    version: 1,
    createdAt: NOW,
    updatedAt: NOW,
  }
}

function makeQuestion(projectId: string) {
  return {
    questionId: ids.questionId,
    projectId,
    questionBankId: `qb-${projectId}`,
    kind: 'SINGLE_CHOICE',
    prompt: QUESTION_PROMPT,
    options: [
      { id: 'A', text: OPTION_A_TEXT },
      { id: 'B', text: OPTION_B_TEXT },
    ],
    correctAnswers: ['A'],
    explanation: '选项 A 与资料原文一致。',
    score: 5,
    difficulty: 2,
    knowledgePointId: ids.pointId,
    sourceReferences: [],
    status: 'READY',
    version: 1,
    createdAt: NOW,
    updatedAt: NOW,
  }
}

function makeSessionQuestion() {
  return {
    questionId: ids.questionId,
    kind: 'SINGLE_CHOICE',
    prompt: QUESTION_PROMPT,
    options: [
      { id: 'A', text: OPTION_A_TEXT },
      { id: 'B', text: OPTION_B_TEXT },
    ],
    score: 5,
    difficulty: 2,
    knowledgePointId: ids.pointId,
  }
}

function makeGraph(studyProjectId: string) {
  return {
    graphId: ids.graphId,
    materialId: ids.materialId,
    studyProjectId,
    version: 1,
    subjectCode: 'CS_DS',
    chapterCount: 1,
    pointCount: 1,
    relationCount: 0,
    status: 'READY',
    textChecksum: 'sha256-e2e-text',
    createdAt: NOW,
  }
}

function makeChapter() {
  return {
    chapterId: ids.chapterId,
    graphId: ids.graphId,
    parentChapterId: null,
    title: '第一章 冒烟章节',
    ordinal: 1,
    depth: 0,
    startOffset: 0,
    endOffset: 128,
    segmentationMode: 'AUTO',
  }
}

function makePoint() {
  return {
    pointId: ids.pointId,
    graphId: ids.graphId,
    chapterId: ids.chapterId,
    conceptKey: 'smoke-point',
    title: '冒烟知识点',
    summary: 'E2E 冒烟用知识点',
    subjectCode: 'CS_DS',
    tags: ['冒烟'],
    confidence: 0.9,
    sourceReferences: [],
    mastery: {
      userId: ids.userId,
      pointId: ids.pointId,
      score: 0.4,
      reason: 'seed',
      repetitions: 0,
      easinessFactor: 2.5,
      intervalDays: 1,
      nextReviewAt: NOW,
      lastReviewedAt: null,
      lapses: 0,
      version: 1,
    },
    createdAt: NOW,
    updatedAt: NOW,
  }
}

function makePlan(reviewPlanId: string, estimatedQuestionCount: number) {
  return {
    schemaVersion: '1.0',
    reviewPlanId,
    type: 'ASSESSMENT',
    status: 'OPEN',
    graphId: ids.graphId,
    graphVersion: 1,
    ownerUserId: ids.userId,
    selectedChapterIds: [ids.chapterId],
    snapshotVersion: 'snap-e2e-1',
    algorithmVersion: 'plan-e2e-v1',
    nodes: [
      {
        pointId: ids.pointId,
        chapterId: ids.chapterId,
        title: '冒烟知识点',
        summary: 'E2E 冒烟用知识点',
        tags: ['冒烟'],
        masteryScore: 0.4,
        role: 'TARGET',
        weight: 1,
        selectionReason: 'e2e',
        dependencyDepth: 0,
        questionTarget: true,
        outsideRequestedChapters: false,
        coversPointIds: [ids.pointId],
        supportsPointIds: [],
      },
    ],
    edges: [],
    rootPointIds: [ids.pointId],
    estimatedQuestionCount,
    estimatedCoverage: 1,
    totalWeight: 1,
    createdAt: NOW,
    expiresAt: '2026-10-01T08:00:00.000Z',
  }
}

function makeAnswer() {
  return {
    attemptId: ids.attemptId,
    questionId: ids.questionId,
    gradingStatus: 'DECIDED',
    outcome: 'PERFECT',
    correct: true,
    similarity: 0.95,
    quality: 1,
    awardedScore: 5,
    answerJudgeVersion: 'judge-e2e-v1',
    abstainReason: null,
    facets: [],
  }
}

interface MockState {
  projects: ReturnType<typeof makeProject>[]
  practiceSessionAnswers: ReturnType<typeof makeAnswer>[]
  practiceSessionStatus: 'ACTIVE' | 'COMPLETED'
  /** 图谱归属册：立册链路会校验 studyProjectId === 当前 projectId */
  graphOwnerId: string
}

function newPracticeSession(state: MockState) {
  state.practiceSessionAnswers = []
  state.practiceSessionStatus = 'ACTIVE'
  return {
    sessionId: ids.practiceSessionId,
    projectId: state.projects[state.projects.length - 1]?.projectId ?? ids.existingProjectId,
    mode: 'SMART_REVIEW',
    questions: [makeSessionQuestion()],
    answers: [],
    durationSeconds: null,
    status: 'ACTIVE' as const,
    createdAt: NOW,
    completedAt: null,
  }
}

function practiceSessionPayload(state: MockState) {
  return {
    sessionId: ids.practiceSessionId,
    projectId: state.projects[state.projects.length - 1]?.projectId ?? ids.existingProjectId,
    mode: 'SMART_REVIEW',
    questions: [makeSessionQuestion()],
    answers: state.practiceSessionAnswers,
    durationSeconds: state.practiceSessionStatus === 'COMPLETED' ? 30 : null,
    status: state.practiceSessionStatus,
    createdAt: NOW,
    completedAt: state.practiceSessionStatus === 'COMPLETED' ? NOW : null,
  }
}

/**
 * 在 page 上安装全部 API mock。
 * `withExistingProject=true` 时列表预置一份带图谱的研习册（进入旧册路径）；
 * 创建项目路径对两种预置都兼容。
 */
export async function installApiMocks(page: Page, options: { withExistingProject?: boolean } = {}) {
  const withExistingProject = options.withExistingProject ?? true
  const state: MockState = {
    projects: withExistingProject ? [makeProject(ids.existingProjectId, true)] : [],
    practiceSessionAnswers: [],
    practiceSessionStatus: 'ACTIVE',
    // 既有册预置时图谱归属既有册；立册链路会改写为新建册
    graphOwnerId: ids.existingProjectId,
  }

  await page.route('**/api/v1/**', async (route: Route) => {
    const url = new URL(route.request().url())
    const method = route.request().method()
    const path = url.pathname.replace(/^\/api\/v1/, '')

    // ---- auth ----
    if (method === 'POST' && path === '/auth/sessions') {
      return route.fulfill(json({
        session: {
          sessionId: ids.authSessionId,
          userId: ids.userId,
          status: 'ACTIVE',
          createdAt: NOW,
          expiresAt: '2026-10-01T08:00:00.000Z',
        },
        tokens: {
          accessToken: 'e2e-access-token',
          refreshToken: 'e2e-refresh-token',
          tokenType: 'Bearer',
          expiresInSeconds: 3600,
        },
      }))
    }
    if (method === 'GET' && path === `/auth/sessions/${ids.authSessionId}`) {
      return route.fulfill(json({
        sessionId: ids.authSessionId,
        userId: ids.userId,
        status: 'ACTIVE',
        createdAt: NOW,
        expiresAt: '2026-10-01T08:00:00.000Z',
      }))
    }
    if (method === 'POST' && path === '/auth/tokens') {
      return route.fulfill(json({
        accessToken: 'e2e-access-token-2',
        refreshToken: 'e2e-refresh-token-2',
        tokenType: 'Bearer',
        expiresInSeconds: 3600,
      }))
    }
    if (method === 'DELETE' && path.startsWith('/auth/sessions/')) {
      return route.fulfill({ status: 204, body: '' })
    }

    // ---- users ----
    if (method === 'GET' && path === '/users/me') {
      return route.fulfill(json({
        userId: ids.userId,
        displayName: 'E2E 学习者',
        avatarUrl: null,
        locale: 'zh-CN',
        preferredSubjectCodes: ['CS_DS'],
        createdAt: NOW,
        updatedAt: NOW,
      }))
    }

    // ---- materials ----
    if (method === 'GET' && path === '/materials') {
      return route.fulfill(json({ items: [makeMaterial()], nextCursor: null }))
    }

    // ---- practice-projects ----
    if (method === 'GET' && path === '/practice-projects') {
      return route.fulfill(json({ items: state.projects, nextCursor: null }))
    }
    if (method === 'POST' && path === '/practice-projects') {
      const created = makeProject(ids.createdProjectId, false)
      created.name = 'E2E 新建研习册'
      state.projects.push(created)
      return route.fulfill(json(created))
    }
    const projectMatch = path.match(/^\/practice-projects\/([^/]+)$/)
    if (projectMatch) {
      const projectId = projectMatch[1]
      const project = state.projects.find((item) => item.projectId === projectId)
        ?? makeProject(projectId, true)
      if (method === 'GET') {
        return route.fulfill(json({
          project,
          questionCounts: { SINGLE_CHOICE: 1 },
          readyQuestionCount: 1,
        }))
      }
      if (method === 'PATCH') {
        const body = route.request().postDataJSON() as { graphId?: string } | null
        if (body?.graphId) project.graphId = body.graphId
        project.version += 1
        return route.fulfill(json(project))
      }
    }

    // ---- questions ----
    const questionsMatch = path.match(/^\/practice-projects\/([^/]+)\/questions$/)
    if (method === 'GET' && questionsMatch) {
      return route.fulfill(json({ items: [makeQuestion(questionsMatch[1])], nextCursor: null }))
    }

    // ---- question generation（立册自动成题）----
    const generationMatch = path.match(/^\/practice-projects\/([^/]+)\/question-generations$/)
    if (method === 'POST' && generationMatch) {
      return route.fulfill(json({
        jobId: 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee',
        projectId: generationMatch[1],
        kind: 'QUESTION_GENERATION',
        status: 'SUCCEEDED',
        progress: 100,
        createdCount: 1,
        diagnostics: [],
      }))
    }

    // ---- knowledge graph builds ----
    if (method === 'POST' && path === '/knowledge-graph-builds') {
      const body = route.request().postDataJSON() as { studyProjectId?: string } | null
      // 识网归属发起构图的研习册，供 create/ensureGraphScope 的归属校验通过
      if (body?.studyProjectId) state.graphOwnerId = body.studyProjectId
      return route.fulfill(json({
        buildId: ids.buildId,
        materialId: ids.materialId,
        studyProjectId: state.graphOwnerId,
        status: 'SUCCEEDED',
        progress: 100,
        graphId: ids.graphId,
        sourceTextChecksum: 'sha256-e2e-text',
        segmentationMode: 'AUTO',
        segmenterVersion: 'e2e',
        extractorVersion: 'knowledge-extractor-v3',
        error: null,
        createdAt: NOW,
        updatedAt: NOW,
      }))
    }
    if (method === 'GET' && path === `/knowledge-graph-builds/${ids.buildId}`) {
      return route.fulfill(json({
        buildId: ids.buildId,
        materialId: ids.materialId,
        studyProjectId: state.graphOwnerId,
        status: 'SUCCEEDED',
        progress: 100,
        graphId: ids.graphId,
        sourceTextChecksum: 'sha256-e2e-text',
        segmentationMode: 'AUTO',
        segmenterVersion: 'e2e',
        extractorVersion: 'knowledge-extractor-v3',
        error: null,
        createdAt: NOW,
        updatedAt: NOW,
      }))
    }

    // ---- knowledge graphs ----
    if (method === 'GET' && path === `/knowledge-graphs/${ids.graphId}`) {
      return route.fulfill(json(makeGraph(state.graphOwnerId)))
    }
    if (method === 'GET' && path === `/knowledge-graphs/${ids.graphId}/chapters`) {
      return route.fulfill(json([makeChapter()]))
    }
    if (method === 'GET' && path === `/knowledge-graphs/${ids.graphId}/points`) {
      return route.fulfill(json({ items: [makePoint()], nextCursor: null }))
    }

    // ---- plans ----
    if (method === 'POST' && path === '/assessment-plans') {
      return route.fulfill(json(makePlan(ids.planId, 1)))
    }
    if (method === 'POST' && path === '/learning-plans') {
      return route.fulfill(json(makePlan(ids.planId, 1)))
    }

    // ---- practice sessions ----
    if (method === 'POST' && path === '/practice-sessions') {
      return route.fulfill(json(newPracticeSession(state)))
    }
    if (method === 'GET' && path === `/practice-sessions/${ids.practiceSessionId}`) {
      return route.fulfill(json(practiceSessionPayload(state)))
    }
    const answerMatch = path.match(/^\/practice-sessions\/([^/]+)\/answers\/([^/]+)$/)
    if (method === 'PUT' && answerMatch) {
      const answer = makeAnswer()
      state.practiceSessionAnswers = [answer]
      return route.fulfill(json(answer))
    }
    if (method === 'POST' && path === `/practice-sessions/${ids.practiceSessionId}/completion`) {
      if (state.practiceSessionAnswers.length === 0) {
        state.practiceSessionAnswers = [makeAnswer()]
      }
      state.practiceSessionStatus = 'COMPLETED'
      return route.fulfill(json({
        session: practiceSessionPayload(state),
        evidence: { recorded: state.practiceSessionAnswers.length },
      }))
    }

    // ---- 兜底：未覆盖的 /api/v1 请求显式失败，避免静默 404 让冒烟假绿 ----
    return route.fulfill(failure(404, 'NOT_FOUND', `E2E mock 未覆盖: ${method} ${path}`))
  })
}
