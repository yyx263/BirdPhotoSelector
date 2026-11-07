# v2.0 跨平台工程

当前里程碑 **2.0.0-alpha.9**：导出全部评级及原件移除后的 RAW 缩略图恢复；沿用 alpha.8 的大图 GPU 缓存和解码/显示链路优化。alpha.6 已完成：只保留 Avalonia 宿主，清理旧 WPF 源码/项目/测试宿主与历史输出，建立三平台目录发布、原生目标隔离、逐文件/模型/归档校验和实际发布目录自检。`dev` 持续开发，`main` 保持稳定。

## 依赖方向

```text
Desktop (Avalonia)
  ├─ Application ─ Domain ─ Imaging / Foundation
  │      └─ Recognition ─ Domain / Imaging
  ├─ Catalog ─ Domain
  ├─ Imaging.Portable ─ Imaging.Native ─ Imaging
  └─ Platform ─ Application / Foundation
```

| 项目 | 职责 |
| --- | --- |
| Foundation | 本机文件路径、扩展名及鸟种身份比较 |
| Imaging | 不可变 RGB 帧、统一采样、预加载缓存、视图与选区几何 |
| Imaging.Native | LibRaw UTF-8 ABI 3、安全像素租约和串行门 |
| Imaging.Portable | Skia JPEG、八种方向、EXIF 与真实 RAW 显示像素 |
| Domain | 照片/原件/分组/评级/鸟框/鸟种、稳定身份与可见编号 |
| Recognition | YOLO/BioCLIP 2 推理、固定预处理和置信度 |
| Application | 导入、背景分组、识别、人工编辑/撤销、会话、工程/自动保存、导出 |
| Platform | 原生文件身份与 Windows/GIO/Foundation 回收站 |
| Catalog | 离线名称、拼音/首字母及 OpenCC 简繁搜索折叠 |
| Desktop | 依赖组装、Avalonia 控件、对话框和异步显示协调 |

根 `BirdPhotoSelector.slnx` 是唯一解决方案入口；`Directory.Build.props` 统一版本和原生 RID 路径。没有 `UseWPF`、旧根项目或 `Imaging.Windows`。设置 `DisableTransitiveFrameworkReferenceDownloads`，避免 SDK 为未使用的潜在传递引用预取 WPF/ASP.NET 定位包或运行包；保留显式 .NET Core 所需下载。新宿主程序集名为 `BirdPhotoSelector`，命名空间仍按职责保留 `Desktop`。

## 边界与维护约定

- 领域/用例不持有 UI 位图、窗口、句柄或平台调用。`ApplicationServices` 显式注入图像和文件能力；模型按操作延迟创建并负责释放。
- `DesktopController` 按会话/预览、Recognition、Export 分文件，窗口也分离识别和文件交互。规则只由共享用例及 `PhotoSelectionSession` 实现。
- 后台读取前快照可变会话，返回独立提案；完成并检查取消后在 UI 应用。单张重建成功才替换，正常零检出清空，失败/取消保持旧结果。
- 人工结果保护、删除鸟种同步删除框、更换成为无框人工结果、可见编号、名称排序、最近十种和撤销归共享会话。全部结果删除后解除批量保护。
- 源类及函数说明职责、生命周期、所有权和失败语义；添加功能优先扩展用例/接口，不把规则堆入窗口。

## 图像、视图和内存

不可变 `ImageFrame` 已应用 EXIF/RAW 方向，左上角为原点，像素尺寸为实际显示方向；鸟框采用 0..1 归一化坐标。解码完成释放输入文件，公共接口不暴露可写缓冲。

JPEG 使用 SkiaSharp 3.119.4，EXIF 使用 MetadataExtractor 2.9.3。`Resize` 固定 BilinearPixelCenter-v1；BioCLIP 的上下文裁切、抗混叠双三次和逐遍取整独立固定，不与普通显示采样混用。

Avalonia 缓存入口只保留一份不可变 BGRA，双预览共享。缓存前台优先、共享解码、JPG 前后各两张、RAW 只前台、LRU 和版本失效；窗口预算 512 MiB，按至少四字节/像素计费。预算仅约束保留帧，不包含解码工作内存、显示引用、缩略图、模型/张量和 UI 资源；超大前台可显示但不保留。

