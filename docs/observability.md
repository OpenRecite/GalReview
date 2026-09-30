# GalReview 可观测性与告警最低集（T10）

> 状态：已实施（八服务全覆盖，含 Auth/GalGame，见 §5）
> 风格约束：轻量自研中间件 + 进程内计数器 + Prometheus 文本格式；不引入 OpenTelemetry / Service Mesh / Redis。
> 相关：`docs/optimization-roadmap.md` T10；验证证据见 `docs/test_report.md` §47/§48。

---

## 1. 链路追踪（X-Correlation-Id）

### 1.1 传播约定

| 环节 | 行为 | 位置 |
|---|---|---|
| 生成/清洗 | 网关生成 ULID；入站头过滤控制字符、限长 128 | `gateway/src/middleware/traceContext.ts` |
| 回写 | 响应头 `X-Correlation-Id` 与错误信封 `traceId` 同源 | gateway errorHandler / 各服务错误信封 |
| 代理透传 | `proxyReq.setHeader('X-Correlation-Id', req.traceId)` | `gateway/src/proxy/createProxy.ts` |
| 服务归一 | 各 .NET 服务 `RequestLogging`（或既有 TraceContext 中间件）把头写入 `context.TraceIdentifier` | 各服务 `RequestLogging.cs` |
| 服务出站 | Practice `GatewayClient` / `GatewayModelFacetAdjudicator` 经 `TraceFlow`（AsyncLocal）透传；Knowledge 客户端方法参数透传；Auth `AuthGatewayClient` 显式透传 | 各服务 Persistence |
| 日志 | JSON Console 带 `traceId`/`userId`/`path`/`method` scope + `durationMs`/`status` 收尾行 | 各服务 `RequestLogging.cs` |

### 1.2 排障用法（一次注入故障串起 Gateway→Practice→Model→Credit）

1. 复现请求，从响应头取 `X-Correlation-Id`（或错误信封 `traceId`）。
2. 在各服务日志中 grep 该 traceId：Gateway JSON 日志 → Practice `RequestLogging` 收尾行 →
   Model / Credit 收尾行；出站调用（Practice→Model 判分、Practice→Credit 预授权/结算/释放）
   携带同一 traceId，下游错误信封 `traceId` 与之相同。
3. 若请求在代理层失败，Gateway 日志 `gateway_upstream_proxy_error` 事件含 `traceId` 与
   `targetService`；`/metrics` 的 `gateway_upstream_failures_total` 可确认是否为面上游故障。

---

## 2. 指标清单

### 2.1 Gateway（`GET /metrics`，单实例 Prometheus 文本）

| 指标 | 类型 | 标签 | 含义 |
|---|---|---|---|
| `gateway_uptime_seconds` | gauge | — | 进程运行时长 |
| `gateway_http_requests_total` | counter | method, path | 请求计数（路径折叠 UUID/数字段） |
| `gateway_http_responses_total` | counter | method, path, status | 按状态码响应计数 |
| `gateway_http_request_duration_ms_sum` | counter | route | 请求耗时累计（配合请求数求均值） |
| `gateway_introspection_cache_events` | counter | result=hit\|miss\|inflight | 令牌内省缓存事件 |
| `gateway_upstream_failures_total` | counter | service, kind=timeout\|connection\|contract\|other | 上游代理失败（连接/超时/契约错误） |

### 2.2 PracticeService（`GET /metrics`）

| 指标 | 类型 | 含义 |
|---|---|---|
| `practice_gradings_total` | counter | 判分总次数 |
| `practice_abstained_total` | counter | ABSTAINED 判分次数（模型不可用/降级/RUBRIC_EMPTY 等） |
| `practice_degraded_total` | counter | 标记 degraded 的判分次数 |

### 2.3 CreditService（`GET /metrics`）

| 指标 | 类型 | 含义 |
|---|---|---|
| `credit_release_failures_total` | counter | `ReleaseCreditsCommand` 释放失败/异常次数 |

### 2.4 队列与就绪（`GET /readyz` 旁路）

| 信号 | 服务 | 字段 |
|---|---|---|
| 推理队列深度 | ModelService | `load.queuedBatches`（另有 `inflightBatches`/`completedBatches`） |
| 解析队列深度 | FileService | `ingestionQueueDepth` |
| 存储就绪 | 各服务 | `status` / `storage` / `adminConfigured` |

GalGame 生成队列深度待其拆分完成后接入（见 §5）。

---

## 3. 告警最低集（建议阈值 + PromQL 风格伪查询）

> 以下为单实例自采建议；接 Prometheus 抓取 `/metrics` 后按同一表达式告警。
> 「5 分钟」类窗口用 `rate(...[5m])` 或运维侧 probe 持续时长表达。

### A1 · readyz / 上游异常持续 >5min

```promql
# 网关侧：任一上游 5 分钟内持续出现失败
sum by (service) (increase(gateway_upstream_failures_total[5m])) > 0

# 服务侧：readyz 非 ready 持续 5 分钟（probe 规则，非 PromQL）
# probe:http_status{job="readyz",service=~"model|file|credit|..."} != 200 for: 5m
```

