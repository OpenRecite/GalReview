import type { Request, Response, NextFunction } from 'express';
import type { IntrospectionCache } from './introspectionCache.js';

/**
 * 轻量进程内指标（Prometheus text format）。
 * 单实例足够；水平扩展时应换 Prometheus client + 聚合。
 */
export interface MetricsRegistry {
  onRequest(method: string, path: string, status: number, durationMs: number): void;
  onIntrospection(result: 'hit' | 'miss' | 'inflight'): void;
  /** 上游代理失败（连接/超时/契约错误）——告警信号 */
  onUpstreamFailure(service: string, kind: string): void;
  render(): string;
}

interface Bucket {
  count: number;
  durationSumMs: number;
  byStatus: Map<number, number>;
}

function normalizePath(path: string): string {
  // 避免高基数：UUID/数字路径段折叠
  return path
    .split('/')
    .map((seg) => {
      if (/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(seg)) return ':id';
      if (/^\d+$/.test(seg)) return ':n';
      return seg;
    })
    .join('/')
    .slice(0, 120);
}

export function createMetricsRegistry(): MetricsRegistry {
  const started = Date.now();
  const requests = new Map<string, Bucket>();
  const introspection = { hit: 0, miss: 0, inflight: 0 };
  // service|kind -> count；kind ∈ timeout/connection/contract/other
  const upstreamFailures = new Map<string, number>();

  return {
    onRequest(method, path, status, durationMs) {
      const key = `${method.toUpperCase()} ${normalizePath(path)}`;
      let bucket = requests.get(key);
      if (!bucket) {
        bucket = { count: 0, durationSumMs: 0, byStatus: new Map() };
        requests.set(key, bucket);
      }
      bucket.count += 1;
      bucket.durationSumMs += durationMs;
      bucket.byStatus.set(status, (bucket.byStatus.get(status) ?? 0) + 1);
    },
    onIntrospection(result) {
      introspection[result] += 1;
    },
    onUpstreamFailure(service, kind) {
      const key = `${service}|${kind}`;
      upstreamFailures.set(key, (upstreamFailures.get(key) ?? 0) + 1);
    },
    render() {
      const lines: string[] = [];
      lines.push('# HELP gateway_uptime_seconds Gateway process uptime');
      lines.push('# TYPE gateway_uptime_seconds gauge');
      lines.push(`gateway_uptime_seconds ${((Date.now() - started) / 1000).toFixed(3)}`);

      lines.push('# HELP gateway_http_requests_total HTTP requests by method/path');
      lines.push('# TYPE gateway_http_requests_total counter');
      for (const [key, bucket] of requests) {
        const [method, ...rest] = key.split(' ');
        const path = rest.join(' ');
        lines.push(
          `gateway_http_requests_total{method="${method}",path="${path}"} ${bucket.count}`,
        );
        for (const [status, n] of bucket.byStatus) {
          lines.push(
            `gateway_http_responses_total{method="${method}",path="${path}",status="${status}"} ${n}`,
          );
        }
      }

      lines.push('# HELP gateway_http_request_duration_ms_sum Sum of request durations');
      lines.push('# TYPE gateway_http_request_duration_ms_sum counter');
      for (const [key, bucket] of requests) {
        lines.push(
          `gateway_http_request_duration_ms_sum{route="${key}"} ${bucket.durationSumMs.toFixed(1)}`,
        );
      }

      lines.push('# HELP gateway_introspection_cache_events Token introspection cache events');
      lines.push('# TYPE gateway_introspection_cache_events counter');
      lines.push(`gateway_introspection_cache_events{result="hit"} ${introspection.hit}`);
      lines.push(`gateway_introspection_cache_events{result="miss"} ${introspection.miss}`);
      lines.push(`gateway_introspection_cache_events{result="inflight"} ${introspection.inflight}`);

      lines.push('# HELP gateway_upstream_failures_total Upstream proxy failures by service and kind');
      lines.push('# TYPE gateway_upstream_failures_total counter');
      for (const [key, count] of upstreamFailures) {
        const [service, kind] = key.split('|');
        lines.push(`gateway_upstream_failures_total{service="${service}",kind="${kind}"} ${count}`);
      }

      return lines.join('\n') + '\n';
    },
  };
}

export function createMetricsMiddleware(registry: MetricsRegistry) {
  return (req: Request, res: Response, next: NextFunction): void => {
    const start = process.hrtime.bigint();
    res.on('finish', () => {
      const durationMs = Number(process.hrtime.bigint() - start) / 1e6;
      registry.onRequest(req.method, req.path || req.url, res.statusCode, durationMs);
    });
    next();
  };
}

/** 把认证中间件的缓存事件接到 metrics（装饰 IntrospectionCache.resolve）。 */
export function instrumentIntrospectionCache(
  cache: IntrospectionCache,
  registry: MetricsRegistry,
): void {
  const originalResolve = cache.resolve.bind(cache);
  cache.resolve = async (token, load) => {
    const cached = cache.get(token);
    if (cached) {
      registry.onIntrospection('hit');
      return cached;
    }
    // 仅该 token 已有 in-flight 时计 inflight；否则计 miss（不双计）
    if (cache.hasInflight(token)) {
      registry.onIntrospection('inflight');
    } else {
      registry.onIntrospection('miss');
    }
    return originalResolve(token, load);
  };
}
