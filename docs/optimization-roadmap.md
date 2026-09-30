# GalReview 深度优化任务清单

> 状态：BASELINE-2（基于仓库当前工作区审计，含未提交变更；不替代 `docs/contract.md`）
> 基线日期：2026-09-30（上一版 2026-09-12）
> 审计范围：frontend / gateway / backend 全部 10 服务 / compose×3 / CI / 部署脚本 / desktop / mobile
> 优先级：P0 阻断生产安全或核心闭环 · P1 明显性能/可靠性瓶颈 · P2 可维护性与长期演进
> 状态标记：✅ 已完成 · 🟡 部分完成 · ⬜ 未开始
> 更新：2026-09-30 当日 P0（T0/T1/T2/T3）已全部实施并验证，证据见 `docs/test_report.md` §43。

---

## 0. 审计结论一句话

**2026-09-12 路线图中的 P0 止血项已全部落地且经测试验证**（CI 六 job、内省缓存、批量化、lazy 路由、生产 overlay、会话持久化），并新增了 desktop/mobile 双端壳与 Gateway 指标面；当前投入产出比最高的三件事是：**① 把工作区里 18 天的未提交成果验证后入库并补仓库卫生（含证书文件暴露面），② 清掉残存的代码内默认密钥/GUID 兜底，③ 补后台任务重试/死信与推理背压这两个"最后一公里"可靠性缺口。**

---

## 1. 现状画像（2026-09-30 实测）

### 1.1 相对上一基线的关键变化

| 领域 | 变化 |
|---|---|
| Gateway | 新增 `introspectionCache.ts`（30s 正缓存 / 5s 负缓存 / single-flight / LRU 1 万条 / SHA-256 键）与 `metrics.ts`（Prometheus `GET /metrics`）；17 个测试文件 216 用例全绿 |
| CI | 六 job：integration-smoke（含"生产 overlay 缺密钥必须解析失败"反向门禁）、端口策略、dotnet-core（Practice/Credit/Model）、dotnet-platform、gateway、render-service、frontend |
| Compose | 新增 `compose.production.yaml`（5 处 `${VAR:?}` 强制）与 `compose.smoke.yaml`（Model 占位容器）；`scripts/Test-ProductionEnv.ps1` 门禁脚本 |
| 后台任务 | File/GalGame 从 `Task.Run` 迁移为 Channel + BackgroundService；GalGame 有原子状态机 + 启动恢复 `RecoverStaleJobs` |
| RenderService | 新增 `sessionStore.ts` 文件快照（tmp+rename 原子写 + 启动加载 + 防抖保存），`/readyz` 上报 `storage=file` |
| 服务拆分 | UserService 全拆（Program 仅 80 行 + 4 模块）；GalGame 抽出 GalGameHttp/GamePackageEndpoints；Auth 仅抽出 AuthHttp |
| 前端 | 16 页面全部 `React.lazy` + Suspense + ErrorBoundary；Vitest 9 用例；token 迁 sessionStorage；server.mjs 下发 CSP |
| 新端 | `desktop/`：Electron 33 + electron-builder，加载本地 frontend/dist 并反代 `/api`，安全基线完整（contextIsolation/sandbox/无 nodeIntegration）；`mobile/`：Capacitor 6 Android 壳，完成度低 |
| 测试实跑 | Gateway 216 通过；前端 Vitest 9 通过；`tsc -b` 无错误（.NET 测试本机受 NuGet 异常影响，需 CI 验证） |

### 1.2 测试与文件规模现状（实测）

