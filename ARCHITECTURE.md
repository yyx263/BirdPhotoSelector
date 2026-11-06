# v2.0 跨平台架构与迁移计划

本文件是持续维护的工程说明，提交到 `dev`；`docs/` 只存放本地临时输出，不进入 Git。
当前里程碑：**2.0.0-alpha.5，Avalonia 识别、人工编辑与导出流程**。新宿主已接通中国候选批量识别、单张重建、手动框选、鸟种搜索/更换/删除/撤销，以及分类导出、同名冲突与原件处理。保留 WPF 完整功能对照入口；实际运行验证仍以 Windows 为限，跨平台实机与发布验收尚未完成。

## 版本与分支

- Gitea `main` 保持稳定，不为迁移执行合并、变基、重置或推送；`dev` 接收小步、可验证的 v2.0 提交。
- `2.0.0-alpha.N` 表示迁移中，功能对齐后进入 beta，三平台验收后再考虑正式版。
- 迁移不覆盖已有发布目录、ZIP 或用户原照片；本阶段不生成正式安装包。
- 所有源码项目和验证项目统一升级到 .NET 10，SDK 以 `global.json` 固定为 10.0.403、允许同一功能带的补丁更新。下载地址和官方 SHA-512 在 `scripts/DotNetSdk.json`，项目内安装保留已有工具且不修改系统。WPF 对照宿主也使用 net10.0-windows。

## 依赖方向与实施阶段

继续使用 C#，最终桌面界面采用 Avalonia。领域和用例不依赖 UI、WPF 位图或直接系统调用。

```text
Desktop.Wpf（完整功能对照） / Desktop.Avalonia（当前迁移宿主）
  ├─> Application ─> Domain ─> Imaging / Foundation
  │       └─> Recognition ─> Domain / Imaging
  ├─> Catalog ─> Domain
  ├─> Imaging.Portable ─> Imaging.Native ─> Imaging
  ├─> Platform ─> Application / Foundation
  └─> Imaging.Windows ─> Imaging.Native / Imaging（只供当前 WPF 显示和对照）
```

1. **图像层，alpha.1 已完成**：独立图像契约、像素接口和有界缓存，隔离 Windows 实现，算法与缩略图使用不可变图像帧。
2. **领域与用例，alpha.2 已完成**：拆出 Domain/Application/Recognition；统一依赖组装、照片会话、人工编辑、撤销、工程和推理生命周期。
3. **后端与系统适配，alpha.3 已实现并通过 Windows 验证**：固定 JPEG/EXIF 依赖、统一缩放规则、UTF-8 RAW C ABI/CMake、文件路径/身份/回收站、离线简繁检索及独立验证入口。macOS/Ubuntu 原生编译、文件行为和像素对照仍待目标机器验收。
4. **Avalonia 界面，分两步推进**：alpha.4 升级 .NET 10，完成独立新宿主、照片导入/列表、无闪烁双预览、缩放边界、选中同步、导航、评级、背景分组、拆组/移除/清空、工程及自动保存、异步关闭。alpha.5 接通识别命令、手动选区/鸟种编辑/撤销、导出和冲突等对话框，并用真实模型与生成文件验证。随后进行逐项功能对照、Linux/macOS 原生与桌面验收和发布准备。新宿主不引用 WPF，不复制识别或导出业务规则。
5. **发布**：win-x64、osx-arm64、linux-x64 实机验收，osx-x64 根据设备追加；分别产出 Windows 目录包、macOS .app 和 Ubuntu 包，核查依赖、性能、签名、公证与安装体验，再决定合并稳定分支。

## 当前目录和职责

