const { contextBridge } = require('electron')

contextBridge.exposeInMainWorld('galreviewDesktop', {
  platform: process.platform,
  versions: {
    electron: process.versions.electron,
    chrome: process.versions.chrome,
    node: process.versions.node,
  },
})
