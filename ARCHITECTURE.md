# v2.0 跨平台工程

当前里程碑 **2.0.0-alpha.6**：只保留 Avalonia 宿主，清理旧 WPF 源码/项目/测试宿主与历史输出，建立三平台目录发布、原生目标隔离、逐文件/模型/归档校验和实际发布目录自检。`dev` 持续开发，`main` 保持稳定。

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

根 `BirdPhotoSelector.slnx` 是唯一解决方案入口；`Directory.Build.props` 统一版本和原生 RID 路径。没有 `UseWPF`、旧根项目或 `Imaging.Windows`。新宿主程序集名为 `BirdPhotoSelector`，命名空间仍按职责保留 `Desktop`。

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

`python scripts/Test.py --include-model --native-window` 顺序执行：图像核心 33、业务核心 50、实际后端 176、保留并移植的工作流 67、新界面 80 项（含真实模型），以及真实 Windows 窗口探针。本轮 Windows 均通过；旧 WPF 的 1451 项对照属于 alpha.5 历史记录，不作为本版本当前检查数。

保留的工作流覆盖分类多目标副本、同名覆盖/重命名/记住、硬链接原件保护、失败/取消屏障和编号；全部只操作私有生成夹具。图像和界面样本现在位于 `Tests/Assets/`，附作者和 CC BY-SA 署名，发布包不包含这些测试照片。

本地日志位于忽略的 `artifacts/`，正式测试输出位于 `publish/validation/`。程序的 `--self-test 报告.json` 测试 JPEG、两套 ABI、文件身份、YOLO 和中国候选 BioCLIP；`--smoke-test 报告.json` 打开最小化真实窗口、导入预览生成图并关闭。均不改用户照片或工程。

后续以 Mac/Ubuntu 目标机器的自检 JSON、桌面手动测试及文件行为结果推进 beta；完成验收前不合并 main。

平台基线参考：[.NET macOS 支持](https://learn.microsoft.com/dotnet/core/install/macos)、[Avalonia 平台支持](https://docs.avaloniaui.net/docs/supported-platforms)。实际发布仍以本项目所带原生文件和目标验收为准。