`DetailViewTransform` 统一适应到 200%、70% 鸟视图、锚点缩放、边界钳制和白框。手动选区从可见照片内开始，结束裁切到可见边界，拒绝小于 8 显示像素或 4 原图像素。换图、Esc、布局变化或任务开始取消未提交手势。

当前选择与实际已显示照片分开。加载时保留旧帧、禁止误操作；递增版本与独立取消避免迟到覆盖。关闭时先退出 Closing 调用栈再异步确认/取消/保存，最后关闭，避免关闭阶段重新显示窗口。

## 文件、工程与原件保护

文件路径 Windows 不区分大小写，Unix 保留大小写；原生桥接使用已打开句柄查询设备/节点，避免在托管层硬编码 stat 布局。未知身份保守禁止覆盖。跨机器工程路径重定位尚未加入；加载跳过缺失文件并提示。

工程保持 JSON v3、读取 v1/v2/v3；记录原件、分组、评级、位置、自动/人工结果、最近选择和视图。自动保存复用同一持久化和写入门，原子替换，未命名/不可写路径使用本机恢复目录。

导出固定计划后提醒全列表未标记照片；分类模式自动检查未分类、失败框及低置信度，其他有效鸟种仍可继续。冲突通过异步纯数据回调交给窗口；关闭或取消返回取消，记住操作只限本批次。

复制/覆盖使用 SHA-256、原件/硬链接保护、临时文件与校验；全部目标成功才允许移除对应原件。取消也必须应用已经发生的逐成员移除结果。Windows shell、Linux GIO、macOS Foundation 都只在明确不支持回收站时降级永久删除，其他错误保留原件。

## 三平台原生和发布

- `.tools/native/<rid>/` 隔离目标库；MSBuild 用目标 RID 选文件，普通构建使用宿主 RID。外平台发布禁止自动调用 Windows 原生构建。
- Windows 使用固定且校验过的 LibRaw 0.22.3 Win64 SDK 和本地 MSVC，随包带应用私有 CRT。
- Unix 本机可用 CMake/固定源码构建；Windows 交叉编译使用固定 Zig 0.17.1、未修改的官方 LibRaw 0.22.3 源码。交叉构建不启用可选外部图像编解码库或 OpenMP；不使用内嵌 JPEG 冒充 RAW。
- Mac 交叉构建的 `native/macos-imports` 仅声明桥接使用的少量公开 Foundation/Objective-C 导入符号，不提供系统实现；运行仍加载系统 Foundation 和 libobjc。本机 macOS 构建使用 Xcode SDK。导入表不能当作完整 SDK 扩展，新增 API 应同步核查公开签名和目标实测。
- Linux 最低 glibc 2.35，Mac arm64 最低 macOS 14；每个平台的原生库都检查 PE/ELF/Mach-O 架构，不能复制 Windows DLL 充当 Unix 组件。
- `Build-Release.py` 顺序 self-contained 发布、关闭裁剪/单文件、验证模型和依赖、准备 Mac .app、生成逐文件清单、归档并重读每个归档成员验证哈希。存在同名输出时停止。
- 只有当前宿主平台自动运行实际发布程序的无界面自检与真实窗口探针；清单分别记录构建宿主和 `ExecutedOnTarget`。Mac 本机打包可做 ad-hoc 签名，Windows 交叉生成的 .app 未做开发者证书签名/公证。
- 测试包按三套分发，不将其称为已验收正式版；系统依赖、桌面字体/输入法、回收站、性能和签名仍需目标机器验证。

## 验证入口与记录

`python scripts/Test.py --include-model --native-window` 顺序执行：图像核心 33、业务核心 50、实际后端 176、保留并移植的工作流 67、新界面 109 项（含真实模型），以及真实 Windows 窗口探针。本轮 Windows 均通过；旧 WPF 的 1451 项对照属于 alpha.5 历史记录，不作为本版本当前检查数。