| 项目/目录 | 职责 |
| --- | --- |
| `src/BirdPhotoSelector.Foundation` | 无 UI 的宿主路径语义，区分文件路径、格式和鸟种身份 |
| `src/BirdPhotoSelector.Imaging` | 不可变帧、RGB24/方向/统一缩放、格式和解码契约、缓存调度 |
| `src/BirdPhotoSelector.Imaging.Native` | LibRaw UTF-8 ABI 3，原生像素短期租约、安全释放和共享串行门 |
| `src/BirdPhotoSelector.Imaging.Portable` | Skia JPEG 解码、MetadataExtractor EXIF、真实 RAW 委托 |
| `src/BirdPhotoSelector.Imaging.Windows` | 冻结 WPF 帧/转换；保留 WIC 对照后端及薄 RAW 显示适配 |
| `src/BirdPhotoSelector.Domain` | 照片、原件、组、评级、框、鸟种证据、几何参照及不变量 |
| `src/BirdPhotoSelector.Recognition` | 只接受已解码图像的 YOLO/BioCLIP 推理、固定预处理和候选评分 |
| `src/BirdPhotoSelector.Application` | 导入、分组、识别、人工编辑/撤销、工程/自动保存、导出与会话 |
| `src/BirdPhotoSelector.Platform` | 原生句柄文件身份、Windows/GIO/Foundation 回收站策略 |
| `src/BirdPhotoSelector.Catalog` | 鸟种名称、固定拼音索引、OpenCC 单字简繁搜索折叠 |
| `native/` | UTF-8 C ABI、LibRaw/系统桥接、CMake 和固定依赖清单 |
| `src/BirdPhotoSelector.Desktop` | Avalonia 独立入口、协调器、双预览绘制、主题、文件选择与对话框 |
| `Infrastructure/DesktopComposition.cs` | 当前宿主唯一默认组装点，选择 Portable 图像和 SystemFileOperations |
| `Infrastructure/Wpf/` | 窗口预览缓存、冻结位图转换等显示边界 |
| `Tests/Imaging.Core`、`Application.Core`、`PortableAdapters` | 普通 net10.0 检查，不引用或启动 WPF |

各项目暂保留现有公共命名空间，避免迁移同时改变 JSON 和绑定字段。工程仍写 v3，继续读取 v1/v2/v3；像素、缓存和原生句柄不序列化。

## 业务与生命周期

`ApplicationServices` 显式接收图像、文件系统和可替换模型工厂，组装导入、工程、导出与自动保存，并创建分组/识别操作范围；业务中没有宿主静态默认后端。

- 导入、工程恢复、分组、识别使用同一个 `IPhotoImageDecoder`；取消令牌可进入缩略图读取。
- `IBirdDetector` / `IBirdSpeciesRecognizer` 只接受图像。完整识别和单张重建拥有操作内模型，结束时释放；已有框批次明确区分借用与拥有，第二模型创建失败也释放第一模型。
- `PhotoSelectionSession` 维护选择、跨组导航、评级、人工鸟种、清空、撤销和最近选择。组/照片选择成组通知，避免列表刷新时短暂空选择。
- WPF `MainViewModel` 只保留按键映射；窗口负责显示、鼠标、异步调度和弹窗。后台先快照数据，返回独立提案，再由会话线程显式应用。
- 单张重建仍核对状态与 SHA-256；人工保护、清空后重识别及撤销语义保持不变。
- 导出冲突通过纯数据回调交给宿主。业务保留 SHA-256、覆盖/重命名、原件/硬链接保护、校验副本和全部目标完成后才能删除的屏障。

## 图像与内存约定

`ImageFrame` 表示已应用 EXIF/RAW 方向的不可变图像，左上角为原点，尺寸/裁切用像素，鸟框用 0..1 归一化坐标。实现不得保留输入文件句柄或暴露可写缓冲；`CopyRgb24` 返回紧密排列、R/G/B 顺序的独立副本。

- JPEG 使用 SkiaSharp **3.119.4** 解码到 sRGB、MetadataExtractor **2.9.3** 读取 EXIF。验证真正 JPEG 内容，支持 EXIF 1..8 的旋转和镜像；返回前释放 Skia 对象及文件流。
- 可缩小 JPEG 先用原生缩小解码，再精确采样。`maxWidth=0` 表示全分辨率；JPG 保留按编码宽度缩小后定向、RAW 按定向宽度缩小的既有约定。
- 所有 `ImageFrame.Resize` 固定 **BilinearPixelCenter-v1**：像素中心对齐、边界钳制、双线性逐通道采样、round-to-even，输出指定尺寸。WPF/托管后端共用实现，检测和背景分组不再采用不同缩放规则。
- BioCLIP 的 **SquareContext20-v1**、抗混叠双三次、逐遍取整和 OpenCLIP 归一化独立固定，不经过上面的 Resize；Pillow 金样和真实模型数值对照保持通过。
- 拍摄参数沿用原展示字段和文本，曝光补偿 `0 EV` 与缺失 `—` 分开；相机拍摄时间不推断时区。
- 托管解码接管新分配的私有 RGB 数组，不再次复制；WPF 全图在入缓存前转为冻结位图，随后缓存命中到显示直接共享，避免长期同时保留 RGB 和 WPF 两份整图。