| 组件 | 源文件规模 | 测试文件数 | 进 CI |
|---|---|---|---|
| AuthService | Program.cs **595 行**（仅抽 AuthHttp 177 行） | 4 | 是 |
| UserService | Program.cs 80 行 + 4 模块 | 3 | 是 |
| FileService | 单文件 + 队列/Worker 新文件 | 2（新建） | 是 |
| PracticeService | 四层；ReciteQuestionGenerator **731 行**（反增） | 13 | 是 |
| KnowledgeService | 四层 + 后台恢复（标杆） | 20 | 是 |
| ModelService | 四层 + ONNX，offload 已做 | 4 | 是（Integration 排除） |
| CreditService | 批查已做；API 37 行 | 1 文件 7 Fact | 是 |
| GalGameService | Program.cs **547 行** + 2 新模块 | 19 | 是 |
| RenderService | TS 会话层 + 文件快照 | 3 | 是 |
| OCRService | Python 单文件 | 0 | 否 |
| Gateway | 分层 + 缓存/指标中间件 | 17（216 用例） | 是 |
| Frontend | ReviewPage **884 行/26 useState**、api.ts 869、global.css 3358、types/api.ts 579、LandingPage 508 | 4 文件 9 用例 | 是 |

### 1.3 剩余债务热区（证据级）

1. **仓库卫生缺口（新增，最高优先）**：`desktop/build/galreview-codesign.cer` 未被 .gitignore 覆盖（`*.pfx` 已覆盖私钥 ✅，`.cer` 未覆盖 ❌）；`desktop/dist-installer/`（整包构建产物）、`mobile/android` 构建产物、`dist/`、`tmp/` 均未忽略，且 **tmp/pdfs 下 png 已被 git 跟踪**。一次 `git add desktop/` 即会把证书提交进历史。
2. **代码内默认兜底仍存**：`gateway/src/config.ts:70` 默认 `moonstone-local-gateway-key`；`AdminIdentity.cs:3` 与 `CreditService.API/Program.cs:11` 保留默认管理员 GUID（配置化机制已接入，兜底未拆）；各服务 appsettings*.json 内联服务 key 7+ 处；`compose.integration.yaml` 仍默认 Mock。
3. **后台任务"最后一公里"**：队列无重试/死信；`FileService/Program.cs:34,117` 仍有 2 处 `Task.Run`（启动恢复与 OCR 取消）。
4. **推理背压缺失**：ModelService 已 offload + 批次门 + `/readyz` 负载上报，但无请求队列上限、503 不带 `Retry-After`。
5. **前端回潮**：ReviewPage 884 行/26 useState（基线 ~820/20，不降反升）；vite 无 `manualChunks`（G6 未独立 chunk）；五张背景 PNG 共 ~9MB 未压缩（仅 landing 场景图转了 WebP）；`credits.ts` 仍硬编码 `DEFAULT_PURCHASE_URL`。
6. **部署脚本漂移**：`deploy-windows.ps1` 1670 行单文件，**未引用** compose.production.yaml 与 Test-ProductionEnv.ps1；`docs/windows-production.md` 未提及生产 overlay。
7. **CI 缺口**：无 secret 扫描（gitleaks）；无路径过滤（全量跑）；OCR 0 测试；Model 真机推理被排除。
8. **文档纪律**：`docs/test_report.md` 最新记录停在 **2026-08-10**，近两个月无测试证据入库。

---

## 2. 任务清单

> 每项含：目标 / 优先级 / 依赖 / 当前进度 / 待完成 / 验收标准。

### T0 · 在途成果入库（P0，最优先，1 天）

- **目标**：把工作区 18 天的未提交工作（46 个修改文件 +1293/−704，约 40 个新源文件）安全落库。
- **依赖**：无（但 T1 应在同一批或紧随其后，避免把构建产物/证书带进首次提交）。
- **进度**：✅ 已完成（2026-09-30，8 个提交已推送 ClassTechStar；后续 T2/T3 变更另行提交）。
- **待完成**：① 先做 T1（补 .gitignore）；② 按域分批提交：gateway 缓存+指标 → compose 三件套+CI → backend 队列/拆分 → 前端 lazy/测试 → desktop/mobile 壳；③ push 后确认 CI 全绿，把 .NET 结果记入 `test_report.md`。
- **验收**：`git status` 干净（除构建产物）；CI 六 job 全绿；test_report 有 2026-09 记录。

### T1 · 仓库卫生与敏感文件治理（P0，1 天）

