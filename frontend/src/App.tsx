import { Suspense, lazy, useEffect, useRef, useState, type ReactNode } from 'react'
import { BrowserRouter, Navigate, Route, Routes, useLocation } from 'react-router'
import LandingPage from './pages/LandingPage'
import LoadingIndicator from './components/LoadingIndicator'
import ErrorBoundary from './components/ErrorBoundary'
import { api, ApiClientError } from './lib/api'
import { clearSession, readSession } from './lib/session'
import { readAdminSession } from './lib/adminSession'
import { recoverWorkflow } from './lib/workflowRecovery'

// 路由级代码分割：首屏只加载 Landing；其余页面按需加载
const ForgotPasswordPage = lazy(() => import('./pages/ForgotPasswordPage'))
const AdminLoginPage = lazy(() => import('./pages/AdminLoginPage'))
const AdminPage = lazy(() => import('./pages/AdminPage'))
const HomePage = lazy(() => import('./pages/HomePage'))
const KnowledgeGraphPage = lazy(() => import('./pages/KnowledgeGraphPage'))
const KnowledgePointsPage = lazy(() => import('./pages/KnowledgePointsPage'))
const LoginPage = lazy(() => import('./pages/LoginPage'))
const NotFoundPage = lazy(() => import('./pages/NotFoundPage'))
const RegisterPage = lazy(() => import('./pages/RegisterPage'))
const ReviewPage = lazy(() => import('./pages/ReviewPage'))
const SettingsPage = lazy(() => import('./pages/SettingsPage'))
const StudyFlowPage = lazy(() => import('./pages/StudyFlowPage'))
const PracticeProjectsPage = lazy(() => import('./pages/PracticeProjectsPage'))
const PracticeProjectPage = lazy(() => import('./pages/PracticeProjectPage'))
const PracticeSessionPage = lazy(() => import('./pages/PracticeSessionPage'))
const SharedPracticePackagesPage = lazy(() => import('./pages/SharedPracticePackagesPage'))

const routeDepth: Record<string, number> = {
  '/login': 0,
  '/register': 1,
  '/forgot-password': 1,
  '/admin/login': 0,
  '/admin': 1,
  '/home': 2,
  '/projects': 3,
  '/shared-projects': 3,
  '/materials': 3,
  '/knowledge': 3,
  '/knowledge-graph': 3,
  '/review': 3,
  '/settings': 3,
}

const pageTitles: Record<string, string> = {
  '/login': '登录',
  '/register': '注册',
  '/forgot-password': '找回密码',
  '/admin/login': '管理员登录',
  '/admin': '观测台',
  '/home': '起点',
  '/projects': '研习册',
  '/shared-projects': '复习资源中心',
  '/materials': '藏书阁',
  '/knowledge': '拾知',
  '/knowledge-graph': '识网',
  '/review': '回响',
  '/settings': '我的',
}

function resolvePageTitle(pathname: string) {
  if (pathname.startsWith('/projects/') && pathname.endsWith('/story')) return '故事回响'
  if (pathname.startsWith('/projects/')) return '研习册'
  if (pathname.startsWith('/practice/')) return '温习'
  return pageTitles[pathname] ?? '页面未找到'
}