`PreviewImageCache` 保持前台优先、共享解码、JPG 前后各两张、RAW 仅前台、邻居保护、LRU 淘汰、文件版本失效及独立等待者取消。窗口预算 512 MiB，核心默认 256 MiB；超大前台可显示但不保留，超大后台不预取。

预算只约束**保留帧**，至少按四字节/像素计费；解码工作内存、显示中引用、缩略图、模型/裁切/张量及 UI 资源另计。缓存淘汰只移除引用，不能释放仍被显示或算法共享的像素。

## 新桌面宿主（alpha.4）

`DesktopController` 只管理宿主异步流程、预览与工程路径；挑选状态来自 `PhotoSelectionSession`，导入、分组、JSON 和自动保存仍调用 Application。源模型与鸟种结果只有一份，打开原 v3 工程不会丢弃暂未迁移编辑界面的字段。

- `AvaloniaImageFrame` 在缓存入口转为单份不可变 BGRA 显示像素，两个预览共享 `IImage`；缩略图单独使用弱键显示缓存。保留帧按四字节/像素计费，临时 RGB/BGRA 转换及 UI 显示引用另计。
- `DetailViewTransform` 将适应画面到 200%、70% 鸟视图、鼠标锚点、中心钳制与白框归一化提取到 Imaging，可脱离任何 UI 检查。新画布只绘制共享图像，不在每次缩放时重解码。
- 当前选择与实际已显示照片分开记录。慢图期间保留旧帧、禁用交互；取消与递增版本校验确保迟到结果不覆盖新选择。
- 列表显式同步组/照片选择，忽略重建产生的空选择；保留 Ctrl/Shift 多选。滚轮只滚列表，每格约两张；方向键/WASD 连续跨组，Page Up/Down 单独切组，1..4 评级。
- 保存/打开共用写入门，自动保存复用五分钟服务与修订号；恢复文件打开为未命名会话。关闭事件先取消当前关闭并排队异步确认，任务取消回滚与保存结束后再最终关闭，避免在 Closing 调用栈内显示新窗口。
- Avalonia 使用 12.1.5 正式包，不引入商业控件；主窗口及数据模板启用带类型的编译绑定，属性重命名可在构建时发现。Linux 与图像后端统一使用支持 Fontconfig 的标准 Skia 原生包，避免同名库冲突。中文优先使用系统字体，Linux 主机需提供合适的中文字体；实际字体、输入法、文件选择器和缩放在目标桌面验收。

## RAW 与原生组件

`native/dependencies.json` 是各平台下载版本和 SHA-256 的公共清单，固定 LibRaw **0.22.3**，保留 CDDL 1.0 许可选项及上游版权。Windows 使用已验证 SDK；Unix 构建同版本、未修改的源码。

- C ABI 3 只传 UTF-8 路径、固定结构、指针和整数；Windows 桥接内部严格转 UTF-16，Unix 直接使用 UTF-8。头文件静态断言、托管结构大小及运行时版本检查拒绝混用旧桥接和错误 LibRaw。
- RAW 仍解包传感器数据、解拜耳、相机白平衡、sRGB；不拿内嵌 JPEG 冒充解码结果。所有操作共享一个串行门并保留取消回调。
- `RawPixelBuffer` 是独占短期租约，必须 Dispose；SafeHandle 提供异常路径兜底，释放后不能继续读指针。Portable 一次复制后立即释放；WPF 对照适配可在租约内直接复制到冻结位图。
- CMake 输出 `BirdPhotoSelector.Raw` 和独立 `BirdPhotoSelector.Platform.Native`。后者只链接系统库，文件身份检查不需要加载 LibRaw。
- 目标都是 64 位。原生库必须按目标系统与 CPU 架构重新构建，不能复制 Windows DLL 到 macOS/Linux。