- **含义**：上游连接失败/超时/契约错误，或服务 readyz 报 not-ready 持续 5 分钟。
- **建议阈值**：`increase > 0` 持续 5 分钟即告警（不看绝对量，防漏报）；夜高峰可放宽到 10 分钟。
- **处置**：按 `service`/`kind` 标签定位；`connection` 看进程/端口，`timeout` 看下游负载，`contract` 看版本漂移。

### A2 · ABSTAINED 率突增

```promql
# 5 分钟内 ABSTAINED 占比 > 20%（且样本量足够）
(
  sum(increase(practice_abstained_total[5m]))
  /
  sum(increase(practice_gradings_total[5m]))
) > 0.20
and
sum(increase(practice_gradings_total[5m])) >= 5
```

- **含义**：Model 降级/不可用导致判分大面积弃权，用户侧表现为「答案不判分」。
- **建议阈值**：比例 > 20% 持续 5 分钟（样本 ≥5 次）；瞬时 100%（`practice_gradings_total` 增量全为 abstained）立即告警。
- **处置**：先看 Model `/readyz` 与 `gateway_upstream_failures_total{service="modelService"}`；再看 `practice_degraded_total`。

### A3 · Credit 释放失败

```promql
sum(increase(credit_release_failures_total[5m])) > 0
```

- **含义**：预授权释放失败会遗留 HELD 额度（用户可用余额被虚占）。
- **建议阈值**：任意增量即告警（释放失败单价影响是额度泄漏，无「少量可容忍」区间）。
- **处置**：对照 traceId 找到 `ReleaseCreditsCommand` 调用方（Practice/GalGame）；核对 reservation 状态机是否停在 HELD。

### A4 · 队列深度

```promql
# Model 推理队列（readyz 旁路，也可由探针转指标）
model_readyz_queued_batches > 32          # 接近 Nli:MaxPendingBatches=64 时预警
# File 解析队列
file_readyz_ingestion_queue_depth > 50
```

- **含义**：排队持续增长意味着消费速度跟不上，最终会触发背压 503（Model）或任务超时。
- **建议阈值**：Model `queuedBatches > MaxPendingBatches/2`（默认 32）预警、接近上限（64）紧急；
  File `ingestionQueueDepth > 50` 预警。二者均建议以「持续 5 分钟」去抖。
- **处置**：Model 看 GPU/批大小；File 看 OCR 时长与 Worker 重启；必要时横向扩容（注意单实例约束）。

---

## 4. 日志格式约定

各服务 `AddJsonStructuredLogging()` 输出 Console JSON：

```json
{
  "Timestamp": "2026-09-30T14:22:31.123Z",
  "LogLevel": "Information",
  "Category": "UserService",
  "EventId": 0,
  "Message": "HTTP GET /api/v1/users/me completed with 200 in 12.3ms traceId=01J... userId=7bc4...",
  "Scopes": { "traceId": "01J...", "userId": "7bc4...", "path": "/api/v1/users/me", "method": "GET" }
}
```

- `Timestamp` UTC；`Scopes.traceId` 即 `X-Correlation-Id`；`durationMs` 在消息模板与结构化字段中双记，便于 grep 与解析。
- 错误信封（`ApiFailure`）不变：`{ data, error: { code, message, details }, traceId }`。

---

## 5. AuthService / GalGameService 接入记录（2026-10-14 已完成）

> T9 拆分落地后已按下列清单接入（见 `docs/test_report.md` §48）。

### 5.1 AuthService ✅

1. `RequestLogging.cs`（与 `backend/UserService/RequestLogging.cs` 同源，含 `BeginAmbientTrace` 钩子）。
2. `Program.cs`：`builder.Logging.AddJsonStructuredLogging();`。
3. `Program.cs`：原内联 correlation 中间件替换为 `app.UseRequestLogging("AuthService");`。
4. `AuthGatewayClient` 出站已透传 `X-Correlation-Id` ✅ 无需改。

### 5.2 GalGameService ✅

1. `RequestLogging.cs` + `GalGameTraceFlow.cs`（AsyncLocal，对齐 Practice `TraceFlow`）。
2. `Program.cs`：`AddJsonStructuredLogging()`；`UseGalGameCorrelationId()`（保留严格字符集校验）后接
   `UseRequestLogging("GalGameService", normalizeCorrelationId: false)`；注册 `RequestLogging.BeginAmbientTrace = GalGameTraceFlow.Begin`。
3. 出站：`PlanGraphClient` 已带 traceId；`CreditBillingClient` 补 `X-Correlation-Id`（取 `GalGameTraceFlow.Current`，缺省生成）。
4. 后台生成：`GameGenerationEndpoints` 的 `generationWork` 在执行时 `GalGameTraceFlow.Begin(traceId)`，
   保证脱离 HTTP 上下文后 credits 调用仍可按原 traceId 串联。

### 5.3 接入后验收（与 T10 验收同口径）

- 注入一次 Model 超时（或断掉 Model 端口），从客户端响应头取 traceId，
  在 Gateway / Practice / Model / Credit / Auth / GalGame 日志中都能 grep 到同一值；
  错误信封 `traceId` 一致。
- `/metrics` 可抓到对应告警指标增量（A1–A4 至少各触发一次模拟）。
