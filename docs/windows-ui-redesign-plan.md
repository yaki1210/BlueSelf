# BlueSelf Windows 端 UI 优化 · 最终计划（方案 A，已确认）

> 状态：**计划已确认，待实施**。用户于 2026-08-28 08:10 选定方案 A。
> 证据：界面截图（docs/windows-ui-current.png）+ XAML/C# 源码走读。

---

## 确认结论

- 发送目标展示：**方案 A** — 删除编辑器顶部目标行与提示条，目标信息完全由左侧设备栏承担
- 未选目标引导：**placeholder 动态化**（推荐项 c，用户选 A 未单独反对，按推荐执行；如需改动实施前可提）
- 附件托盘：彻底去容器，chip 裸排，无附件隐藏
- 窗口：Win11 DWM 圆角，最大化直角，Win10 静默降级

---

## 改动清单（按实施顺序）

### 1. 窗口圆角 — `UI/MainWindow.xaml.cs`

- P/Invoke `dwmapi.dll` 的 `DwmSetWindowAttribute`
- 构造函数（`InitializeComponent()` 后）调用：`DWMWA_WINDOW_CORNER_PREFERENCE(33)` = `DWMWCP_ROUND(2)`
- 同时设置 `DWMWA_BORDER_COLOR(34)` = `DWMWA_COLOR_NONE(0xFFFFFFFE)` 去掉 Win11 默认 1px 描边（贴合现有自绘深色边框）
- 订阅 `StateChanged`：`WindowState.Maximized` → `DWMWCP_DONOTROUND(1)`；还原 → `ROUND(2)`
- 全部包 try/catch，Win10 / dwmapi 不可用时静默保持直角

### 2. 删除编辑器顶部目标区 — `UI/Views/WorkspaceView.xaml`

- 删除 Grid.Row=0 的整个 Border（含 `ShowTargetHint` 提示条 + `IsTargetRowVisible` 目标行两块）
- Grid 行定义从 4 行减为 3 行，下方编辑区/附件/操作行 Row 索引前移
- 图标硬编码问题随行删除自然消失（目标行不再存在）
- 硬编码 `#065F46` 一并清除

### 3. 附件托盘去容器 — `UI/Views/WorkspaceView.xaml`

- 删除托盘 Border（`SidebarBgBrush` 底 + 边框 + CornerRadius 10）
- 保留横向 `ItemsControl` + `AttachmentChip`，chip 自带 `Margin="0,0,8,0"` 提供间距
- 无附件时整个 ItemsControl `Collapsed`：绑定 `HasPendingAttachments`（新转换器 `HasItemsToVisibilityConverter` 已存在于 Converters.cs，直接复用，绑定 `Attachments.Count` 或集合）
- 位置：编辑区与操作行之间，仅在有附件时出现，无边框无底色

### 4. 未选目标引导 — `UI/Views/WorkspaceView.xaml` + `ViewModels/MainViewModel.cs`

- 新增 `PlaceholderText` 计算属性：`SelectedDevice == null` → 用 `wsTargetHint`（"请在左侧选择要发送的目标设备"）；已选 → 用 `wsPlaceholder`（原占位文案）
- `SelectedDevice` setter 及各处 `_selectedDevice` 赋值点补 `OnPropertyChanged(nameof(PlaceholderText))`
- XAML 占位 TextBlock 改绑 `PlaceholderText`
- 语言切换时同步刷新（挂进现有 `NotifyStatusText`/语言切换链路）
- 点发送校验逻辑不动（三合一校验 + 日志提示保留）

### 5. 左侧选中态强化（方案 A 的目标可视性兜底）— `App.xaml`

- 现状已可用：`FluentListBoxItem` 选中态 `BrandLightBrush` 底 + `BrandBrush` 描边
- 微调：选中态边框 1.5 → 1.2，hover 与选中区分已足够；此项保守，如目测已清晰则跳过
- 附带：目标设备行的"连接/设为目标"按钮文案维持现状（在线时 ActionText=设为目标，本身已表达当前目标）

### 6. 文案资源清理 — `Resources/Strings/Zh.xaml` / `En.xaml`

- `wsTargetHint` 保留（用作 placeholder 引导文案）
- `wsSendTo` 两份语言文件均不再被引用 → 删除（En.xaml 的 To: 尾随空格问题随之消失）
- `wsPlaceholder` 保留
- `wsAttachLabel` 检查引用（托盘去容器后如无引用一并删除）

### 7. 编译 + 截图验证

- `dotnet build windows/FileTransferApp.WinUI/FileTransferApp.WinUI.csproj`
- 杀掉运行中的 BlueSelf（PID 23172）→ 启动新编译产物 → 截图
- 验证矩阵：

| 验证点 | 预期 |
|-------|------|
| 窗口四角 | Win11 圆角，无系统描边 |
| 最大化/还原 | 圆角正确切换，无残缺 |
| 编辑区顶部 | 无目标行、无提示条，纯文本区 |
| 无附件 | 托盘区域完全消失，编辑区直通操作行 |
| 有附件 | chip 裸排无边框 |
| 未选目标 | placeholder 显示"请在左侧选择…"，点发送仍被校验拦截 |
| 选中设备后 | placeholder 恢复正常文案；左侧行高亮 |
| 深色/浅色 | 两主题各截图核对 |
| 中文/English | 两语言 placeholder 均正确切换 |

---

## 涉及文件汇总

| 文件 | 改动类型 |
|-----|---------|
| `UI/MainWindow.xaml.cs` | 新增 DWM P/Invoke + StateChanged（约 +35 行） |
| `UI/Views/WorkspaceView.xaml` | 删目标区、删托盘 Border、placeholder 改绑（净减约 30 行） |
| `ViewModels/MainViewModel.cs` | 新增 PlaceholderText 属性 + 通知点（约 +10 行） |
| `Resources/Strings/Zh.xaml` | 删 wsSendTo（其余保留） |
| `Resources/Strings/En.xaml` | 删 wsSendTo（To: 问题消失） |
| `App.xaml` | （可选）FluentListBoxItem 选中态微调 |

不改动：业务逻辑、蓝牙连接流程、发送校验、Inbox/Settings/AddDevice 视图、主题色板。

## 风险与回退

- 全部改动为 UI 层，git 可一键回退
- DWM API 失败不影响功能（静默直角）
- 行索引前移注意 TransferStatusPanel 的 Grid.Column 不受影响（列布局未动）