## 文件路径、身份与回收站

`FilePaths` 在 Windows 使用不区分大小写比较，在 Unix 保留大小写并保留根目录。扩展名仍不区分大小写，鸟种学名和分类目录身份沿用各自规则，不能全局替换比较器。

`IFileIdentityProvider` 只授予已打开句柄的只读身份查询；`IExportFilePlatform` 再额外授予明确移除能力。原生桥接在 Windows 读取卷号/文件索引，在 Unix 读取本机 `fstat` 的设备号/inode，不在 C# 硬编码平台结构布局。未知身份保守禁止覆盖，不能用路径文本代替实体。

导入在原有 JPG+RAW 配对规则外检查物理别名，硬链接、macOS 大小写不敏感卷的别名不产生重复记录；Unix 真正不同的大小写文件不会仅因文本折叠而合并。跨机器目录重定位交互仍待设计，不能将宿主路径比较等同于工程可任意跨系统搬迁。

- Windows 继续使用 shell 回收站。
- Linux 使用 GIO；缺少对应能力或明确 `G_IO_ERROR_NOT_SUPPORTED` 才返回 Unsupported。
- macOS 使用 Foundation `trashItemAtURL`；本程序桥接缺失属于组件错误。
- **只有明确 Unsupported 才直接删除**；权限、磁盘满、组件或其他 IO 错误抛出并保留原件。此策略覆盖生成文件验证，真实 GIO/Foundation 与目标卷语义尚待实机验收。
- 工程保存继续使用临时文件和原子替换，独立检查验证新内容与恢复副本；目标系统的文件锁/卷/链接行为仍需补测。

## 名称检索

名称和搜索移至 Catalog。拼音索引仍固定嵌入，不加载 Python；简繁折叠改为固定 OpenCC **1.1.9** `TSCharacters.txt` 单字表，按 Unicode 标量读取，保留已有罕见鸟名字形别名，不调用 Windows NLS。

字表原文 SHA-256：`6b5a0a799bea2bb22c001f635eaa3fc2904310f0c08addbff275477a80ecf09a`。Git 固定 LF；取首选转换仅生成搜索键，不修改显示名称、学名或识别身份。许可证与来源见 [第三方说明](THIRD_PARTY_NOTICES.md)。

## 构建和验证入口

Windows 需要固定的 .NET 10 SDK、Visual Studio C++ x64 构建工具和 CMake 组件；普通构建自动运行 `Build-RawDecoder.ps1`，只下载并校验固定 SDK，不更改系统安装。

```powershell
.\.tools\dotnet\dotnet.exe build BirdPhotoSelector.csproj -c Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Test.ps1 -IncludeModel
```

新跨平台入口（从工程根目录启动，不覆盖 `publish/`）：

```sh
# 已有受支持 Python 时，在项目内安装固定 SDK；Unix 安装入口要求 Python 3.12+
python scripts/Setup-Dotnet.py
python scripts/Run-Desktop.py
python scripts/Run-Desktop.py --build-only
python scripts/Run-Desktop.py --check
```

也可按 `global.json` 安装官方 SDK；Windows 下使用 `python`，Linux/macOS 通常使用 `python3`。启动入口支持传入 JSON 工程或本地照片路径。完整 WPF 对照入口仍为根目录项目，旧发布目录继续使用原发布版本。

Linux/macOS 后端开发需要 .NET 10 SDK、Python 3.9+、CMake、C++ 编译器、make 及适用的 LibRaw 编译依赖；macOS 需 Xcode 命令行工具。GIO 用于 Ubuntu 桌面回收站。以下入口不构建 WPF：

```sh
python3 scripts/Build-Native.py
python3 scripts/Test-Portable.py
# 也可一步编译原生并验证：
python3 scripts/Test-Portable.py --build-native
# 已有同版本 LibRaw 开发前缀时：
python3 scripts/Build-Native.py --libraw-prefix /path/to/libraw-0.22.3
```

