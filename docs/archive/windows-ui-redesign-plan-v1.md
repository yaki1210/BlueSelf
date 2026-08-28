# BlueSelf Windows 端 UI 优化方案（调研稿 · 待确认）

> 状态：**计划待用户确认**，未实施。确认后按「任务清单」逐项落地。
> 证据来源：界面截图取证（2026-08-28）+ XAML 源码走读。

---

## 一、现状截图

![当前界面](windows-ui-current.png)

应用：BlueSelf（WPF，.NET 10，`windows/FileTransferApp.WinUI`），自绘无边框窗口，默认浅色主题 + 中文（本截图为深色主题，切换主题可见）。

---

## 二、问题定位（截图 + 源码双重证据）

| # | 用户反馈 | 实际表现 | 根因定位 | 严重度 |
|---|---------|---------|---------|--------|
| 1 | 应用窗口改为圆角 | 窗口四角为直角。`MainWindow.xaml` 中 `WindowChrome CornerRadius="0"`，`WindowStyle="None"` 无系统圆角 | WPF 传统窗口默认矩形；Win11 可用 DWM API 画圆角，但需代码显式开启 | 中 |
| 2a | 发送区背景框不好看 | 附件托盘是一个几乎全空的空腔：整宽 Border（`SidebarBgBrush` 底 + 1px 边框，Padding 10,8）里只有一个横向 ItemsControl，**没有附件时依然渲染完整空框**，与上方"发送目标"提示条、下方大小文字形成三层叠框，视觉噪 | `WorkspaceView.xaml` 附件托盘 Border 无空态折叠 | 高 |
| 2b | 状态标识是大深绿矩形看不清 | "发送目标"行内 `OnlineLightBrush` 底 + `CornerRadius="4"` + 深绿文字 `#065F46` 的**大色块徽标**（含"在线/离线"文案），深色主题下 `OnlineLightBrush=#0F2E22` 与文字对比度低，块大突兀，且**硬编码色值未随主题联动** | WorkspaceView.xaml 目标行徽标 Border；颜色硬编码绕过了主题资源 | 高 |
| 3 | 英文 "To:" 与设备名贴太近 | `En.xaml` 中 `wsSendTo` = `"To: "`（带尾随空格），但 XAML 里 `wsSendTo` 与设备名是**两个相邻 TextBlock**，WPF 渲染时**尾随空格被吞**，变成 "To:**Pixel 9a**" | 文案 + 渲染方式双重原因；中文「发送目标：」用全角冒号所以没事 | 低（但影响英文观感） |

补充发现（调研中新看到的问题，一并列入方案，可选做）：

| # | 问题 | 位置 |
|---|-----|------|
| B1 | 附件为空时托盘 Border 仍占位（同 2a 根因） | WorkspaceView.xaml |
| B2 | 窗口边缘 6px ResizeBorderThickness 区域鼠标悬停无视觉反馈 | MainWindow.xaml |
| B3 | 窗口四角在最大化时应禁用圆角（避免残缺） | 需配合问题 1 处理 |

---

## 三、修改方案

### 方案 1：窗口圆角（Win11 DWM）

**目标文件**：`UI/MainWindow.xaml.cs`（新增 P/Invoke）+ `UI/MainWindow.xaml`（`WindowChrome CornerRadius` 微调）

**做法**：在 MainWindow 构造后调用 `DwmSetWindowAttribute(DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2)`。同时：
- 监听 `StateChanged`，最大化时切回 `DWMWCP_DONOTROUND = 1`（避免最大化圆角残缺），还原时切回圆角
- 兼容性兜底：Win10 上 DWM 调用失败则静默保持直角（不影响功能）
- 可选增强：`DWMWA_BORDER_COLOR = 34` 设为 `DWMWA_COLOR_NONE` 去掉 Win11 默认 1px 描边，更贴合现有自绘深色边框

**预计代码量**：~30 行 C#，无 XAML 结构改动。

