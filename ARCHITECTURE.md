# v2.0 跨平台架构与迁移计划

本文件是持续维护的工程说明，提交到 `dev`；`docs/` 继续只存放本地临时输出，不进入 Git。
当前里程碑：**2.0.0-alpha.2，领域、业务用例与识别层拆分**。现有 WPF 程序作为迁移期可运行的功能对照，尚未提供 macOS/Ubuntu 桌面程序。

## 版本与分支

- Gitea `main` 保持稳定版，不为本次迁移执行合并、变基、重置或推送。
- `dev` 持续接收 v2.0 的小步提交，每个阶段保留可编译和可验证状态。
- `2.0.0-alpha.N` 表示迁移中；跨平台功能与原版本对齐后进入 beta，三平台验收后再考虑正式版。
- 当前安装包、旧发布 ZIP 和用户原照片不由重构修改。是否合并稳定分支留到发布时处理。

## 总体方向

继续使用 C#，最终桌面界面采用 Avalonia。按依赖方向拆分，不让领域与业务代码依赖 UI、WPF 位图或 Win32。
迁移第一阶段暂用已有 .NET 8 工具链，便于原版本回归；正式 v2.0 前统一升级到 .NET 10 LTS，并在实际目标系统上验证。
.NET 8 支持将于 2026-11-10 结束，因此运行时升级安排在界面发布前，不能把此迁移快照作为长期正式发行基础。

目标依赖关系（部分项目在后续阶段创建）：

```text
Desktop.Avalonia ──> Application ──> Domain
       │                   │          │
       ├──> 系统适配器      ├──> Imaging（图像契约/帧/有界缓存）
       │                   └──> Recognition（定位/鸟种/候选表）
       └──> 图像后端实现 ─────────> Imaging
```

| 层 | 职责 | 禁止耦合 |
| --- | --- | --- |
| Domain | 照片、原件关联、评级、鸟种/选区、视图数据、不变量 | 窗口、位图、文件选择器、回收站 |
| Application | 导入/分组/识别/导出/撤销/工程保存的用例和进度 | 对话框、UI 调度器、直接 Win32 调用 |
| Imaging | 方向统一的图像契约、像素读取、解码接口、预加载预算与调度 | WPF/Avalonia、ONNX、具体文件格式解码库 |
| Recognition | YOLO/BioCLIP 输入、推理、结果和候选评分 | 控件、消息框、直接修改 UI 集合 |
| Platform | 路径身份、回收站、文件锁/原子替换、应用目录和原生组件 | 评级、识别规则、窗口逻辑 |
| Desktop | 显示、交互、快捷键、依赖组装和对话框 | 复制一套业务规则 |

## 分阶段实施和验收

1. **图像层（alpha.1 已完成）**：独立 `net8.0` 图像契约与缓存；隔离 WIC/LibRaw Windows 后端；缩略图和 AI 改用图像帧；保留既有解码与采样行为。核心可脱离 Windows Desktop SDK 编译，像素/缓存独立验证通过，原 Windows 功能与真实模型/RAW 回归通过。
2. **领域与用例（本次 alpha.2）**：拆出 Domain/Application/Recognition 项目；将当前宿主默认依赖改为统一组装；工程读取、缩略图、导出冲突等通过接口注入；逐步把 MainWindow 的业务操作转成用例和视图模型命令。保留 v1/v2/v3 JSON 读取和人工保护/撤销语义。
3. **跨平台后端与系统适配**：评估并固定跨平台 JPG/EXIF 后端及许可证；统一采样金样；LibRaw 桥接改为 UTF-8 C ABI，使用 CMake 分平台构建；路径比较和文件身份/回收站分离。先验证 win-x64、osx-arm64、linux-x64；osx-x64 根据设备覆盖追加。
4. **Avalonia 界面**：迁移双预览、滚轮缩放/边界、手工选区、快捷键、结果列表、弹窗和无闪烁切图；使用新宿主组装业务和后端，不把 WPF 适配带进跨平台程序集。
5. **发布**：统一 .NET 10 LTS，三平台实际测试 JPG/RAW/ONNX/文件操作/工程恢复；分别产出 Windows 目录包、macOS .app 和 Ubuntu 包。签名、公证、CPU/GPU 性能与安装依赖分别验证；正式版再合并稳定分支。

