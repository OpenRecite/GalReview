import { Component, type ErrorInfo, type ReactNode } from 'react'

interface Props {
  children: ReactNode
  fallback?: ReactNode
  /** 用于 key，路由变化时重置边界 */
  resetKey?: string
}

interface State {
  error: Error | null
}

/**
 * 路由级错误边界：渲染崩溃时展示可恢复的兜底 UI，而不是白屏。
 */
export default class ErrorBoundary extends Component<Props, State> {
  state: State = { error: null }

  static getDerivedStateFromError(error: Error): State {
    return { error }
  }

  componentDidCatch(error: Error, info: ErrorInfo): void {
    console.error('[ErrorBoundary]', error, info.componentStack)
  }

  componentDidUpdate(prev: Props): void {
    if (this.state.error && prev.resetKey !== this.props.resetKey) {
      this.setState({ error: null })
    }
  }

  private reset = () => {
    // React.lazy 会缓存失败的 import；整页重载才能重新拉取 chunk
    window.location.reload()
  }

  render(): ReactNode {
    const { error } = this.state
    if (!error) return this.props.children
    if (this.props.fallback) return this.props.fallback
    return (
      <main className="app-loading" role="alert">
        <div style={{ maxWidth: 420, textAlign: 'center', padding: 24 }}>
          <h1 style={{ fontSize: 18, marginBottom: 8 }}>页面出现异常</h1>
          <p style={{ fontSize: 14, opacity: 0.75, marginBottom: 16 }}>
            {error.message || '发生了未知错误，请重试。'}
          </p>
          <button type="button" onClick={this.reset} style={{ cursor: 'pointer', padding: '8px 16px' }}>
            重试
          </button>
        </div>
      </main>
    )
  }
}