### 方案 2：发送区视觉降噪

**目标文件**：`UI/Views/WorkspaceView.xaml` + `Resources/Themes/Light.xaml` / `Dark.xaml`

**做法**：
1. **附件托盘空态折叠**：给托盘 Border 加 `Style` + `DataTrigger`（绑定 `HasAttachments`，需在 `MainViewModel` 补一个该属性到 Workspace 上下文，目前挂在 Inbox 相关类上）——无附件时 `Collapsed`，有附件才出现，消除空腔
2. **状态徽标小型化**：把"发送目标"行的**大色块徽标**（`OnlineLightBrush` 大底 + 深绿文字）改成**微型圆点 + 轻文字**风格，与左侧设备列表 `DeviceListItem.xaml` 的 8px 状态圆点（`StatusToBrushConverter`）视觉语言对齐：
   - 圆点颜色沿用现有 `StatusToBrushConverter`（在线=绿 / 连接中=黄 / 失败=红 / 离线=灰），不再新造色值
   - 删除独立的"大深绿矩形"Border，设备名后直接跟 8px 圆点 + 可选小字状态
   - 顺带修复硬编码 `#065F46`，颜色全部走主题资源，深浅主题自动适配
3. **托盘有附件时的样式**：保留圆角 10 底框，但底色从 `SidebarBgBrush` 改为更轻的 `SurfaceBrush`（降低与编辑区的层次冲突），边框用 `BorderSubtleBrush`（已有资源）

### 方案 3：英文 "To:" 间距修复

**目标文件**：`Resources/Strings/En.xaml` + `UI/Views/WorkspaceView.xaml`

**做法**（二选一，推荐 A）：
- **A. 文案层**：`En.xaml` 中 `wsSendTo` 从 `"To: "` 改为 `"To"`，在 XAML 两个 TextBlock 之间显式加一个 `Margin="0,0,4,0"` 的固定间距（或者把 wsSendTo 那行 TextBlock 单独设 `Margin`），空格不再依赖字符串尾部，中英文都不会被吞
- B. 渲染层：改用 `xml:space="preserve"` 强制保留尾随空格——改动更小但语义不清，不推荐

**同步检查**：中文 `wsSendTo`「发送目标：」保持不变（全角冒号自带间距，无此问题）。

---

## 四、任务清单（确认后按此执行）

| 顺序 | 任务 | 文件 | 预计改动 |
|-----|------|------|---------|
| 1 | 窗口圆角 + 最大化还原切换 | MainWindow.xaml.cs | +30 行 P/Invoke |
| 2 | 附件托盘空态折叠 | WorkspaceView.xaml + MainViewModel.cs | ~10 行 |
| 3 | 状态徽标小型化（圆点化） | WorkspaceView.xaml | ~15 行 |
| 4 | 托盘底色/边框降对比 | WorkspaceView.xaml | 2 属性 |
| 5 | En.xaml "To:" 间距修复 | En.xaml + WorkspaceView.xaml | 2 行 |
| 6 | 全部文案/颜色走主题资源 | WorkspaceView.xaml | 检查项 |

**验证方式**：`dotnet build windows/FileTransferApp.WinUI` 编译通过 → 启动 BlueSelf.exe → 截图对比圆角 / 空托盘 / 徽标 / To: 间距 → 深浅主题 + 中英语言各看一眼。

---

## 五、遗留确认项

1. 状态徽标改成"圆点 + 状态字"后，是否还需要保留"在线/离线"文字？（推荐保留，但字色用 `TextSecondaryBrush`，不再大色块）
2. 附件托盘空态是完全隐藏，还是保留一个 1px 虚线"点击下方 + 添加附件"的极简提示区？（推荐完全隐藏，编辑区更干净）
3. Win10 下圆角不可用（系统限制），是否接受"Win11 圆角 + Win10 直角"的降级？（DWM 圆角 API 是 Win11 独有）
