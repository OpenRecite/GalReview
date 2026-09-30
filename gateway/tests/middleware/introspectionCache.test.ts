import { describe, it, expect, vi } from 'vitest';
import { IntrospectionCache } from '../../src/middleware/introspectionCache.js';
import type { IntrospectionResult } from '../../src/middleware/authentication.js';

const active = (expiresAt: string | null = null): IntrospectionResult => ({
  status: 'ok',
  data: {
    active: true,
    userId: '7bc4918a-9079-4ea2-9e8e-369ad79a9f20',
    sessionId: '6fa43e7f-0383-4c60-b305-8011f4a8cab8',
    scopes: ['user'],
    expiresAt,
  },
});

const invalid: IntrospectionResult = { status: 'invalid' };
const unreachable: IntrospectionResult = { status: 'unreachable' };

describe('IntrospectionCache', () => {
  it('应在 TTL 内命中缓存，不再调用 load', async () => {
    let now = 1_000_000;
    const cache = new IntrospectionCache({
      positiveTtlMs: 30_000,
      now: () => now,
    });
    const load = vi.fn(async () => active());

    await cache.resolve('token-a', load);
    now += 10_000;
    await cache.resolve('token-a', load);

    expect(load).toHaveBeenCalledTimes(1);
  });

  it('TTL 过期后应重新内省', async () => {
    let now = 1_000_000;
    const cache = new IntrospectionCache({
      positiveTtlMs: 30_000,
      now: () => now,
    });
    const load = vi.fn(async () => active());

    await cache.resolve('token-a', load);
    now += 30_001;
    await cache.resolve('token-a', load);

    expect(load).toHaveBeenCalledTimes(2);
  });

  it('token 即将过期时应缩短缓存寿命', async () => {
    let now = Date.parse('2026-09-12T10:00:00Z');
    const cache = new IntrospectionCache({
      positiveTtlMs: 60_000,
      expirySkewMs: 2_000,
      now: () => now,
    });
    // 剩余 10s，skew 2s → 只缓存 8s
    const load = vi.fn(async () => active('2026-09-12T10:00:10Z'));

    await cache.resolve('token-b', load);
    now += 7_000;
    await cache.resolve('token-b', load);
    expect(load).toHaveBeenCalledTimes(1);

    now += 2_000; // total +9s > 8s TTL
    await cache.resolve('token-b', load);
    expect(load).toHaveBeenCalledTimes(2);
  });

  it('应负缓存 invalid，但不缓存 unreachable', async () => {
    const cache = new IntrospectionCache({ negativeTtlMs: 5_000 });
    const invalidLoad = vi.fn(async () => invalid);
    const unreachableLoad = vi.fn(async () => unreachable);

    await cache.resolve('bad', invalidLoad);
    await cache.resolve('bad', invalidLoad);
    expect(invalidLoad).toHaveBeenCalledTimes(1);

    await cache.resolve('down', unreachableLoad);
    await cache.resolve('down', unreachableLoad);
    expect(unreachableLoad).toHaveBeenCalledTimes(2);
  });

  it('相同 token 并发只发起一次上游调用（single-flight）', async () => {
    const cache = new IntrospectionCache();
    let resolveLoad!: (r: IntrospectionResult) => void;
    const load = vi.fn(
      () =>
        new Promise<IntrospectionResult>((resolve) => {
          resolveLoad = resolve;
        }),
    );

    const p1 = cache.resolve('token-c', load);
    const p2 = cache.resolve('token-c', load);
    expect(load).toHaveBeenCalledTimes(1);

    resolveLoad(active());
    const [r1, r2] = await Promise.all([p1, p2]);
    expect(r1.status).toBe('ok');
    expect(r2.status).toBe('ok');
  });

  it('缓存键使用 token 哈希，不保存明文', async () => {
    const cache = new IntrospectionCache();
    const token = 'super-secret-access-token';
    await cache.resolve(token, async () => active());
    expect(cache.get(token)?.status).toBe('ok');
    expect(IntrospectionCache.hashToken(token)).toHaveLength(64);
    expect(IntrospectionCache.hashToken(token)).not.toContain(token);
  });

  it('超出 maxEntries 时应淘汰最旧条目', async () => {
    const cache = new IntrospectionCache({ maxEntries: 2 });
    await cache.resolve('t1', async () => active());
    await cache.resolve('t2', async () => active());
    await cache.resolve('t3', async () => active());
    expect(cache.size).toBe(2);
    expect(cache.get('t1')).toBeUndefined();
    expect(cache.get('t3')?.status).toBe('ok');
  });
});
