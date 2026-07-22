# Codex Usage Overlay

一个面向 Windows Codex 桌面应用的轻量用量悬浮条。它跟随 Codex 窗口显示账户套餐、周额度、重置时间、可用重置券、累计 Token 和当前任务状态。

## 为什么做这个工具

Codex 的剩余用量信息需要进入“设置 → 剩余用量”才能查看。使用频繁时，为了确认还能用多少、什么时候重置，每次都要打开设置页面，会打断当前工作。

这个工具把常用信息放到 Codex 窗口顶部：周额度剩余比例、重置时间、重置券、累计 Token 和任务状态都能直接看到。目标很简单——不用离开当前任务，也不用反复点击设置，一眼就知道用量情况。

## 界面预览

以下图片由程序自身的界面绘制代码直接导出，不是 AI 生成图。

### 荧光蓝

收起：

![荧光蓝收起状态](docs/images/themes/neon-blue-collapsed.png)

展开：

![荧光蓝展开状态](docs/images/themes/neon-blue-expanded.png)

### 透明磨砂玻璃

收起：

![透明磨砂玻璃收起状态](docs/images/themes/frosted-glass-collapsed.png)

展开：

![透明磨砂玻璃展开状态](docs/images/themes/frosted-glass-expanded.png)

### 浅色渐变橙

收起：

![渐变橙收起状态](docs/images/themes/orange-gradient-collapsed.png)

展开：

![渐变橙展开状态](docs/images/themes/orange-gradient-expanded.png)

### 渐变粉

收起：

![渐变粉收起状态](docs/images/themes/pink-gradient-collapsed.png)

展开：

![渐变粉展开状态](docs/images/themes/pink-gradient-expanded.png)

## 功能

- 显示 Codex 返回的套餐名称，不自行添加“20X”等文案。
- 显示长周期／周额度剩余比例和重置时间。
- 显示可用重置券数量。
- 显示个人资料中的账户累计 Token（`summary.lifetimeTokens`）。
- 显示任务状态：处理中、完成、中断、检测中。
- 自动刷新，可在设置中调整刷新秒数。
- 支持荧光蓝、磨砂玻璃、渐变橙、渐变粉和自定义背景色。
- 跟随 Codex 窗口居中，支持窗口最大化和高 DPI 显示器。
- 随 Windows 登录自动启动，并创建桌面快捷方式。
- 支持用同一安装包直接覆盖更新。

## 系统要求

- Windows 10 或 Windows 11。
- 已安装并登录 Codex 桌面应用，或已安装并登录官方 Codex CLI。
- 系统自带 .NET Framework 4.x。

## 下载与安装

1. 下载 [CodexUsageOverlay-Setup-1.1.0.exe](https://github.com/ivan51769/CodexUsageOverlay/releases/latest/download/CodexUsageOverlay-Setup-1.1.0.exe)。
2. 可选：下载 [SHA256SUMS.txt](https://github.com/ivan51769/CodexUsageOverlay/releases/latest/download/SHA256SUMS.txt) 校验安装包完整性。
3. 双击安装包，按提示完成安装。
4. 安装结束后工具会自动启动，同时创建桌面快捷方式。
5. 打开 Codex，悬浮条会出现在窗口顶部菜单栏中央。

以后更新时直接运行新版安装包即可覆盖安装，设置会保留。

## 交给 Agent 一键安装

如果电脑上有 Codex、Claude Code 等可以执行 Windows 操作的 Agent，可以直接复制 [Agent 一键安装部署提示词](AGENT_INSTALL_PROMPT.md)。提示词包含环境检查、下载安装、SHA-256 校验、静默覆盖更新、启动验证和安全边界。

## 首次启用与账户读取

工具优先寻找 Codex 桌面应用随附的 `codex.exe`，也支持系统中已安装的 Codex CLI。只要 Codex 已正常登录，一般无需额外配置。

如果工具无法读取账户数据，可以安装并登录官方 Codex CLI：

```powershell
npm.cmd install --global @openai/codex
codex.cmd login --device-auth
```

如需指定自定义 CLI 路径，可设置用户环境变量：

```powershell
[Environment]::SetEnvironmentVariable(
  "CODEX_CLI_PATH",
  "C:\path\to\codex.exe",
  "User"
)
```

设置后重新启动工具。

## 使用方法

- 单击右侧齿轮：展开或收起设置面板。
- 字体：使用左右箭头切换。
- 外观：选择预设主题或自定义颜色。
- 自动刷新：使用 `−`、`+` 调整间隔，最短为 5 秒。
- 保存：应用并保存设置。
- 取消：放弃本次修改。
- 退出工具：结束悬浮工具；再次运行桌面快捷方式即可恢复。

## 数据来源与隐私

账户信息通过 Codex CLI 的本地 `app-server` JSON-RPC 接口读取，包括 `account/read`、`account/rateLimits/read` 和 `account/usage/read`。

任务状态通过本地 `.codex\sessions` 事件类型判断。工具不上传数据，不直接读取或写入 `auth.json`，也不保存对话正文。刷新失败时会保留上一次成功数据，避免阻塞 Codex 窗口。

运行数据保存在安装目录：

- `usage-cache.ini`：最近一次成功读取的套餐、额度和累计 Token。
- `settings.ini`：字体、主题、自定义颜色和刷新秒数。

这些本机文件均已从 Git 仓库排除。

## 从源码构建

```powershell
.\build.ps1
.\bin\CodexUsageOverlay.exe --snapshot
.\bin\CodexUsageOverlay.exe "--export-theme-previews=.\docs\images\themes"
```

安装包使用 Inno Setup 6 编译：

```powershell
ISCC.exe .\installer.iss
```

生成文件位于 `dist` 目录。

## 免责声明

本项目是非官方辅助工具，与 OpenAI 无隶属或背书关系。Codex 和 OpenAI 是其各自权利人的商标。

## ☕ 请作者喝杯咖啡

如果这个小工具节省了你的时间，可以自愿支持作者。

- 公众号：拾玖说跨境AI
- 作者：拾玖Blues

<img src="docs/images/support-wechat.png" alt="微信赞赏码" width="180">