每阶段优先保持已有功能。阶段间允许很薄的 WPF 兼容入口，但必须集中在 `Infrastructure/Wpf/` 或 Windows 图像项目，并标明移除时机。

## 当前落地的目录

```text
src/
  BirdPhotoSelector.Imaging/           # net8.0：不可变图像、解码契约、拍摄参数、有界缓存
  BirdPhotoSelector.Imaging.Windows/   # net8.0-windows：WIC 与当前 LibRaw 桥接、WPF 图源转换
  BirdPhotoSelector.Domain/            # net8.0：照片/原件/组/鸟框/鸟种/几何参照/最近选择
  BirdPhotoSelector.Recognition/       # net8.0：只接受已解码图像的推理模型
    Contracts/                        # IBirdDetector、IBirdSpeciesRecognizer
    Detection/                        # YOLO 定位
    Species/                          # BioCLIP、候选向量、置信度、固定预处理
  BirdPhotoSelector.Application/       # net8.0：明确注入后端的业务用例
    ApplicationServices.cs            # 统一依赖图和延迟模型工厂
    Contracts/                        # 文件实体身份与回收站接口
    Session/                          # 可复用挑选会话与照片组展示数据
    Import/                           # 导入及原件关联提案
    Projects/                         # v1/v2/v3 JSON 工程与自动保存
    Grouping/                         # 背景特征与顺序分组
    Recognition/                      # 定位+识别、已有框识别、单张重建事务
    Editing/                          # 人工鸟种/选区编辑与撤销
    Export/                           # 导出计划、同名冲突、分类、校验和移除
Infrastructure/
  DesktopComposition.cs               # Windows 宿主唯一的默认后端选择点
  Windows/WindowsFileOperations.cs     # Win32 实体身份与 Windows shell 回收站
  Wpf/                                # 预览缓存/位图推理的薄显示适配
ViewModels/MainViewModel.cs            # 仅保留 WPF 按键映射，业务状态来自 PhotoSelectionSession
Services/                             # 暂保留名称文件读取与含 Windows NLS 的离线搜索
Tests/
  Imaging.Core/                       # 无 WPF、模型或原生 RAW 的图像检查
  Application.Core/                   # 无 WPF、模型文件的业务与依赖边界检查
  TestServices.cs                     # Windows 回归的显式组装；故障委托不进入生产用例
```

领域原来的大文件已拆成照片、鸟框、自动识别证据、单条结果等小文件。各项目暂保留 `BirdPhotoSelector` 公共命名空间，工程字段和公开数据含义保持一致，避免迁移同时引入 JSON 或绑定变更。
`PhotoItem.Thumbnail` 继续使用 `ImageFrame`，不保存 `BitmapSource`；WPF 缓存入口只代理一份核心缓存，不重复保留整图。

## 业务依赖与生命周期

`ApplicationServices` 接收明确的图像后端、文件平台和可替换模型工厂，组装导入、工程、导出与自动保存，并为分组/识别创建操作范围。
Windows 宿主在 `DesktopComposition` 中选择 WIC/LibRaw 与系统文件实现，`MainWindow` 可直接接收完整依赖图。业务项目没有宿主静态默认值，也不能反向读取 Windows 图像实现。

- 导入、工程恢复、分组和识别使用同一个 `IPhotoImageDecoder`；工程取消令牌可进入缩略图读取。
- `IBirdDetector` / `IBirdSpeciesRecognizer` 只接受已解码图像。`BirdSpeciesRecognitionService` 不再读取照片或管理列表，原有逐框批次移至 `SpeciesRecognitionBatchService`。
- 模型延迟创建，完整识别/单张重建用例接管各自模型并在结束时释放；创建第二个模型失败时释放第一个。已有框批次默认借用外部模型，可显式取得所有权，避免重复释放。
- `PhotoSelectionSession` 统一选择、连续跨组导航、评级、人工鸟种、清空、撤销和最近选择。组/照片选择成组通知，避免列表刷新时出现瞬间空选择。照片移出列表后历史失效规则也在会话内维护。
- 现有 WPF `MainViewModel` 是很薄的过渡派生类，只把平台按键映射为会话操作。窗口保留图像显示、鼠标选区、异步调度与弹窗，人工编辑规则已委托会话。
- 所有后台流程先快照 UI 所属数据，返回独立提案，再由会话线程显式应用；单张重建仍校验原状态与源文件 SHA-256。
- 导出冲突由纯数据回调交给宿主，业务层不打开对话框；复制、校验、硬链接/来源保护和“所有目标完成后才删除原件”的屏障保持原规则。