function Protected({ children }: { children: ReactNode }) {
  const location = useLocation()
  const [session, setSession] = useState(() => readSession())
  const [ready, setReady] = useState(false)
  const [valid, setValid] = useState(() => Boolean(readSession()))

  // 登出/令牌刷新失败会广播 galreview:session；必须重读并失效，否则停留在死会话页面
  useEffect(() => {
    const onSessionChange = () => {
      const next = readSession()
      setSession(next)
      if (!next) {
        setValid(false)
        setReady(true)
      }
    }
    window.addEventListener('galreview:session', onSessionChange)
    return () => window.removeEventListener('galreview:session', onSessionChange)
  }, [])

  useEffect(() => {
    if (!session) {
      setValid(false)
      setReady(true)
      return
    }
    let active = true
    setReady(false)
    void api.getSession(session.session.sessionId).then((remoteSession) => {
      if (remoteSession.status !== 'ACTIVE') {
        throw new ApiClientError('登录状态已失效，请重新登录。', 'AUTH_REQUIRED', 401)
      }
      return recoverWorkflow()
    }).then(() => {
      if (active) setValid(true)
    }).catch((reason: unknown) => {
      if (!active) return
      // 401/404：明确失效；其它错误也 fail-closed，避免半验证会话继续调用 API
      if (reason instanceof ApiClientError && (reason.status === 401 || reason.status === 404)) {
        clearSession()
      }
      setValid(false)
    }).finally(() => { if (active) setReady(true) })
    return () => { active = false }
  }, [session?.session.sessionId])

  if (!session || !valid) return <Navigate replace to="/login" state={{ message: '登录状态已失效，请重新登录。' }} />
  if (!ready) return <main className="app-loading"><LoadingIndicator label={`正在加载${pageTitles[location.pathname] ?? '页面'}`} /></main>
  return children
}

function AdminProtected({ children }: { children: ReactNode }) {
  return readAdminSession() ? children : <Navigate replace to="/admin/login" />
}

function AnimatedRoutes() {
  const location = useLocation()
  const previousPath = useRef(location.pathname)
  const previousDepth = routeDepth[previousPath.current] ?? 0
  const currentDepth = routeDepth[location.pathname] ?? 0
  const direction = previousPath.current === location.pathname
    ? 'neutral'
    : currentDepth > previousDepth ? 'forward' : currentDepth < previousDepth ? 'back' : 'neutral'

  useEffect(() => {
    previousPath.current = location.pathname
  }, [location.pathname])

  useEffect(() => {
    if (location.pathname === '/') {
      document.title = '千知万理 · 一卷成册，循知而习'
    } else {
      document.title = `${resolvePageTitle(location.pathname)} · 千知万理`
    }
  }, [location.pathname])

  return (
    <div className={`route-transition route-transition--${direction}`} key={location.key}>
      <ErrorBoundary resetKey={location.pathname}>
        <Suspense fallback={<main className="app-loading"><LoadingIndicator label="正在加载页面" /></main>}>
          <Routes location={location}>
            <Route path="/" element={<LandingPage />} />
            <Route path="/login" element={<LoginPage />} />
            <Route path="/register" element={<RegisterPage />} />
            <Route path="/forgot-password" element={<ForgotPasswordPage />} />
            <Route path="/admin/login" element={<AdminLoginPage />} />
            <Route path="/admin" element={<AdminProtected><AdminPage /></AdminProtected>} />
            <Route path="/home" element={<Protected><HomePage /></Protected>} />
            <Route path="/projects" element={<Protected><PracticeProjectsPage /></Protected>} />
            <Route path="/projects/:projectId" element={<Protected><PracticeProjectPage /></Protected>} />
            <Route path="/projects/:projectId/story" element={<Protected><ReviewPage /></Protected>} />
            <Route path="/shared-projects" element={<Protected><SharedPracticePackagesPage /></Protected>} />
            <Route path="/practice/:sessionId" element={<Protected><PracticeSessionPage /></Protected>} />
            <Route path="/materials" element={<Protected><StudyFlowPage /></Protected>} />
            <Route path="/knowledge" element={<Protected><KnowledgePointsPage /></Protected>} />
            <Route path="/knowledge-graph" element={<Protected><KnowledgeGraphPage /></Protected>} />
            <Route path="/review" element={<Protected><Navigate replace to="/projects" /></Protected>} />
            <Route path="/settings" element={<Protected><SettingsPage /></Protected>} />
            <Route path="*" element={<NotFoundPage />} />
          </Routes>
        </Suspense>
      </ErrorBoundary>
    </div>
  )
}

export default function App() {
  return (
    <BrowserRouter>
      <AnimatedRoutes />
    </BrowserRouter>
  )
}
