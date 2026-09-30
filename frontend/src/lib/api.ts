/**
 * API 门面（facade）。
 *
 * 实现按域拆分在 `src/lib/api/` 目录下：
 * - `client.ts`：request / requestRawJson / requestBlob 核心、令牌刷新、错误信封解析；
 * - 其余文件按业务域分组（auth / users / materials / knowledge / galgame / practice / admin / credits）。
 *
 * 域文件只允许 `import { ... } from './client'`，禁止互相引用，避免循环依赖。
 * 本文件保证既有调用方 `import { api, ApiClientError } from './lib/api'` 零改动。
 */
import * as auth from './api/auth'
import * as users from './api/users'
import * as materials from './api/materials'
import * as knowledge from './api/knowledge'
import * as galgame from './api/galgame'
import * as practice from './api/practice'
import * as admin from './api/admin'
import * as credits from './api/credits'

export * from './api/client'
export * from './api/auth'
export * from './api/users'
export * from './api/materials'
export * from './api/knowledge'
export * from './api/galgame'
export * from './api/practice'
export * from './api/admin'
export * from './api/credits'

export const api = {
  ...auth,
  ...users,
  ...materials,
  ...knowledge,
  ...galgame,
  ...practice,
  ...admin,
  ...credits,
}