`IExportFilePlatform` 将文件实体身份和原件移除隔离。Windows 实现仍使用卷号+完整文件索引及 shell 回收站；无法确认实体身份时返回未知，覆盖保护保守关闭，不能退化为只比路径字符串。
当前的路径大小写、文件锁/原子替换语义、简繁 NLS 和原生 RAW 仍需第三阶段真正的系统适配。独立程序集可编译和检查通过，不代表 Mac/Ubuntu 的整套文件行为已经验收。

## 图像数据约定

- `ImageFrame` 永远表示已经应用 EXIF/RAW 方向的显示图像；左上角为原点，像素坐标用于裁切，鸟框仍用 0..1 的归一化坐标。
- 图像必须不可变、可跨线程读取、不保留输入文件句柄。帧由共享托管引用决定生命周期，淘汰缓存不能直接释放仍在显示的像素；后端原生像素所有者应使用 SafeHandle 等安全释放机制。`CopyRgb24` 返回独立副本，固定 RGB 顺序、紧密行排列；不得把 BGR 或平台像素布局泄露给算法。
- WPF 实现直接包装冻结的位图，完整预览和缓存不再额外分配一份整图 RGB。只有算法明确请求像素时才复制，BioCLIP 逐鸟只复制对应裁切区域。
- `Resize` 是后端采样入口，调用者读取返回帧的实际尺寸。Windows 第一阶段保留 WIC；托管实现明确使用像素中心双线性采样。它们不承诺逐字节相同，跨平台后端定型前必须建立分组/检测阈值和张量对照。
- BioCLIP 的 SquareContext20-v1、双三次抗混叠、逐遍取整、OpenCLIP 归一化保持固定算法，独立 Pillow 金样不能随平台更改。
- `maxWidth=0` 是全分辨率；其他值沿用原有行为：JPG 按编码宽度缩小后应用方向，RAW 按已定向的宽度缩小；JPG 竖图应用方向后不承诺显示宽度小于该值。
- 元数据仍保留现有展示文本和 JSON 字段；相机时间不推断时区，曝光补偿 `0 EV` 与缺失分开。

## 缓存与内存

`PreviewImageCache` 保持前台优先、相同文件共享解码、JPG 前后各两张、RAW 仅前台、邻居按距离保护、LRU 淘汰、文件版本失效与独立等待者取消。
当前窗口继续显式设置 512 MiB，核心默认 256 MiB；超过预算的前台照片允许显示但不保留在缓存，超大后台来源不预取。

预算是**保留帧预算**，不是整个进程硬上限。每帧至少按四字节/像素计费；RAW 解码器工作内存、当前显示帧、AI 模型、算法裁切/张量、缩略图和 UI 资源另计。
Windows 帧从缓存到显示零复制；未来后端不得为了绑定显示为每张缓存图长期附加另一份未计费整图。

所有进入后台的来源都是不可变 `PreviewSource` 快照；后台不读取可变 `PhotoItem` 或操作 UI 集合。昂贵来源在最后一个等待者取消后取消解码，其他共享等待者不受影响。
核心路径键在 Windows 沿用不区分大小写，在 Unix 保留大小写；现有导入/工程/导出中的 Windows 路径规则尚未整体迁移，不能据此宣称整个应用已支持 Unix。

## 系统适配的后续边界

