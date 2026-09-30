// Optional file-backed snapshot for ReviewSession records.
// Single-instance production can survive restarts without adding Mongo to
// RenderService. Writes are atomic (tmp + rename). Multi-instance is still
// unsupported — document that constraint.
import { mkdirSync, readFileSync, renameSync, writeFileSync, existsSync } from 'node:fs'
import { dirname } from 'node:path'

export interface PersistedQuestionDigest {
  knowledgePointId: string
  sceneId: string
  choices: Record<string, boolean>
}

export interface PersistedPackageDigest {
  entrySceneId: string
  sceneIds: string[]
  questions: Record<string, PersistedQuestionDigest>
  hasQuestions: boolean
}

export interface PersistedSessionRecord {
  sessionId: string
  userId: string
  packageId: string
  reviewPlanId: string
  snapshotVersion: string
  clientRuntimeVersion: string
  status: string
  currentSceneId: string | null
  progressVersion: number
  startedAt: string | null
  completedAt: string | null
  createdAt: string
  digest: PersistedPackageDigest
  snapshot: unknown
  snapshotChecksum: string | null
  eventIds: string[]
  result: unknown
  pendingResult: unknown
}

export interface FileSnapshot {
  version: 1
  savedAt: string
  sessions: PersistedSessionRecord[]
}

export function loadFileSnapshot(path: string): FileSnapshot | null {
  if (!existsSync(path)) return null
  try {
    const raw = readFileSync(path, 'utf8')
    const parsed = JSON.parse(raw) as FileSnapshot
    if (!parsed || parsed.version !== 1 || !Array.isArray(parsed.sessions)) return null
    return parsed
  } catch {
    return null
  }
}

export function saveFileSnapshot(path: string, snapshot: FileSnapshot): void {
  mkdirSync(dirname(path), { recursive: true })
  const tmp = `${path}.tmp`
  writeFileSync(tmp, JSON.stringify(snapshot), 'utf8')
  renameSync(tmp, path)
}