工具缓存和输出位于 `.tools/`，不会安装到系统目录。Unix 默认校验源码包、拒绝越界和链接成员，再构建 LibRaw 与桥接；macOS 调整桥接对 LibRaw 的相对加载路径。其他库的完整依赖收集、签名、公证、发行目录结构属于发布阶段，不能把开发输出直接当作可分发安装包。

`Test.ps1` 先构建原生组件，再执行三套普通 net10.0 检查、新 Avalonia 无窗口真实渲染/输入检查及完整 Windows 回归。真实 RAW/BioCLIP 参考资源仍通过环境变量准备，验证入口不会自动下载模型。日志写入忽略的 `artifacts/`；正式发布仍用原 Windows 发布脚本，并保留新增依赖和许可文件检查。

## 识别编辑与导出（alpha.5）

- 协调器分为会话/预览、Recognition 和 Export 三个部分；窗口的识别及文件交互也各自独立，避免新窗口继续长成包含全部规则的大文件。新增类和方法继续说明生命周期、取消、坐标和所有权。
- 批量识别仍使用默认中国候选并由共享用例保护人工修改；全部结果删除后解除保护。单张重建成功才整体替换，正常零检出可以清空，失败/取消保持旧结果，成功记录一次撤销，不额外确认。
- 手动选区通过 Imaging 的 `TryMapRegion` 转换显示坐标，起点必须在可见照片内，结束点裁切到可见边界，拒绝小于 8 个显示像素或 4 个原图像素的框。换图、Esc、布局和任务开始会取消未提交手势。识别使用当前不可变显示帧，失败或取消保留可复核的框并支持撤销。
- 自动/人工结果共用删除和更换入口：删除鸟种同时删除框，更换后成为无框人工结果；全图框右键删除走同一入口。编号、排序、最近十种和撤销均复用领域/会话规则，不在 UI 再实现一份。
- 搜索窗口空输入显示最近十种，关键词使用 Catalog 的简繁/拼音/首字母检索，不加载大模型；输入框给足行高和垂直内边距。
- 导出选项默认关闭分类与原件移除。执行前固定原件计划，提醒整个列表的未标记照片，返回自动定位；分类模式自动检查未分类、失败选区及低置信度，继续允许按其余有效鸟种导出。
- 冲突弹窗异步回到 UI，关闭/取消返回取消，只有用户明确勾选才把选择传回本次导出。共享用例负责相同内容跳过、数字后缀递增、受保护原件禁止覆盖、全部目标校验后的移除屏障；宿主总是应用实际逐成员结果，取消不丢失已经完成的原件变化。
- 首次定位明确选择面积最大的鸟，不再依赖检测适配器返回顺序；已有有效选择保持不变。

## 当前验证记录（alpha.5）

- 新桌面 **80 项**检查通过（含真实模型）；覆盖主界面原有交互、手动框选/取消/撤销、人工保护、重新识别失败和零检出、搜索与近期记录、未标记返回定位、分类提醒、多鸟种复制、逐张同名询问、记住选择、生成文件删除及工程往返。
- 真实 YOLO/BioCLIP 已通过新宿主批量识别；真实 BioCLIP 也已从 Avalonia 显示帧中裁切并识别手动框，验证显示像素接口与模型没有脱节。
- 完整 Windows 回归同时通过 **1,451 项**原界面/服务/模型、**33 项**图像、**50 项**业务、**176 项**实际后端检查，以及真实 Avalonia 窗口导入/预览/关闭探针；RAW 报告 **47 项**通过，源文件哈希保持一致。新宿主 Release 构建零编译警告、零错误。
- 当前日志为 `artifacts/regression-2.0-alpha5.log`、`desktop-2.0-alpha5-real.log`，RAW 与鸟种记录为 `artifacts/raw-2.0-alpha5/summary.json`、`artifacts/species-native-2.0-alpha5-validation.json`，布局截图在 `artifacts/desktop-2.0-alpha5/`。
- 当前尚未进行 macOS/Ubuntu 实机运行、系统文件行为和发行验收；不生成正式安装包，不覆盖原发布目录，也不合并 main。

