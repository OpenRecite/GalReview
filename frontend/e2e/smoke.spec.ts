import { expect, test, type Page } from '@playwright/test'
import { fixtures, ids, installApiMocks } from './apiMocks'

/**
 * T11 核心用户路径冒烟（桌面 1280×720 / 移动 375×667 双视口）。
 *
 * 路径：登录 → 创建项目或进入复习项目 → 答一题 → 看结果。
 * API 全部 mock（见 apiMocks.ts），不依赖真实后端。
 *
 * 选择器约定：AppShell 的 PageHeader <h1> 在移动视口被 CSS 隐藏，
 * 页面级断言改用 URL + 两种视口都可见的正文元素（如 h2 / 列表项）。
 */

async function login(page: Page) {
  await page.goto('/login')
  await expect(page.getByRole('heading', { name: '登录 千知万理' })).toBeVisible()
  // FormField 的清除按钮 aria-label 含「邮箱/密码」，故用 name 定位输入框本体
  await page.locator('input[name="email"]').fill('e2e@example.com')
  await page.locator('input[name="password"]').fill('e2e-password')
  await page.getByRole('button', { name: /登录并继续/ }).click()
  // 登录成功后 Protected 放行并跳转 /home
  await expect(page).toHaveURL(/\/home$/)
  await expect(page.getByRole('heading', { name: /欢迎回来/ })).toBeVisible()
}

test.describe('核心复习闭环冒烟', () => {
  test('登录 → 进入研习册 → 答一题 → 看结果', async ({ page }) => {
    await installApiMocks(page, { withExistingProject: true })
    await login(page)

    // 进入研习册列表并打开既有项目
    await page.goto('/projects')
    await expect(page).toHaveURL(/\/projects$/)
    await expect(page.getByRole('heading', { name: '续读旧卷' })).toBeVisible()
    await page.getByRole('link', { name: new RegExp(fixtures.projectName) }).click()
    await expect(page).toHaveURL(new RegExp(`/projects/${ids.existingProjectId}$`))
    // 册详情：正文「本次温习范围」两种视口均可见（PageHeader h1 移动端隐藏）
    await expect(page.getByRole('heading', { name: '本次温习范围' })).toBeVisible()
    await expect(page.getByText(/道可练习题/)).toBeVisible()

    // 开始温习 → 进入练习会话
    await page.getByRole('button', { name: '开始温习' }).click()
    await expect(page).toHaveURL(new RegExp(`/practice/${ids.practiceSessionId}$`))
    await expect(page.getByRole('heading', { name: fixtures.questionPrompt })).toBeVisible()

    // 答一题（选项 A 为正确答案）
    await page.getByRole('button', { name: new RegExp(`A ${fixtures.optionAText}`) }).click()

    // 看结果：得分提示 + 完成练习
    const result = page.locator('.practice-result')
    await expect(result).toBeVisible()
    await expect(result).toContainText('完整掌握')
    await expect(result).toContainText('本题获得 5 分')
    await page.getByRole('button', { name: '完成练习' }).click()
    await expect(page.getByText('本轮练习已经记录')).toBeVisible()
    await expect(page.getByText(/共作答 1 道/)).toBeVisible()
  })

  test('登录 → 创建研习册 → 进入新册并答一题看结果', async ({ page }) => {
    await installApiMocks(page, { withExistingProject: true })
    await login(page)

    // 立册：名称 + 主资料 → 建立研习册（mock 完成识网/成题链路）
    await page.goto('/projects')
    await expect(page).toHaveURL(/\/projects$/)
    await expect(page.getByRole('heading', { name: '建立经典复习项目' })).toBeVisible()
    await page.getByLabel('研习册名称').fill('E2E 新建研习册')
    await page.getByLabel('主资料').selectOption(ids.materialId)
    await page.getByRole('button', { name: '建立研习册' }).click()

    // 创建链路结束后跳到新册详情
    await expect(page).toHaveURL(new RegExp(`/projects/${ids.createdProjectId}$`))
    await expect(page.getByRole('heading', { name: '本次温习范围' })).toBeVisible()

    // 新册内同样走「答一题 → 看结果」
    await page.getByRole('button', { name: '开始温习' }).click()
    await expect(page).toHaveURL(new RegExp(`/practice/${ids.practiceSessionId}$`))
    await expect(page.getByRole('heading', { name: fixtures.questionPrompt })).toBeVisible()
    await page.getByRole('button', { name: new RegExp(`A ${fixtures.optionAText}`) }).click()
    await expect(page.locator('.practice-result')).toContainText('完整掌握')
    await page.getByRole('button', { name: '完成练习' }).click()
    await expect(page.getByText('本轮练习已经记录')).toBeVisible()
  })
})
