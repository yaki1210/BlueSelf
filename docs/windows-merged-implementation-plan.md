# BlueSelf Windows 端 UI 优化 · 合并实施计划（方案 A + AddDevice 重做）

> 状态：**合并计划待确认**（2026-08-28）。两份需求文档：
> - 方案 A：`docs/windows-ui-redesign-plan.md`（已确认）
> - AddDevice 重做：`docs/windows-add-device-redesign-plan.md`（用户已写入，本计划默认按其执行）

---

## 1. 文件冲突矩阵（两计划叠加）

| 文件 | 方案 A | AddDevice | 冲突 |
|------|--------|-----------|------|
| `UI/MainWindow.xaml.cs` | DWM 圆角 P/Invoke | — | 无 |
| `UI/Views/WorkspaceView.xaml` | 删目标区、托盘去容器、placeholder 改绑 | — | 无 |
| `UI/Views/AddDeviceView.xaml` | — | 重写（扫描动画/流式列表/三态按钮/空态） | 无 |
| `ViewModels/MainViewModel.cs` | +PlaceholderText 属性与通知点 | ScannedDeviceItem 扩展、StartScan 流式化、ConnectAndAddAsync 走 PairAsync、KindOfName 换 DeviceKind | **同文件不同区域，合并编辑，无语义冲突** |
| `UI/Converters.cs` | — | DeviceKindToGlyphConverter 支持 "other" | 无 |
| `Resources/Strings/Zh.xaml` / `En.xaml` | 删 wsSendTo；wsTargetHint 转用 | 新增 adScan/adStopScan/adPairAndAdd/adConnect/adConnected/adEmptyHint1~3 | **同文件，键集不相交，合并编辑** |
| `Bluetooth/Core/DeviceKind.cs` | — | 新增（四信号分类器） | 无 |
| `Bluetooth/Core/DeviceWatcherScanner.cs` | — | 新增（流式扫描） | 无 |
| `Bluetooth/Core/DeviceScanner.cs` | — | 删除（被 watcher 替代） | 无 |
| `Bluetooth/Core/Discovery.cs` | — | IsLikelyPeerDevice 分类部分改调 DeviceKind（过滤放宽） | 无 |
| `app/.../BluetoothManager.kt`（Android） | — | 词表对齐 + sName 收到后校准 deviceType | 无 |
| `App.xaml` | （可选）FluentListBoxItem 微调 | — | 无 |

注意：`wsRefresh` 键保留——工作区设备栏刷新按钮仍在引用它，只是 AddDevice 顶栏不再误用。

## 2. 合并实施顺序

统一按"先 UI 后蓝牙、每阶段可编译可验证"推进：

1. **方案 A 全部 7 步**（窗口圆角 → 删目标区 → 托盘去容器 → placeholder → 文案清理 → 编译 → 截图验证）——纯 UI 层，独立可验
2. **AddDevice · Windows 分类器**：DeviceKind.cs + KindOfName/IsLikelyPeerDevice 调用点替换（独立可编译验证）
3. **AddDevice · 扫描重做**：DeviceWatcherScanner.cs + MainViewModel 扫描流式化 + ConnectAndAddAsync 走 PairAsync + 删 DeviceScanner.cs
4. **AddDevice · UI 重做**：AddDeviceView.xaml 重写 + Converters "other" + 新资源键（含修复横幅图标 Text 误绑 BoolToVis 的实锤 bug）
5. **AddDevice · Android**：BluetoothManager.kt 词表对齐 + sName 校准（约 15 行，可独立构建 APK）
6. **总验证**：两份验证矩阵合并执行（见 §4）

每个阶段结束 `dotnet build` 一次；阶段 1、4 额外截图核对；阶段 5 `gradlew assembleDebug` 构建验证。

## 3. 实施中已确认的技术事实

- AddDevice 横幅图标 bug 实锤：`Text="{Binding IsScanning, Converter={StaticResource BoolToVis}}"` 把 Visibility 当 Text 用 → 永远空/异常。修复为静态蓝牙 glyph `\uE706` + 扫描中旋转 Storyboard。
- 顶栏按钮文案误用 `wsRefresh`（"刷新设备列表"）实锤，重做后改 `adScan`/`adStopScan` 两态。
- `ScannedDeviceItem.Kind` 目前 = `KindOfName(Name)`（纯名字猜测），将被 DeviceKind 四信号替换。
- `Discovery.cs` 的 `IsLikelyPeerDevice` 同样走名字+CoD 过滤，是"扫不到新设备"的第二根因，按 AddDevice 计划放宽。

## 4. 合并验证矩阵

方案 A 的 9 项（圆角/最大化、顶栏无框、空/有附件、未选/已选 placeholder、主题×语言四组合）+ AddDevice 的 6 项（手机重扫 PC 图标、PC 流式扫描发现手机、扫描动画/空态/三态按钮、反向配对图标、Android sName 自愈、互发回归）。

**验证分工**：
- 可自动完成：双端构建、PC 端启动截图（工作区新布局、AddDevice 页空态/扫描中动画/列表态）、深浅主题×中英语言
- 需要用户配合真机：蓝牙实际扫描发现、系统配对弹窗两端确认、手机端图标验证、sName 自愈验证、互发回归

## 5. 风险叠加提示

- AddDevice 原风险表（AQS 属性键缺失、CanPair 过滤、PairAsync 拒绝等）全部继承
- 方案 A 的 Grid 行索引前移与 AddDevice 无交集（不同文件），无叠加风险
- 阶段 3 替换扫描器后，阶段 1 已验证的工作区截图不受影响（工作区不引用扫描器）
