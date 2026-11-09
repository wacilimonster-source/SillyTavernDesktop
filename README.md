# SillyTavern 托盘管理器

在 Windows 上后台管理已有的 SillyTavern：启动、停止、查看日志、调用官方命令更新，以及设置登录自启。管理器不替代 SillyTavern 的网页，也不修改现有 Tailscale 设置。

## 使用

1. 从 [GitHub Releases](https://github.com/wacilimonster-source/SillyTavernDesktop/releases/latest) 下载并运行 `SillyTavernTray-win-x64.exe`。程序会尝试查找已有安装；找不到时，在设置中选择包含 `server.js` 的 SillyTavern 目录。服务以 `node server.js --listen` 的行为后台启动。
2. 管理窗口默认出现在屏幕右下角，程序只显示在系统托盘，不在任务栏显示按钮。关闭或最小化窗口会收进托盘；双击托盘图标可以重新打开，右键托盘图标可以控制服务和退出。
3. 如需登录自启，在“设置”中勾选“登录 Windows 后启动管理器”。该选项只作用于当前 Windows 用户；登录前及电脑睡眠时服务不保证可访问。
4. 如果提示端口 8000 已占用，说明可能还有另一个实例在运行。请先自行停止原有的命令行实例，再在管理器中点击“启动服务”。管理器不会结束或接管已有进程。

默认保留 SillyTavern 的白名单和认证配置。当前设置下并非所有局域网设备都一定能连接；如果访问被拒绝，请检查 SillyTavern 配置以及 Windows 防火墙。

日志保存在 `%LOCALAPPDATA%\SillyTavernTray\logs\`。窗口显示最近记录并支持搜索，完整记录可通过“日志目录”查看。日志可能包含访问地址等信息，分享前请自行检查。

## 更新与修复

**SillyTavern 服务更新**：主窗口的“检查 ST 更新”和“更新 ST”操作现有 SillyTavern 仓库。更新在预检通过并停止由管理器启动的服务后，调用当前官方脚本中的 `git pull --rebase --autostash` 和 `npm install --no-save --no-audit --no-fund --loglevel=error --no-progress --omit=dev --ignore-scripts`，成功后重新启动。预检检测到本地修改时不会停止服务；更新过程失败后会保留日志并保持服务停止，不执行强制重置或自动回退。

**托盘管理器自身更新**：默认在启动后和运行期间定期检查本仓库的正式 GitHub Release；也可以从托盘菜单选择“检查管理器更新”，或在设置中关闭自动检查。新版本会后台下载并校验 GitHub 提供的 SHA-256，等用户退出管理器后安装；如果关机时未完成安装，下次启动时会在启动服务前尝试安装。它不会自动更新 SillyTavern，也不会为了更新管理器而中断正在使用的服务。

“依赖修复”执行同一条官方 npm 安装命令。Git、npm 和 Node.js 使用电脑已有的安装，更新及依赖安装可能需要网络。

检查更新、更新及依赖修复时，程序每次读取当前 Windows 用户的系统代理设置，分别按 Git 上游地址和 npm 仓库地址选择代理或直连；代理设置只传给本次 Git/npm 子进程，不修改 Windows、Git 或 npm 的持久配置。如果系统代理不可读取，会提示错误而不会静默改用其他代理。SSH 类型的 Git 远端仍按 SSH 自身配置连接。对于 PAC 中按域名区分代理的特殊规则，npm 进程使用按其默认仓库地址解析的代理；依赖下载若跨多个域名，可能无法逐域完全复现 PAC 规则。

## 开发与构建

项目使用 .NET 10 Windows 桌面框架和 .NET 10 SDK。

```powershell
dotnet run --project 'tests\TraySmokeTests\TraySmokeTests.csproj' -c Release
dotnet publish 'SillyTavernTray.csproj' -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o 'publish\win-x64'
```

发布文件是自包含的 Windows x64 程序，不要求目标电脑另行安装 .NET；仍需要可用的 Node.js、npm 和 Git。测试使用临时目录、临时 Git 仓库和隔离的注册表键，不操作实际 SillyTavern 安装或真正的登录启动项。

发布新管理器版本时，先更新 `SillyTavernTray.csproj` 的 `Version`、提交并推送，再创建并推送匹配的 `v版本号` 标签；GitHub Actions 会构建并发布 Windows x64 程序。自动更新只读取正式 Release，要求资产名为 `SillyTavernTray-win-x64.exe`，并校验 GitHub 提供的资产 SHA-256。

程序图标取自 [SillyTavern Launcher](https://github.com/SillyTavern/SillyTavern-Launcher)，其许可见 [assets/SillyTavern-Launcher-LICENSE.txt](assets/SillyTavern-Launcher-LICENSE.txt)。
