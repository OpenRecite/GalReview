# Windows 桌面端发布与签名

`desktop/` 是 Electron 33 壳工程：加载本地打包的 `frontend/dist`（经内置 HTTP 服务同源提供，
`/api` 反代到网关），安全基线为 `contextIsolation + sandbox + 无 nodeIntegration`。

## 网关地址（必读）

- **开发态**（`npm start` / `electron .`）：未设置 `GALREVIEW_GATEWAY_URL` 时默认
  `http://127.0.0.1:5000`（本机集成栈）。
- **打包发布物**：必须通过环境变量 `GALREVIEW_GATEWAY_URL` 显式指定网关（例如
  `https://galreview.example.com`）；未设置时应用启动即弹窗提示并退出，不再静默指向本机端口。
  发布渠道可在安装后由启动脚本、快捷方式目标或系统环境变量注入该值。

## 代码签名生命周期

签名钩子为 `desktop/build/sign.js`（electron-builder `sign` 钩子）：

1. **材料**：`WIN_CSC_LINK` 指向 PFX（含私钥），`WIN_CSC_KEY_PASSWORD` 为其密码。
   两者只通过环境变量/CI Secret 注入，**绝不入库**（`.gitignore` 已忽略 `*.pfx` 与
   `desktop/build/*.cer`）。
2. **当前状态**：`desktop/build/` 内的 `galreview-codesign.cer/.pfx` 为**自签测试证书**
   （本机开发/内测用），未设置 `WIN_CSC_LINK` 时钩子自动跳过签名。
3. **公开发布前**：改用受信任 CA（如 DigiCert/Sectigo 或 Azure Trusted Signing）签发的
   代码签名证书；自签证书会导致 SmartScreen 告警。
4. **轮换**：证书到期前 30 天签发新证书；更新 CI/发布机的 `WIN_CSC_LINK` 与密码；
   旧 PFX 立即从发布机与本地磁盘销毁。历史安装包不做重签。
5. **私钥泄露**：立即在 CA 侧吊销并轮换；吊销前签出的安装包需评估是否撤回。

## 构建与冒烟

```powershell
# 构建前端产物（extraResources 依赖 ../frontend/dist）
cd frontend && npm ci && npm run build
# 打包未签名目录版（CI 冒烟用）
cd ../desktop && npm ci && npx electron-builder --win --dir
# 完整 NSIS 安装包
npx electron-builder --win
```

CI 中 `desktop` job 等价执行上述目录版构建（不签名）；`mobile` job 构建 Capacitor
Android 调试 APK。
