import { createHash } from 'node:crypto';
import type { ActiveTokenIntrospection, IntrospectionResult } from './authentication.js';

export interface IntrospectionCacheOptions {
  /** active=true 结果的最大缓存时间（会被 token expiresAt 截短） */
  positiveTtlMs?: number;
  /** active=false 负缓存时间，避免无效令牌反复打爆 AuthService */
  negativeTtlMs?: number;
  /** 距 expiresAt 的安全窗口：剩余寿命短于该值则不缓存 */
  expirySkewMs?: number;
  /** 最大条目数，超出后按插入序淘汰 */
  maxEntries?: number;
  now?: () => number;
}

interface CacheEntry {
  result: IntrospectionResult;
  expiresAtMs: number;
}

/**
 * 令牌内省结果的进程内缓存 + single-flight。
 * - 只缓存 ok / invalid；unreachable 永远穿透，保证 Auth 恢复后立刻可用。
 * - key 为 token 的 SHA-256，缓存中不保留明文令牌。
 */
export class IntrospectionCache {
  private readonly positiveTtlMs: number;
  private readonly negativeTtlMs: number;
  private readonly expirySkewMs: number;
  private readonly maxEntries: number;
  private readonly now: () => number;
  private readonly entries = new Map<string, CacheEntry>();
  private readonly inflight = new Map<string, Promise<IntrospectionResult>>();

  constructor(options: IntrospectionCacheOptions = {}) {
    this.positiveTtlMs = options.positiveTtlMs ?? 30_000;
    this.negativeTtlMs = options.negativeTtlMs ?? 5_000;
    this.expirySkewMs = options.expirySkewMs ?? 2_000;
    this.maxEntries = options.maxEntries ?? 10_000;
    this.now = options.now ?? Date.now;
  }

  static hashToken(token: string): string {
    return createHash('sha256').update(token, 'utf8').digest('hex');
  }

  get(token: string): IntrospectionResult | undefined {
    const key = IntrospectionCache.hashToken(token);
    const entry = this.entries.get(key);
    if (!entry) return undefined;
    if (entry.expiresAtMs <= this.now()) {
      this.entries.delete(key);
      return undefined;
    }
    // 命中后刷新插入序（近似 LRU）
    this.entries.delete(key);
    this.entries.set(key, entry);
    return entry.result;
  }

  set(token: string, result: IntrospectionResult): void {
    if (result.status === 'unreachable') return;

    const ttlMs = this.resolveTtlMs(result);
    if (ttlMs <= 0) return;

    const key = IntrospectionCache.hashToken(token);
    this.entries.set(key, { result, expiresAtMs: this.now() + ttlMs });
    this.evictIfNeeded();
  }

  /**
   * 相同 token 并发请求只发起一次上游内省。
   * 失败（unreachable）不写入缓存，但会从 inflight 摘除，后续请求可重试。
   */
  async resolve(
    token: string,
    load: () => Promise<IntrospectionResult>,
  ): Promise<IntrospectionResult> {
    const cached = this.get(token);
    if (cached) return cached;

    const key = IntrospectionCache.hashToken(token);
    const existing = this.inflight.get(key);
    if (existing) return existing;

    const pending = load()
      .then((result) => {
        this.set(token, result);
        return result;
      })
      .finally(() => {
        this.inflight.delete(key);
      });

    this.inflight.set(key, pending);
    return pending;
  }

  get size(): number {
    return this.entries.size;
  }

  get inflightCount(): number {
    return this.inflight.size;
  }

  hasInflight(token: string): boolean {
    return this.inflight.has(IntrospectionCache.hashToken(token));
  }

  clear(): void {
    this.entries.clear();
    this.inflight.clear();
  }

  private resolveTtlMs(result: IntrospectionResult): number {
    const configured =
      result.status === 'ok' ? this.positiveTtlMs : this.negativeTtlMs;

    if (result.status !== 'ok' || !result.data.expiresAt) {
      return configured;
    }

    const expiresAtMs = Date.parse(result.data.expiresAt);
    if (Number.isNaN(expiresAtMs)) return configured;

    const remaining = expiresAtMs - this.now() - this.expirySkewMs;
    return Math.min(configured, remaining);
  }

  private evictIfNeeded(): void {
    while (this.entries.size > this.maxEntries) {
      const oldest = this.entries.keys().next();
      if (oldest.done) break;
      this.entries.delete(oldest.value);
    }
  }
}

export function createIntrospectionCache(
  options?: IntrospectionCacheOptions,
): IntrospectionCache {
  return new IntrospectionCache(options);
}