- **目标**：证书、构建产物、临时文件绝不进入 git 历史。
- **依赖**：无；**必须先于 T0 的 desktop/mobile 提交**。
- **进度**：✅ 已完成——ignore 规则全覆盖（.cer/pfx/keystore/dist/tmp）；tmp 已跟踪文件已 `git rm --cached`；gitleaks 分诊归入 T3。
- **待完成**：`.gitignore` 追加 `desktop/build/*.cer`、`desktop/dist-installer/`、`desktop/node_modules/`、`mobile/android/.gradle/`、`mobile/android/app/build/`、`mobile/node_modules/`、`dist/`、`tmp/`；对已跟踪的 tmp 文件执行 `git rm -r --cached tmp`；CI 加 gitleaks（归入 T3）。
- **验收**：`git check-ignore desktop/build/galreview-codesign.cer` 命中；`git status --porcelain` 无产物条目；`git log` 无证书历史。

### T2 · 代码内默认密钥/身份兜底清除（P0，2–3 天）

- **目标**：生产配置缺失时 fail-fast，联调默认值只存在于 dev 路径。
- **依赖**：无（与 compose.production.yaml 已有 `${VAR:?}` 叠加）。
- **进度**：✅ 已完成（2026-09-30）——gateway `GATEWAY_KEY` 生产缺失即退出；8 个 .NET 服务禁止生产使用开发默认服务 key；Auth/Credit `Admin:PrincipalId` 生产 fail-fast + `/readyz` 上报 `adminConfigured`；production overlay 强制 `Admin__PrincipalId`；integration compose 显式降为 Development 联调栈；start_dev 固定 Development。
- **待完成**：① `gateway/src/config.ts:70` 移除默认 gateway key，缺配置启动即 exit 1（或仅 `NODE_ENV=development` 放行）；② `AdminIdentity.cs` 与 `CreditService.API/Program.cs:11` 的默认 GUID 改为 dev-only 兜底，生产缺配置不启动；③ `compose.integration.yaml` 的 Mock 默认限定在 integration 场景并在文件头注释声明"禁止直接用于生产"；④ 各服务 appsettings 内联服务 key 迁到环境变量（保留 dev 值但标记）。
- **验收**：空 `.env` 启动生产组合直接失败；仓库 grep 默认密钥仅存在于 dev 配置文件；`/readyz` 报告 Admin 是否已显式配置。

### T3 · CI 补强（P0→P1，2 天）

- **目标**：密钥泄漏与无关改动的 CI 成本都可控。
- **依赖**：T1（gitleaks 需 ignore 先到位）。
- **进度**：✅ 已完成（2026-09-30）——gitleaks job（全历史、钉版 8.30.1）+ `.gitleaksignore` 分诊 19 条历史指纹；dorny/paths-filter 路径过滤接入 5 个服务 job；Model Integration 排除经评审维持并记录于 ci.yml 头注。
- **待完成**：① 加 gitleaks job；② 核心服务 job 加 `paths` 过滤；③ 复审 Model Integration 排除策略（真实 ONNX 资产 hash 门禁后择机启用）。
- **验收**：含假密钥的 PR 被 gitleaks 拦截；仅改文档的 PR 不跑 dotnet job。

### T4 · 后台任务重试/死信（P1，3–5 天）

- **目标**：任何后台任务失败可重试、可观测、不静默丢失。
- **依赖**：Channel + BackgroundService 外廓（已完成 ✅）；GalGame 状态机 + 启动恢复（已完成 ✅）。
- **进度**：✅ 已完成（2026-09-30）——IngestionWorker 指数退避重试（`Ingestion:MaxAttempts` 默认 3）+ 死信保持 FAILED 可查；`AttemptCount` 落库；启动恢复收编进 Worker；OCR 取消改限时 await；GalGame 入队失败内联重试 3 次、AttemptCount 随 RUNNING 转换落库、**启动恢复修复 credits 泄漏（恢复置 FAILED 的作业现逐一释放 HELD 预授权）**。生成类任务的自动重试刻意不加：内部已有 provider/draft 多级重试且队列级重试会二次扣费——决策记录于此。
- **待完成**：① 两队列加 `AttemptCount`/最大重试与指数退避，终态 FAILED 落库供查询；② FileService `Program.cs:34,117` 两处 `Task.Run` 收编进 Worker；③ GalGame 的恢复经验（RUNNING 卡死置 FAILED）确认覆盖 File 解析与 Practice 生成。
- **验收**：kill -9 后重启，任务恢复或显式 FAILED；注入失败的任务重试 N 次后进入死信并可查。

