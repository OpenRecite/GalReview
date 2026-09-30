import { request, json, query, collectPages, UPLOAD_TIMEOUT_MS } from './client'
import { createUuidV4 } from '../uuid'
import type {
  ExtractedTextDocument,
  IngestionJob,
  Material,
  MaterialPage,
} from '../../types/api'

export function listMaterials(cursor?: string, filters: { status?: string; subjectCode?: string } = {}): Promise<MaterialPage> {
  return request(`/materials${query({ limit: 100, cursor, status: filters.status, subjectCode: filters.subjectCode })}`)
}

export function getAllMaterials(): Promise<Material[]> {
  return collectPages<Material>((cursor) => request(`/materials${query({ limit: 100, cursor })}`))
}

export function getMaterial(materialId: string): Promise<Material> {
  return request(`/materials/${encodeURIComponent(materialId)}`)
}

export function getExtractedTextPreview(materialId: string): Promise<ExtractedTextDocument> {
  return request(`/materials/${encodeURIComponent(materialId)}/extracted-text-preview`)
}

export function uploadMaterial(file: File, displayName?: string, subjectCode?: string): Promise<Material> {
  const body = new FormData()
  body.append('file', file)
  if (displayName?.trim()) body.append('displayName', displayName.trim())
  if (subjectCode?.trim()) body.append('subjectCode', subjectCode.trim().toUpperCase())
  return request('/materials', { method: 'POST', body, timeoutMs: UPLOAD_TIMEOUT_MS })
}

export function deleteMaterial(materialId: string): Promise<void> {
  return request(`/materials/${encodeURIComponent(materialId)}`, { method: 'DELETE' })
}

export function createIngestionJob(
  materialId: string,
  options: { force?: boolean; enableOcr?: boolean; ocrMode?: 'quick' | 'standard' } = {},
): Promise<IngestionJob> {
  return request(`/materials/${encodeURIComponent(materialId)}/ingestion-jobs`, {
    method: 'POST',
    body: json({
      parserVersion: options.enableOcr ? 'files-ocr-v1' : 'files-text-v1',
      force: options.force ?? false,
      enableOcr: options.enableOcr ?? false,
      ocrMode: options.ocrMode ?? 'standard',
    }),
  })
}

export function getIngestionJob(jobId: string): Promise<IngestionJob> {
  return request(`/ingestion-jobs/${encodeURIComponent(jobId)}`)
}