## 上阶段验证记录（alpha.4）

- 所有项目统一 .NET 10；Windows 真实完整回归 **1,451 项**、图像核心 **33 项**、业务核心 **50 项**和实际后端 **176 项**通过。
- Avalonia 新界面 **38 项**检查通过：真实 Skia 像素、键盘、缩放/拖拽、列表滚动不切图、跨组选中、旧帧保留和迟到结果、分组/评级、JSON 往返、正常及最小窗口四条结果，以及未保存关闭的返回/放弃选择。
- 独立真实 Windows 窗口探针通过，最小化运行时可导入、显示实际 JPEG 并完成异步关闭；不只依靠无窗口测试证明入口可启动。
- 完整回归继续包含真实 RAW 47 项、YOLO/BioCLIP 数值与模型对照、原件保护、导出及 WPF 界面。日志为 `artifacts/regression-2.0-alpha4.log`、`desktop-native-2.0-alpha4.log`、`desktop-2.0-alpha4-launcher.log`，新界面图像为 `artifacts/desktop-2.0-alpha4/main-normal.png` 和 `main-minimum.png`。
- 当前实际平台仍只有 Windows；无窗口检查不是 macOS/Linux 桌面、文件选择器、中文输入法、系统回收站与原生库发布验收。该阶段尚未接入识别编辑与导出 UI；alpha.5 已接通，正式替代仍以三平台验收为前提。

## 上阶段验证记录（alpha.3，.NET 8）

- Windows 构建零编译警告、零错误；CMake 成功编译两个原生桥接。
- **33 项图像核心、50 项业务核心、176 项实际适配、1,451 项完整 Windows UI/服务/真实模型检查通过**。搜索与窗口目标检查另有 525 项通过，与完整检查存在交集，不叠加宣传。
- 实际适配覆盖独立 JPEG/EXIF 八方向、拍摄字段及 signed/zero/missing EV、独立采样金样、文件句柄释放、UTF-8 中文/é/emoji RAW 路径、真实像素、原件哈希、硬链接身份与导入去重、回收站错误保留及原子替换副本。
- 完整回归包含 47 项公开 RAW 检查、真实 YOLO/BioCLIP、固定预处理数值对照、工程/导出/来源保护、正常与最小窗口和关闭行为。相关日志：`artifacts/regression-2.0-alpha3.log`、`portable-adapters-2.0-alpha3.log`，原生报告位于 `artifacts/raw-2.0-alpha3/` 与 `artifacts/species-native-2.0-alpha3-validation.json`。
- **实际执行平台只有 Windows**；源码包安全检查和脚本语法检查不替代 Linux/macOS 的编译、运行、文件行为与发布验收。当前桌面界面仍是 WPF。

## 注释和维护约定

新增和迁移类说明职责、依赖方向及生命周期；函数说明输入/输出、坐标/单位、状态变化、取消与所有权。原生调用、文件替换、并发、采样和取整解释约束与原因，中文注释不机械重复赋值。

图像公开 API 启用 XML 文档，缺失文档与编译警告作为错误；新增项目也将编译警告视为错误。后续功能通过接口和宿主组装扩展，不往业务层添加平台判断，不在不同桌面 UI 复制人工编辑或导出规则。

## 参考依据

- [Avalonia 平台与迁移](https://docs.avaloniaui.net/docs/supported-platforms)、[WPF 迁移](https://docs.avaloniaui.net/docs/migration/wpf/)
- [.NET 支持周期](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
- [SkiaSharp](https://www.nuget.org/packages/SkiaSharp/3.119.4)、[MetadataExtractor](https://github.com/drewnoakes/metadata-extractor-dotnet/tree/2.9.3)
- [OpenCC 固定字表](https://github.com/BYVoid/OpenCC/blob/ver.1.1.9/data/dictionary/TSCharacters.txt)
- [GIO trash](https://docs.gtk.org/gio/method.File.trash.html)、[GIO 错误枚举](https://docs.gtk.org/gio/error.IOErrorEnum.html)
- [Foundation trash](https://developer.apple.com/documentation/foundation/filemanager/trashitem(at:resultingitemurl:))