### T5 · 推理队列与背压（P1，2–3 天）

- **目标**：ModelService 过载时优雅拒绝而非线程堆积。
- **依赖**：offload + 批次门（已完成 ✅）。
- **进度**：✅ 已完成（2026-09-30）——`Nli:MaxPendingBatches`（默认 64）有界排队，超限 503 + `Retry-After`（`MODEL_SERVICE_BUSY`）；`/readyz` 上报 `queuedBatches`；Practice 非 200→ABSTAINED 降级回归通过（68 用例）。
- **待完成**：① 有界请求队列（Channel 容量可配）；② 队满返回 503 + `Retry-After`；③ Practice 侧超时→ABSTAINED 路径回归；④ `/readyz` 报告队列深度（已有 inflight/completed，补 queued）。
- **验收**：压测打满队列时返回 503+Retry-After，进程内存平稳；`/readyz` 可见 queued。

### T6 · 前端大文件与资产瘦身（P1，1–2 周）

- **目标**：首屏 < 300KB gzip；单文件复杂度回到可维护区间。
- **依赖**：无；lazy 路由（✅）、ErrorBoundary（✅）、storyAssets 抽取（✅）已就绪。
- **进度**：✅ 已完成（2026-09-30）——`manualChunks` 拆 knowledge-graph chunk；5 张背景 PNG→WebP（9.07MB→668KB）；bgm.mp3 8.6→4.3MB；api.ts 869→39 行门面 + 8 个域文件；global.css 3358 行→6 文件（级联顺序不变）；ReviewPage 884→310 行（hooks + 展示组件）；购买链接兜底移除；vitest 11 用例。
- **待完成**：① `vite.config` 加 `manualChunks` 拆 `@antv/g6`；② 五张背景 PNG→WebP（目标 <300KB/张）、bgm.mp3 8.3MB 压缩或拆轨；③ ReviewPage（884 行/26 useState）拆 `useGameRuntime`/`useAudioStage`/`useStoryProgress` + 展示组件；④ api.ts 869 行按域拆门面；⑤ global.css 3358 行先拆最大三块；⑥ `credits.ts` 移除硬编码 `DEFAULT_PURCHASE_URL` 兜底。
- **验收**：bundle 分析 G6 不在首屏 chunk；首屏 JS gzip < 300KB；ReviewPage < 300 行。

### T7 · 部署脚本与文档对齐（P1，2–3 天）

- **目标**：Windows 生产部署走生产 overlay 与门禁脚本，文档与实际一致。
- **依赖**：compose.production.yaml / Test-ProductionEnv.ps1（已完成 ✅）。
- **进度**：✅ 已完成（2026-09-30，范围修正）——核实 `deploy-windows.ps1` 为**原生 Windows 部署**（IIS + 进程），不使用 Docker compose；其实际缺口是 T2 fail-fast 的兼容：已接入 `GALREVIEW_ADMIN_PRINCIPAL_ID`（初始化自动生成 GUID + 门禁必填 + Auth/Credit 注入），并在 docs/windows-production.md 写明两条生产路径（原生 vs compose overlay）互斥对照表。脚本拆模块（③）延后：无真实部署靶机可验证，盲改 1670 行生产脚本违背"先有网再动刀"，转 P2 待办。
- **待完成**：① 部署流程前置调用 Test-ProductionEnv.ps1，部署目标切换 compose.production.yaml；② docs/windows-production.md 补 overlay 用法；③ 脚本按 build/migrate/health/rollback 拆模块，保留低空间模式语义。
- **验收**：跑一次完整部署走通 overlay 路径；缺密钥时脚本在部署前失败。

