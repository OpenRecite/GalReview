import { describe, it, expect } from 'vitest';
import express from 'express';
import request from 'supertest';
import {
  createMetricsMiddleware,
  createMetricsRegistry,
  instrumentIntrospectionCache,
} from '../../src/middleware/metrics.js';
import { IntrospectionCache } from '../../src/middleware/introspectionCache.js';

describe('metrics registry', () => {
  it('应输出 Prometheus 文本与请求计数', async () => {
    const registry = createMetricsRegistry();
    const app = express();
    app.use(createMetricsMiddleware(registry));
    app.get('/ok', (_req, res) => res.status(200).send('ok'));

    await request(app).get('/ok');
    const text = registry.render();
    expect(text).toContain('gateway_uptime_seconds');
    expect(text).toContain('gateway_http_requests_total');
    expect(text).toContain('method="GET"');
    expect(text).toContain('path="/ok"');
    expect(text).toContain('status="200"');
  });

  it('应折叠 UUID 路径段避免高基数', async () => {
    const registry = createMetricsRegistry();
    const app = express();
    app.use(createMetricsMiddleware(registry));
    app.get('/api/v1/users/:id', (_req, res) => res.status(200).send('ok'));

    await request(app).get('/api/v1/users/7bc4918a-9079-4ea2-9e8e-369ad79a9f20');
    const text = registry.render();
    expect(text).toContain('path="/api/v1/users/:id"');
    expect(text).not.toContain('7bc4918a');
  });

  it('应统计内省缓存命中', async () => {
    const registry = createMetricsRegistry();
    const cache = new IntrospectionCache();
    instrumentIntrospectionCache(cache, registry);

    const active = {
      status: 'ok' as const,
      data: {
        active: true as const,
        userId: '7bc4918a-9079-4ea2-9e8e-369ad79a9f20',
        sessionId: '6fa43e7f-0383-4c60-b305-8011f4a8cab8',
        scopes: ['user'],
        expiresAt: new Date(Date.now() + 60_000).toISOString(),
      },
    };
    await cache.resolve('tok', async () => active);
    await cache.resolve('tok', async () => active);

    const text = registry.render();
    expect(text).toMatch(/result="miss"\} 1/);
    expect(text).toMatch(/result="hit"\} 1/);
  });

  it('应统计上游代理失败（告警信号）', () => {
    const registry = createMetricsRegistry();
    registry.onUpstreamFailure('modelService', 'timeout');
    registry.onUpstreamFailure('modelService', 'timeout');
    registry.onUpstreamFailure('creditService', 'connection');

    const text = registry.render();
    expect(text).toContain('gateway_upstream_failures_total');
    expect(text).toMatch(/service="modelService",kind="timeout"\} 2/);
    expect(text).toMatch(/service="creditService",kind="connection"\} 1/);
  });
});
