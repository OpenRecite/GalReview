// electron-builder 自定义签名钩子：用本机自签 PFX + signtool
// 环境变量：
//   WIN_CSC_LINK  — pfx 路径（或 data:base64）
//   WIN_CSC_KEY_PASSWORD — pfx 密码
const { execFileSync } = require('child_process')
const fs = require('fs')
const path = require('path')

exports.default = async function sign(configuration) {
  const pfxPath = process.env.WIN_CSC_LINK
  const password = process.env.WIN_CSC_KEY_PASSWORD || ''
  if (!pfxPath) {
    console.warn('[sign] WIN_CSC_LINK not set, skip signing', configuration.path)
    return
  }
  if (!fs.existsSync(configuration.path)) {
    console.warn('[sign] file missing', configuration.path)
    return
  }

  // 定位 signtool（winCodeSign 缓存或 Windows SDK）
  const candidates = [
    path.join(process.env.LOCALAPPDATA || '', 'Temp', 'electron-builder-cache', 'winCodeSign', 'winCodeSign-2.6.0', 'windows-10', 'x64', 'signtool.exe'),
    path.join(process.env.LOCALAPPDATA || '', 'Temp', 'electron-builder-cache', 'winCodeSign', 'winCodeSign-2.6.0', 'windows-10', 'signtool.exe'),
    'C:\\Program Files (x86)\\Windows Kits\\10\\bin\\10.0.26100.0\\x64\\signtool.exe',
  ]
  const signtool = candidates.find((p) => fs.existsSync(p))
  if (!signtool) {
    console.warn('[sign] signtool.exe not found, skip', candidates)
    return
  }

  const args = [
    'sign',
    '/f', pfxPath,
    '/p', password,
    '/fd', 'SHA256',
    '/td', 'SHA256',
    '/tr', 'http://timestamp.digicert.com',
    configuration.path,
  ]
  try {
    execFileSync(signtool, args, { stdio: 'inherit' })
    console.log('[sign] signed', configuration.path)
  } catch (error) {
    // 时间戳服务器不可达时退回无时间戳签名
    console.warn('[sign] timestamp failed, retry without timestamp', error.message)
    execFileSync(signtool, ['sign', '/f', pfxPath, '/p', password, '/fd', 'SHA256', configuration.path], {
      stdio: 'inherit',
    })
    console.log('[sign] signed (no timestamp)', configuration.path)
  }
}