### T8 · desktop/mobile 端治理（P1，3–5 天）

- **目标**：两个新端达到"可发布"的最小安全与工程标准。
- **依赖**：T1（产物 ignore）。
- **进度**：✅ 已完成（2026-09-30）——desktop 打包版网关地址必须显式 `GALREVIEW_GATEWAY_URL`（缺失弹窗退出，开发态保留默认）；mobile `cleartext:false`、`allowNavigation:[]`、`allowMixedContent:false`；新增 docs/desktop-release.md（签名生命周期）；CI 新增 desktop（win --dir 冒烟）与 mobile（gradle assembleDebug）job + 路径过滤。
- **待完成**：① mobile 收紧 `cleartext:false`、`allowNavigation` 白名单化；② desktop 的 `GALREVIEW_GATEWAY_URL` 默认值与生产发布物分离；③ 签名证书生命周期文档化（自签仅限内测）；④ 两端各加一条 CI 构建 job（desktop `--dir` 冒烟、mobile gradle assembleDebug）。
- **验收**：mobile release 构建无明文流量告警；desktop CI 产物可启动加载本地前端。

### T9 · 后端单体拆分收尾（P2，1–2 周）

- **目标**：消灭 >500 行的服务入口与生成器单文件。
- **依赖**：T3 CI 稳定绿（"先有网再动刀"）。
- **进度**：✅ 已完成（2026-10-14）——AuthService/Program.cs 579→**122** 行（注册/会话/密码/管理员/内省 5 组 Endpoint + Contracts/Repositories 拆出）；GalGameService/Program.cs 528→**184** 行（GameGenerationEndpoints/GalGameStartupRecovery/GalGameMiddleware）；ReciteQuestionGenerator 682→**116** 行（GroundedQuestionExtractor/ModelQuestionClient/QuestionTextParser/QuestionChunker）。行为由既有测试锁定：Auth 18 + GalGame 362 + Practice 68 全绿。
- **待完成**：无（验收已满足）。可选后续：MongoGameStore/PlanGraphClient 709 行按需再拆（非本任务范围）。
- **验收**：✅ 无 >400 行的 Program.cs；行为由现有测试锁定。

### T10 · 可观测性深化（P2，1–2 周）

