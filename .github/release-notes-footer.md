## 📥 下载与安装

| 文件 | 大小 | 说明 |
| --- | --- | --- |
| `SillyTavernTray-win-x64.exe` | {{SIZE}} | 自包含单文件，双击即用，无需安装 .NET |
| `SHA256SUMS.txt` | — | SHA-256 校验值，可用下方命令核对 |

- **系统要求**：Windows 10 / 11 x64；已有一个 SillyTavern 安装（包含 `server.js`）；运行 SillyTavern 需要 Node.js，更新与依赖修复需要 Git 和 npm。
- **首次运行**：如果出现“Windows 已保护你的电脑”，点 **更多信息 → 仍要运行**。程序没有购买代码签名证书，可用 SHA-256 自行核对。
- **从旧版本升级**：不用手动下载，打开管理器点“检查更新”即可；本页下载适合首次安装或自动更新失败时使用。

## 🔐 校验下载

```powershell
Get-FileHash .\SillyTavernTray-win-x64.exe -Algorithm SHA256
# 结果应与 SHA256SUMS.txt 一致：
{{HASH}}
```

## 🚀 3 步开始

1. 双击运行程序；它会自动查找已有 SillyTavern，找不到时在设置里选择包含 `server.js` 的目录。
2. 托盘右键 → “打开管理窗口”，启动服务、复制本机 / 局域网地址或检查更新。
3. 需要登录自启时，在设置里勾选“登录 Windows 后启动管理器”（可选）。

{{GALLERY}}

<details>
<summary><b>它还能做什么</b></summary>

- 托盘常驻，双击打开管理窗口；关闭窗口只收起，不停止服务。
- 一键启动 / 停止 / 重启；端口占用、异常退出、未配置都有明确状态。
- 自动列出本机与局域网地址，一键复制。
- 检查 ST 更新、执行官方 Git/npm 更新、依赖修复（先预检，优雅停服）。
- 日志面板：来源筛选、搜索、向上滚动自动暂停、回到底部。
- 登录 Windows 后自启；可选随后启动 ST / 打开浏览器。
- 管理器自更新：检查、下载并校验 SHA-256，退出后安装。

</details>

## 🛡️ 它不会做什么

- 不修改 SillyTavern 的配置、白名单或认证。
- 不接管不是它启动的进程。
- 不上传日志或聊天数据；日志只在本机 `%LOCALAPPDATA%\SillyTavernTray\logs\`。
- 不强制更新：预检发现本地修改会停止；失败保留日志并保持服务停止，不自动重置或回退。

## 🐞 反馈

遇到问题请带上管理器版本（本页为 `v{{VERSION}}`）、Windows 版本、复现步骤，以及 `%LOCALAPPDATA%\SillyTavernTray\logs\` 下的日志。

- [提交 Issue](https://github.com/wacilimonster-source/SillyTavernDesktop/issues/new/choose)
- [README 与常见问题](https://github.com/wacilimonster-source/SillyTavernDesktop#常见问题)
- [完整更新日志]({{COMPARE_URL}})

---

如果它帮你省下了每天找启动脚本的时间，欢迎点一个 ⭐。