保留的工作流覆盖分类多目标副本、同名覆盖/重命名/记住、硬链接原件保护、失败/取消屏障和编号；全部只操作私有生成夹具。图像和界面样本现在位于 `Tests/Assets/`，附作者和 CC BY-SA 署名，发布包不包含这些测试照片。

本地日志位于忽略的 `artifacts/`，正式测试输出位于 `publish/validation/`。程序的 `--self-test 报告.json` 测试 JPEG、两套 ABI、文件身份、YOLO 和中国候选 BioCLIP；`--smoke-test 报告.json` 打开最小化真实窗口、导入预览生成图并关闭。均不改用户照片或工程。

后续以 Mac/Ubuntu 目标机器的自检 JSON、桌面手动测试及文件行为结果推进 beta；完成验收前不合并 main。

平台基线参考：[.NET macOS 支持](https://learn.microsoft.com/dotnet/core/install/macos)、[Avalonia 平台支持](https://docs.avaloniaui.net/docs/supported-platforms)。实际发布仍以本项目所带原生文件和目标验收为准。

## alpha.7 界面和交互

- 控件主题集中定义普通/强调按钮、评级按钮和列表项模板。明确继承前景色、内容对齐、选中、悬停、按下、键盘焦点与禁用状态，避免 Fluent 默认状态盖住应用配色。缩略图内容不再绘制不透明背景，选中绿底由容器统一控制。
- 参数保持均分/借宽布局，11px 双颜色标签/值；缩略图格式标签、23px 评级角标、44px 四色置位按钮、38px 正方品牌标和紧凑滑块统一尺寸。两块预览分别显示居中的空状态。
- 会话结构刷新时建立照片到所属组/旧评级索引和四级计数，单次评级只刷新所属组的 Rating、计数和当前评级；结构变动仍通过 SetGroups/Refresh 发布。Dirty 始终递增编辑修订，只有布尔标记变化才通知窗口标题，自动保存/保存竞争语义不变。
- PhotoPane 图像和覆盖层为两个保留绘制节点；白框/鸟框改变仅影响覆盖层。细节只提交视口可见源矩形，使用双线性采样，不再每次修改白框就重新提交静态全图，不增加整图副本或缓存预算。
- 固定 5000 组、200 次评级并读取统计的通知基准：约 357ms → 1–3ms，组通知 1000000 → 200，标题通知 400 → 1；1000 次脏标记更新的标题通知 1000 → 1。该数字是通知基准，不是桌面帧率保证。
- 新增真实鼠标/键盘焦点与主题检查、空状态/正常/最小窗口截图，以及 24MP 连续缩放拖拽后静态全图图像层仍只绘制一次的检查。Windows 桌面检查 109 项（包含真实模型）；报告位于 artifacts/desktop-2.0-alpha7 和 interaction-before/after.json。
- 对外归档命名 macos-arm64，内部仍保留 osx-arm64 标准 RID。三平台包仍区分宿主验证与交叉编译，Mac/Ubuntu 实机验收未完成。

## alpha.8 真实渲染与切图

上阶段通知/绘制次数检查未覆盖 GPU 纹理上传。固定 Avalonia 12.1.5 的默认 Skia 资源缓存为 29,491,200 字节（约 28MiB），小于一张 24MP 不透明纹理约 92MiB；UI 发生评级或视图重绘时仍可能反复上传整图。

- `DesktopRendering` 统一真实桌面的 Skia 配置：GPU 资源缓存目标 512MiB，按需增长而非预分配。CPU 保留帧预算仍为 512MiB；两项独立计费，渲染目标、在途资源、驱动、解码、缩略图及模型另计，不是进程/显存硬上限。保留原来的 GPU/软件回退，不强制更换图形后端。
- `Bgra32ImageFrame` 保留 JPEG 解码后的显示布局，公共算法接口仍输出 RGB24 副本。定向直接置换完整 BGRA 像素，RGB 采样规则不变；全尺寸显示避免 BGRA→RGB→BGRA 的整图往返。小缩略图和显式缩放继续保存紧凑 RGB，不扩大照片列表的长期存储。
- RAW/托管 RGB 显示采用跨 x64/ARM64 的 Vector128 四像素重排及标量尾部；每次 16 字节读取均有边界检查，不通过降分辨率、降低显示倍率或改变模型输入提速。
- 缓存命中先提交画面；只快照前后各两张，邻居文件状态查询转到后台。提交邻域前在缓存锁内复查取消，迟到旧工作不能覆盖新选择；当前原件的文件版本仍正常验证。
- 未标记实心点统一缩至 11px，其他评级符号及按钮尺寸不变；主窗口批量入口改为“全部识别”，人工保护规则不变。

Windows/OpenGL 原生窗口固定两张 24MP 图、70 帧缩放/换图对照：默认缓存中位绘制 16.10ms、P95 24.10ms、总用时约 10.97s；仅使用 512MiB 缓存后中位约 0.046ms、P95 0.078ms、总用时约 1.47s。此为固定场景、此电脑的真实绘制测量，不是所有设备帧率保证。`Tests/Desktop/NativeRenderProbe.cs` 通过仅测试使用的 Skia lease 读取真实上下文，未来升级 Avalonia 时必须复核该不稳定诊断 API。

24MP 生成 JPEG 的热缓存文件→显示帧中位约 254ms → 138ms，显示转换阶段约 92ms → 21ms，托管分配约 168MB → 96MB。夹具和程序均在同机，不包含 GPU 首次上传时间，也不能代表所有压缩文件的解码速度。

本轮 Windows 检查：图像核心 50、业务 50、实际后端 176、工作流 67、桌面 111（含真实模型）；另有真实窗口探针。像素检查覆盖 BGRA 所有权、裁切、八方向、固定缩放及 SIMD 短尾精确相等。日志 `artifacts/regression-alpha8-final.log`；真实绘制对照为 `render-default.json` / `render-expanded.json`，解码对照为 `decode-before.json` / `decode-after.json`。

复测入口：`dotnet run --project Tests/Desktop/BirdPhotoSelector.Desktop.Checks.csproj -c Release -- --render-probe`（框架默认基线），追加 `--expanded` 使用正式渲染配置；`--decode-probe` 测量 JPEG 到显示帧阶段。两个原生窗口测量会短暂显示并自动关闭。

来源核对：[Avalonia 12.1.5 SkiaOptions](https://github.com/AvaloniaUI/Avalonia/blob/12.1.5/src/Skia/Avalonia.Skia/SkiaOptions.cs)。

## alpha.9 全评级导出和换源缩略图

`ExportChoice.All` 只取消评级过滤，仍通过共享 `ExportService.CreatePlan` 固定原件范围、鸟种目录与文件版本，再沿用现有复制校验/冲突/原件保护流程。未标记与分类核对提醒继续由窗口统一处理，不因导出全部绕过。

文件操作完成后先应用逐成员真实移除结果，随后只重建活动原件确实变化且仍有成员的照片。缩略图恢复逐张在后台通过 `IPhotoImageDecoder.ReadInfo/Decode(maxWidth: 160)` 读取 RAW，不把已移除的 JPG 缩略图重新挂回领域对象。回到 UI 后核对活动成员身份和路径再提交尺寸、参数与缩略图；不修改评级、鸟种或坐标核对规则。使用窗口生命周期令牌，保证导出取消不会阻止已成功移除 JPG 的 RAW 缩略图恢复；关闭时停止，单张解码失败汇入提示而不丢失已完成文件结果。

回归覆盖四种评级全部导出、格式与鸟种范围、实际副本字节、非当前照片换源、取消后的真实删除状态；有本地 RAW 样本时再通过实际 LibRaw 恢复缩略图并校验原件哈希。

本轮验证：图像核心 50、业务 50、实际后端 176、工作流 67、桌面 124（含真实模型及 RAW 样本），另有真实窗口预览检查。核心/业务日志位于 `artifacts/regression-alpha9.log`，修正新增测试夹具后完成的桌面与实际窗口结果位于 `artifacts/desktop-alpha9.log`。