- `GetFileInformationByHandle` 与回收站已隔离到 Windows 实现；后续补充其他平台文件身份/回收站，并验证文件锁及 `File.Replace` 的平台语义。必须保留同名校验、SHA-256、硬链接保护及复制后验证再删除的安全语义。
- Linux 文件路径区分大小写；macOS 可能使用任一种卷。路径身份、扩展名、鸟种学名比较必须分别处理，不能全局替换 `OrdinalIgnoreCase`。
- 工程跨机器移动需要相对路径/目录重定位策略，旧绝对路径和缺失来源提示仍须兼容。
- 简繁检索的 Windows NLS 调用需替换，固定拼音索引与现有异体字规则保留；中文排序需要三平台对照。
- LibRaw 当前仍是 Windows x64 UTF-16 ABI；新增接口不会使它自动成为 macOS/Linux 原生库。UTF-8 桥接、取消和资源释放要独立验收。

## 注释和维护约定

新增或迁移的类说明职责、依赖方向和生命周期；公开函数说明输入/输出、坐标/单位、取消和资源所有权。
并发、原生调用、文件替换、采样/取整等内部函数解释约束与原因。注释使用中文、保留必要 API 名称，不复述每条简单赋值。
第一阶段图像层公开 API 启用 XML 文档生成，核心与 Windows 图像项目将缺失公开文档和编译警告作为错误。第二阶段迁移的领域/业务/识别类和函数补充中文契约、状态变更及资源说明，三个新项目也将编译警告作为错误。尚未迁移的窗口与平台文件随对应阶段逐个完善。

新增图像后端只需实现 `IPhotoImageDecoder` 与 `ImageFrame`，随后通过用例构造参数传入；不得在核心中增加针对 WPF/Avalonia 的条件编译。
UI 图源的转换只放在显示边界，工程序列化不保存像素、原生句柄或缓存状态。

## 开发与验证

独立图像层（任意有 .NET 8 SDK 的目标系统）：

```sh
dotnet run --project Tests/Imaging.Core/BirdPhotoSelector.Imaging.Checks.csproj -c Release
dotnet run --project Tests/Application.Core/BirdPhotoSelector.Application.Checks.csproj -c Release
```

`scripts/Test.ps1` 会先执行上述两组核心检查，再执行现有 Windows 宿主回归：

```powershell
.\.tools\dotnet\dotnet.exe build BirdPhotoSelector.csproj -c Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Test.ps1 -IncludeModel
```

本地固定模型、公开 RAW 样本和预处理参考资源按原验证流程准备；模型/真实 RAW 验证需要显式环境变量，普通核心检查不会下载它们。输出继续写入忽略的 `artifacts/`，开发临时说明写入忽略的 `docs/`。
Windows 的完整界面/模型验证只能证明当前平台；Mac/Ubuntu 上的真实运行和发布验收列为后续阶段，不能用 Windows 编译通过代替。

## 本次验证记录（alpha.2）

- 独立图像检查 **33 项**、独立业务检查 **50 项**、完整 Windows UI/服务/模型检查 **1,451 项**全部通过。
- 独立业务检查确认三个新程序集均无 WPF/Windows 宿主引用、无直接系统 P/Invoke；领域不反向依赖业务或模型，模型不反向依赖业务。
- 业务检查覆盖纯托管图像后端、选择状态、人工编辑/撤销、JSON/自动保存、取消进入缩略图读取、模型实例所有权、来源变化拒绝提案、未知文件身份禁止覆盖和校验后移除。
- 完整回归包含公开 RAW 样本的 **47 项**检查、真实 BioCLIP 数值对照、同名导出和来源保护、最小/正常窗口及关闭行为；原件字节校验通过。
- 本次实际执行平台是 Windows；两个独立验证项目均以普通 `net8.0` 为目标，尚未在 Mac/Ubuntu 主机执行。
- 构建零警告、零错误；版本为 `2.0.0-alpha.2`，Windows 清单数值身份仍为 `2.0.0.0`，构建/打包清单要求三个新增业务程序集。
- 本阶段不生成正式安装包，不覆盖已有发布目录，`main` 保留原稳定提交。

## 参考依据

- [Avalonia 支持平台](https://docs.avaloniaui.net/docs/supported-platforms)
- [WPF 迁移到 Avalonia](https://docs.avaloniaui.net/docs/migration/wpf/)
- [.NET 官方支持周期](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
