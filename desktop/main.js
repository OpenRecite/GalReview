const { app, BrowserWindow, shell, Menu, dialog } = require('electron')
const path = require('path')
const fs = require('fs')
const http = require('http')

// 后端网关：优先环境变量 GALREVIEW_GATEWAY_URL。
// 打包发布物必须显式配置，避免静默把 API 指向用户本机的开发端口；
// 开发态（electron .）保留本机集成栈默认端口。
const configuredGatewayUrl = process.env.GALREVIEW_GATEWAY_URL?.trim()
const GATEWAY_URL = configuredGatewayUrl || (app.isPackaged ? null : 'http://127.0.0.1:5000')
const WEB_DIR = path.join(process.resourcesPath || __dirname, 'web')
const LOCAL_WEB = fs.existsSync(path.join(WEB_DIR, 'index.html'))
  ? WEB_DIR
  : path.join(__dirname, '..', 'frontend', 'dist')

let mainWindow = null
let localServer = null
let localPort = 0

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.webp': 'image/webp',
  '.avif': 'image/avif',
  '.svg': 'image/svg+xml',
  '.mp3': 'audio/mpeg',
  '.wav': 'audio/wav',
  '.woff': 'font/woff',
  '.woff2': 'font/woff2',
  '.wasm': 'application/wasm',
}

/** 内嵌静态站 + /api 反代到网关，保证 SPA 路由与 API 同源可用 */
function startLocalServer() {
  return new Promise((resolve, reject) => {
    if (!fs.existsSync(path.join(LOCAL_WEB, 'index.html'))) {
      reject(new Error('web dist missing'))
      return
    }
    const server = http.createServer((req, res) => {
      const url = req.url || '/'
      // API 代理
      if (url.startsWith('/api/') || url.startsWith('/healthz')) {
        const gateway = new URL(GATEWAY_URL)
        const proxyReq = http.request(
          {
            hostname: gateway.hostname,
            port: gateway.port || 80,
            path: url,
            method: req.method,
            headers: { ...req.headers, host: gateway.host },
          },
          (proxyRes) => {
            res.writeHead(proxyRes.statusCode || 502, proxyRes.headers)
            proxyRes.pipe(res)
          },
        )
        proxyReq.on('error', () => {
          res.writeHead(502, { 'Content-Type': 'application/json' })
          res.end(JSON.stringify({ data: null, error: { code: 'GATEWAY_UNAVAILABLE', message: '网关不可用' }, traceId: 'desktop' }))
        })
        req.pipe(proxyReq)
        return
      }
      // 静态资源
      let filePath = path.join(LOCAL_WEB, decodeURIComponent(url.split('?')[0]))
      if (!filePath.startsWith(LOCAL_WEB)) filePath = path.join(LOCAL_WEB, 'index.html')
      if (fs.existsSync(filePath) && fs.statSync(filePath).isDirectory()) {
        filePath = path.join(filePath, 'index.html')
      }
      if (!fs.existsSync(filePath)) filePath = path.join(LOCAL_WEB, 'index.html')
      const ext = path.extname(filePath).toLowerCase()
      res.writeHead(200, {
        'Content-Type': MIME[ext] || 'application/octet-stream',
        'Cache-Control': ext === '.html' ? 'no-cache' : 'public, max-age=86400',
        'X-Content-Type-Options': 'nosniff',
      })
      fs.createReadStream(filePath).on('error', () => res.destroy()).pipe(res)
    })
    server.listen(0, '127.0.0.1', () => {
      localServer = server
      localPort = server.address().port
      resolve(`http://127.0.0.1:${localPort}/`)
    })
    server.on('error', reject)
  })
}

function resolveStartUrl() {
  // file:// 下 BrowserRouter 会 404，因此优先内嵌 HTTP 服务
  if (fs.existsSync(path.join(LOCAL_WEB, 'index.html'))) {
    return { type: 'embedded', needsServer: true }
  }
  return { type: 'remote', url: 'http://127.0.0.1:5120/' }
}

function createMenu() {
  const template = [
    {
      label: '文件',
      submenu: [
        { label: '刷新', accelerator: 'F5', click: () => mainWindow?.reload() },
        { type: 'separator' },
        { role: 'quit', label: '退出' },
      ],
    },
    {
      label: '视图',
      submenu: [
        { role: 'reload', label: '重新加载' },
        { role: 'toggleDevTools', label: '开发者工具' },
        { type: 'separator' },
        { role: 'resetZoom', label: '实际大小' },
        { role: 'zoomIn', label: '放大' },
        { role: 'zoomOut', label: '缩小' },
        { type: 'separator' },
        { role: 'togglefullscreen', label: '全屏' },
      ],
    },
    {
      label: '帮助',
      submenu: [
        {
          label: '关于千知万理',
          click: () => {
            dialog.showMessageBox(mainWindow, {
              type: 'info',
              title: '关于千知万理',
              message: '千知万理 · GalReview',
              detail: `版本 ${app.getVersion()}\n一卷成册，循知而习。\n\n网关：${GATEWAY_URL}`,
            })
          },
        },
      ],
    },
  ]
  Menu.setApplicationMenu(Menu.buildFromTemplate(template))
}

async function createWindow() {
  // Windows 窗口/任务栏图标：优先 ICO，回退 PNG
  const windowIcon = fs.existsSync(path.join(__dirname, 'icons/icon.ico'))
    ? path.join(__dirname, 'icons/icon.ico')
    : path.join(__dirname, 'icons/icon-256.png')

  mainWindow = new BrowserWindow({
    width: 1280,
    height: 860,
    minWidth: 960,
    minHeight: 640,
    title: '千知万理',
    icon: windowIcon,
    backgroundColor: '#eef8fd',
    autoHideMenuBar: false,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      webSecurity: true,
    },
  })

  const start = resolveStartUrl()
  let url = start.url
  if (start.needsServer) {
    try {
      url = await startLocalServer()
    } catch {
      url = 'http://127.0.0.1:5120/'
    }
  }
  mainWindow.loadURL(url)

  mainWindow.webContents.setWindowOpenHandler(({ url: href }) => {
    shell.openExternal(href)
    return { action: 'deny' }
  })

  mainWindow.on('closed', () => {
    mainWindow = null
  })
}

// 单实例
app.setName('千知万理')
const gotLock = app.requestSingleInstanceLock()
if (!gotLock) {
  app.quit()
} else {
  app.on('second-instance', () => {
    if (mainWindow) {
      if (mainWindow.isMinimized()) mainWindow.restore()
      mainWindow.focus()
    }
  })

  app.whenReady().then(() => {
    if (!GATEWAY_URL) {
      dialog.showErrorBox(
        '缺少网关配置',
        '未配置后端网关地址。请设置环境变量 GALREVIEW_GATEWAY_URL（例如 https://galreview.example.com）后重新启动。',
      )
      app.quit()
      return
    }
    createMenu()
    createWindow()
    app.on('activate', () => {
      if (BrowserWindow.getAllWindows().length === 0) createWindow()
    })
  })

  app.on('window-all-closed', () => {
    if (process.platform !== 'darwin') app.quit()
  })
}

// 探测网关（供 preload / 启动提示）
function probeGateway() {
  return new Promise((resolve) => {
    const req = http.get(`${GATEWAY_URL}/healthz`, { timeout: 2000 }, (res) => {
      resolve(res.statusCode === 200)
      res.resume()
    })
    req.on('error', () => resolve(false))
    req.on('timeout', () => {
      req.destroy()
      resolve(false)
    })
  })
}

module.exports = { probeGateway, GATEWAY_URL }