- **目标**：跨服务排障一次定位；核心指标有告警。
- **依赖**：Gateway /metrics（✅ 已完成）。
- **进度**：✅ 已完成（2026-09-30 主体 + 2026-10-14 Auth/GalGame 接入）——① **八服务** JSON 结构化日志（`RequestLogging.cs`：AddJsonConsole + traceId/userId/path/method scope + durationMs/status 收尾行）：User/File/Practice/Credit/Knowledge/Model/**Auth/GalGame**；② X-Correlation-Id 跨服务透传补齐（Practice `TraceFlow`、GalGame `GalGameTraceFlow` AsyncLocal；Auth→User/Credit 与 GalGame→Credit 均回传原 traceId；后台生成任务显式 Begin）；③ 告警最低集：gateway `gateway_upstream_failures_total{service,kind}`、Practice `practice_abstained_total`、Credit `credit_release_failures_total`、File `ingestionQueueDepth`/Model `queuedBatches` readyz 上报；`docs/observability.md` 含阈值与 PromQL 伪查询。详见 `docs/test_report.md` §47/§48。
- **待完成**：无。可选后续：GalGame `generationQueueDepth` readyz 旁路指标。
- **验收**：✅ 一次注入故障可凭 traceId 串起 Gateway→Practice→Model→Credit（并可扩至 Auth/GalGame 全链）。

### T11 · 前端 E2E 冒烟（P2，3–5 天）

- **目标**：核心用户路径有自动化回归。
- **依赖**：Vitest 通道已进 CI（✅）。
- **进度**：✅ 已完成（2026-10-14）——Playwright 冒烟落地 `frontend/e2e/` + `frontend/playwright.config.ts`：登录→建项目/进册→答一题→看结果，桌面 1280×720 与移动 375×667 双视口；API 以 `page.route` 全量 mock（`e2e/apiMocks.ts`），不依赖真实后端。`npm run test:e2e`；CI 新增可选 `frontend-e2e` job（`continue-on-error: true`，不阻塞主链路）。
- **待完成**：CI 里 E2E job 稳定绿一周后转必需（去掉 `continue-on-error`）；本地/CI 首次需 `npx playwright install chromium`。
- **验收**：CI 里 E2E job 稳定绿一周后转必需。

### T12 · 测试盲区补齐与文档纪律（P2，持续）

- **进度**：✅ 2026-10-14 主体完成——FileService 10 测试文件（104 用例）、Credit 并发扣减/兑换防重放 15 用例、OCR 契约 16 用例全绿；test_report.md §46 更新纪律恢复。
- **待完成**：① MongoFileStore 启动恢复路径（`RecoverIncompleteJobsAsync`）集成测试；② OCR 真实识别精度/资源上限验证（contract.md §5.4 URGENT 项）；③ MySQL 仓储并发/行锁集成复核。
- **验收**：✅ FileService 10 测试文件 ≥8；✅ test_report 有本月记录（§46，2026-10-14）。

---

## 3. 依赖关系

```text
T1 仓库卫生 ──→ T0 成果入库 ──→ 后续一切（干净的基线）
T1 ──→ T3 CI 补强（gitleaks/paths）
T2 默认兜底清除   （独立，可与 T0/T1 并行）
T3 CI 稳定绿 ──→ T9 大文件拆分（动刀前提）
T4 任务重试/死信 ──→ T10 告警（死信/队列深度是告警输入）
T5 推理背压 ──→ T10（queue depth 指标）
T7 部署对齐 依赖既有 compose.production（无阻塞）
T8 端治理 依赖 T1
T6 前端瘦身 （完全独立，随时可做）
```

建议串行锚点：**T1 → T0 → T2**（第一周），T4/T5/T6 并行（第二、三周），T7/T8 随后，T9–T12 按余力展开。

---

## 4. 风险清单

| 风险 | 等级 | 影响 | 缓解 |
|---|---|---|---|
| 证书/构建产物被一次性 `git add desktop/` 提交 | **高** | .cer 入库（.pfx 已挡住）；历史污染需重写 | T1 先行；push 前跑 gitleaks |
| 大量未提交变更滞留工作区 | **高** | 误操作即丢失 18 天工作；无法回滚 | T0 分批提交，先 push 再继续开发 |
| 默认密钥/GUID 兜底被沿用到生产 | 中 | 未授权访问 / 身份混淆 | T2 fail-fast + overlay 已强制 |
| 后台任务失败静默丢失（无重试/死信） | 中 | 用户生成任务无响应且不可查 | T4 |
| ModelService 过载无限堆积 | 中 | 线程池饥饿拖垮整容器 | T5 背压 |
| ReviewPage 复杂度持续回潮 | 中 | 回归风险与迭代减速 | T6 拆分 + T11 E2E 兜底 |
| deploy-windows.ps1 与生产 overlay 漂移 | 中 | 实际部署绕过密钥门禁 | T7 |
| 内省缓存与吊销语义（30s 窗口） | 低 | 注销后短窗可访问 | 已文档化；高敏接口可绕过缓存 |
| Render 文件快照单实例语义 | 低 | 多副本部署会话不共享 | README 已声明单实例约束 |
| mobile Capacitor 宽松网络策略 | 低 | 明文流量 / 任意域跳转 | T8 收紧 |

---

## 5. 分阶段路线

### Phase A — 止血（2026-09-12 基线）✅ 全部完成

A1 CI 六 job · A2 生产 overlay 去 Mock · A3 内省缓存+single-flight · A4 材料校验并行 · A5 Credit 批查 · A6 lazy 路由 —— 均已实测确认落地。

### Phase B — 性能与可靠（2026-09-12 ~ 09-30）🟡 收尾中

| # | 项 | 状态 |
|---|---|---|
| B1 | Model offload + 批次门 + readyz 负载 | ✅（背压归入 T5） |
| B2 | Channel+BackgroundService + 启动恢复 | 🟡 → T4（重试/死信、File 两处 Task.Run） |
| B3 | Render 文件快照持久化 | ✅ |
| B4 | ErrorBoundary/storyAssets/lazy | ✅（ReviewPage 拆分与资产压缩 → T6） |
| B5 | Credit/File 测试起步 | ✅（T12 2026-10-14：File 10 文件 / Credit 并发+防重放 / OCR 契约） |
| B6 | 管理员身份与购买链接配置化 | 🟡 机制已接入，默认兜底未拆 → T2 |

### Phase C — 工程化加深（进行中）

| # | 项 | 状态 |
|---|---|---|
| C1 | 服务模块化 | ✅ T9：Auth 122 / GalGame 184 / Recite 116 行，测试锁行为 |
| C2 | 可观测性 | ✅ T10 八服务 JSON 日志 + 透传 + 告警最低集 |
| C3 | 前端测试 | ✅ Vitest 11 用例 + Playwright E2E 冒烟（T11，CI 可选 job） |
| C4 | sessionStorage + CSP | ✅ |
| C5 | 限流单实例约束文档化 | ✅ |
| C6 | 集成冒烟 workflow_dispatch | ✅（含反向密钥门禁） |

### Phase D — 新增（2026-09-30 起）

| # | 项 | 状态 |
|---|---|---|
| D1 | T0 在途成果入库 | ✅ 8 提交已推送 ClassTechStar |
| D2 | T1 仓库卫生 | ✅ ignore 全覆盖 + tmp 出库 |
| D3 | T2+T3 默认兜底清除与 CI 补强 | ✅ 已实施，证据 test_report §43 |
| D4 | T4+T5 可靠性收尾（重试/死信/背压） | ✅ 2026-09-30 |
| D5 | T6 前端瘦身回潮治理 | ✅ 2026-09-30 |
| D6 | T7+T8 部署对齐与双端治理 | ✅ 2026-09-30（T7③拆模块转 P2） |
| D7 | T9–T12 工程化持续 | ✅ 2026-10-14 全部完成（T9 拆分 / T10 八服务可观测 / T11 E2E / T12 测试盲区） |

---

## 6. 明确「不要做」的项

1. 不重拆已正确的领域边界（Model 不拥有 correct/SM-2；Knowledge 独占掌握度）。
2. 不引入 Service Mesh / k8s / Redis——单机 Compose + 文档化单实例约束与当前规模匹配。
3. 不在无金标扩展前优化 NLI 模型结构；先完成背压（T5）。
4. 不把 Wiki/静态页 SPA 化。
5. 不在 CI 稳定绿前大规模重构 Practice 生成器（T9 依赖 T3）。
6. 不给 desktop/mobile 端投入超过"可用壳"范围的精力——产品主线仍是 Web 复习闭环。

---

## 7. 成功度量（更新）

| 指标 | 当前基线（2026-09-30） | 目标 |
|---|---|---|
| 工作区未提交变更 | 0（T0 已入库） | 0 |
| git 历史中的证书/产物 | 0；ignore 全覆盖 + gitleaks 全历史扫描 | 0 且 ignore 全覆盖 |
| 代码内默认密钥/GUID 兜底 | 生产路径 0（服务守卫 + overlay `:?` + Test-ProductionEnv） | 生产路径 0 |
| 后台任务重试/死信 | 无 | 全队列覆盖 |
| 首屏 JS gzip | 未测（G6 仍在主包） | < 300KB |
| >500 行前端文件 | 5 个（ReviewPage 884） | 0 |
| E2E 冒烟 | 无 | 1 条核心路径进 CI |
| test_report 更新 | 停在 2026-08-10 | 每次变更附证据 |

---

## 8. 文档纪律

- 跨服务接口、状态、错误语义以 `docs/contract.md` 为准；本文件不创造新契约。
- 实施 P0/P1 变更时同步更新本文件状态列与对应服务 README；测试证据追加到 `docs/test_report.md`。
