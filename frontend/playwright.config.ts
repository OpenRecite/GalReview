import { defineConfig } from '@playwright/test'

/**
 * T11 前端 E2E 冒烟配置。
 *
 * - 两个视口项目：桌面 1280×720、移动 375×667。
 * - webServer 只起 Vite dev（端口 5123），API 一律由 e2e/apiMocks.ts 以
 *   page.route 拦截，不依赖本机后端 / gateway。
 * - 首次本地运行前需 `npx playwright install chromium`（CI 里对应
 *   `npx playwright install --with-deps chromium`）。
 */
const PORT = 5123
const baseURL = `http://127.0.0.1:${PORT}`

export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  forbidOnly: Boolean(process.env.CI),
  retries: process.env.CI ? 1 : 0,
  workers: process.env.CI ? 2 : undefined,
  reporter: process.env.CI ? [['list'], ['github']] : [['list']],
  timeout: 60_000,
  expect: { timeout: 10_000 },
  use: {
    baseURL,
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
    video: 'off',
  },
  projects: [
    {
      name: 'desktop-chromium',
      use: {
        browserName: 'chromium',
        viewport: { width: 1280, height: 720 },
      },
    },
    {
      name: 'mobile-chromium',
      use: {
        browserName: 'chromium',
        viewport: { width: 375, height: 667 },
        isMobile: true,
        hasTouch: true,
      },
    },
  ],
  webServer: {
    command: `npm run dev -- --port ${PORT} --strictPort`,
    url: baseURL,
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
  },
})
