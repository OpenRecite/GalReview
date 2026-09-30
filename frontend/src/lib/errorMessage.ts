/** 统一从未知错误提取可展示文案，替换各页面重复的 instanceof 判断。 */
export function errorMessage(reason: unknown, fallback = '操作失败，请稍后重试。'): string {
  if (reason instanceof Error && reason.message.trim()) return reason.message
  if (typeof reason === 'string' && reason.trim()) return reason
  return fallback
}
