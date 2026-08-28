# BlueSelf Windows 端 UI 优化方案（V2 修订稿 · 待确认）

> 状态：**计划待用户确认**，未实施。V2 按用户 2026-08-28 08:05 反馈修订。
> 证据来源：界面截图取证 + XAML/C# 源码走读。

---

## 一、V1 → V2 需求变更

| 项 | V1 方案 | 用户反馈 → V2 修订 |
|---|--------|-------------------|
| 附件托盘 | 有附件时保留托盘框（降对比） | **任何时候都不要空腔/边框**——有附件时直接裸排附件 chip，无附件整体隐藏 |
| 发送目标行 | 保留目标行，徽标改圆点 | **去掉整个目标行的空腔容器**；不显示状态（左侧列表已可见）；只留设备名；未选目标时空态同样无空腔 |
| 状态徽标 | 圆点化保留 | **取消**——目标区不再显示状态 |
| 手机图标 | 未涉及 | 用户发现疑似硬编码 → **已查实**：目标行图标写死 `\uE8EA`（手机），未跟随设备 Kind；需改为绑定 `SelectedDevice.Kind` 复用 `DeviceKindToGlyphConverter` |
| 目标展示形态 | 单独一行展示 | 用户给出两个方向待选：**方案 A** 仅左侧设备栏选中态表达，删除目标行；**方案 B** 保留目标行但去容器改裸排样式 |

---

## 二、查证结论：目标行手机图标是硬编码

- `WorkspaceView.xaml` 发送目标行：`<TextBlock Text="&#xE8EA;" ... Foreground="{DynamicResource BrandBrush}" />` —— **写死的手机图标**，与设备真实类型无关。
- 左侧设备列表 `DeviceListItem.xaml`：`Text="{Binding Kind, Converter={StaticResource KindToGlyph}}"` —— 正确做法，`DeviceItem.Kind`（"pc"/"tablet"/"phone"，由设备名推导）驱动三选一：
  - pc → `\uE968`（电脑）
  - tablet → `\uE970`（平板）
  - 其他 → `\uE8EA`（手机）
- 结论：无论方案 A/B，目标区图标都应改为 `Binding SelectedDevice.Kind` + `DeviceKindToGlyphConverter`；若选方案 A（删目标行），该问题随行一起消失。

---

## 三、两个候选方案（发送目标展示）

### 方案 A · 只在左侧设备栏表达（推荐）

删除编辑器卡片顶部的目标行（含空态提示条），目标信息完全由左侧设备栏承担：

- 左侧列表已有：目标设备行有品牌色描边高亮（`FluentListBoxItem` 选中态 `BrandLightBrush` 底 + `BrandBrush` 边框）+ 状态圆点 + 状态文字
- 编辑器恢复为纯净写作区：上无提示条、下无托盘框，只有文本区 + 底部操作行
- 未选目标时的引导：保留现有 `ShowTargetHint` 逻辑，但**不用常驻容器**——点发送时校验失败已在 MainViewModel 弹日志提示（`三合一校验`），可在编辑区 placeholder 或发送按钮态上做轻提示（如按钮禁用/点击时闪现提示），不再画框
- 影响面：删除 `WorkspaceView.xaml` Row 0 整块；`wsSendTo`/`wsTargetHint` 文案键可保留或清理；En.xaml 的 To: 问题随之消失

优点：编辑区最大化留白，无重复信息（状态本来就在左侧），布局最自然。
缺点：发送时视线需要扫左侧确认目标（但列表选中态高亮已经足够醒目）。

### 方案 B · 保留目标行，去容器改裸排

目标行继续存在，但去掉 Border 空腔：

- 无 Border、无底色、无边框，一行小字自然融入编辑区顶部：`[设备图标(随Kind)] 设备名`（品牌色），仅此而已
- 删除状态徽标、删除 `wsSendTo` 标签（或按用户偏好极简化）
- 未选目标：整行不渲染（编辑区顶无任何占位），点击发送时走现有校验提示
- En.xaml To: 间距问题：若保留 "To:" 标签则按 V1 方案修 Margin；若删除标签则问题消失

优点：编辑器内仍有明确的"当前目标"锚点。
缺点：信息与左侧列表重复（名称+选中高亮已在左侧表达）。

---

## 四、无争议任务（两方案共有，已确认要做）

| 顺序 | 任务 | 文件 | 改动 |
|-----|------|------|------|
| 1 | 窗口圆角（Win11 DWM，最大化切直角，Win10 静默降级） | MainWindow.xaml.cs | +30 行 |
| 2 | 附件托盘：删 Border 容器，附件裸排（横向 ItemsControl 保留，Margin 0,0,8,0），无附件时 Collapsed | WorkspaceView.xaml | ~12 行 |
| 3 | 目标行图标跟随设备 Kind（方案 B 时适用；方案 A 则整行删除） | WorkspaceView.xaml | 1 行 |
| 4 | 清理硬编码色值（#065F46 等）随目标行重构一并处理 | WorkspaceView.xaml | 检查项 |

**图标修复细节**（方案 B）：目标行 TextBlock 改为
`Text="{Binding SelectedDevice.Kind, Converter={StaticResource DeviceKindToGlyph}}"`，
并在 UserControl.Resources 中引入 `DeviceKindToGlyphConverter` 实例（当前在 DeviceListItem 里局部声明，需要提到共享资源或 WorkspaceView 里再声明一份）。

---

## 五、待用户确认

1. **方案 A 还是方案 B？**（推荐 A：目标信息左侧已有选中高亮 + 状态，编辑区去框后最干净）
2. 方案 A 下，未选目标点发送的引导方式：a) 维持现状（日志区提示） b) 发送按钮置灰禁用 c) placeholder 文案动态化为"请先在左侧选择目标设备"（推荐 c，最轻量自然）
3. 方案 B 下，目标行要不要保留 "To:"/"发送目标：" 文字前缀？（若保留，按 V1 方案修 En.xaml 间距）

---

## 六、验证计划

`dotnet build windows/FileTransferApp.WinUI` 编译 → 启动 BlueSelf.exe → 截图对比：
窗口四角圆角（含最大化还原）、无附件时编辑区无空腔、添加附件后 chip 裸排、目标展示形态（A：列表高亮；B：裸排设备名+图标）、
点发送未选目标时的引导反馈、深/浅主题 × 中/英语言各过一遍。
