# 羽选 BirdPhotoSelector

本地鸟类照片筛选应用，当前开发版 **2.0.0-alpha.9**：新增不按评级筛选的“导出全部”，修复删除 JPG 后剩余 RAW 缩略图丢失。`dev` 为 Avalonia 跨平台工程，`main` 为稳定分支；旧 WPF 版本的历史源码保存在 `dev_win` 分支。

## 使用测试包

完整测试包位于 `publish/`（不进入 Git），每套包含 .NET、YOLO/BioCLIP 2、中国候选、原生解码器和许可文件，不需要安装 Python、.NET 或 Visual Studio。

| 目标 | 包 | 启动 |
| --- | --- | --- |
| Windows x64 | `BirdPhotoSelector-2.0.0-alpha.9-win-x64.zip` | 解压后运行 `BirdPhotoSelector.exe` |
| Apple 芯片 Mac | `BirdPhotoSelector-2.0.0-alpha.9-macos-arm64.tar.gz` | 解压后打开 `BirdPhotoSelector.app` |
| Ubuntu x64 | `BirdPhotoSelector-2.0.0-alpha.9-linux-x64.tar.gz` | 解压后运行 `./start.sh` |

Mac 目标为 macOS 14+，Ubuntu 目标为 22.04/24.04 图形桌面。Windows 有本机运行检查；Mac/Ubuntu 在 Windows 交叉构建并核对原生架构，仍需目标机器测试，不能把打包成功当作实机验收。本测试版未做 Apple 开发者证书签名与公证。

各包附 `测试说明.txt`、`release-manifest.json`；归档旁有 SHA-256。原生库、模型、每个文件和归档成员均进行校验。测试电脑可直接执行随包程序的 `--self-test 报告路径.json` 和 `--smoke-test 报告路径.json`，前者测试模型/运行库，后者最小化启动真实界面并导入预览后退出。

## 功能

- 直接选择 JPG/RAW，关联同目录同名原件；追加、去重、多选、移除和清空列表均不直接删除原件。
- 照片组/组内列表、连续导航、四色评级、全图与细节双预览、前后预加载及 512 MiB 保留帧缓存。
- 可调背景分组；中国候选鸟种识别、多鸟选区、70% 鸟视图、手动框选及鸟种添加/更换/删除/撤销。
- 中文、拼音/首字母、学名和简繁搜索，最近十种随工程保存。
- 按评级或导出全部，并选择 JPG/RAW 范围，可选按鸟种分类、多鸟种多份；相同内容跳过、同名不同内容询问覆盖/重命名。
- 原件移除默认关闭；确认全部目标副本校验完成后才回收原件。删除舍弃同样先确认。
- JSON 工程、缺失文件报告、五分钟自动保存与恢复。

具体操作和平台测试事项见 [使用说明](使用说明.md)。架构、内存边界及剩余验收见 [架构说明](ARCHITECTURE.md)。

## 源码开发

需要 Python 3.11+、固定 .NET 10 SDK；Windows 本机原生编译另需 Visual Studio C++ x64/CMake，Unix 本机原生编译需 C++、CMake、make 和开发工具。工程内工具均位于忽略的 `.tools/`，不修改系统 SDK。

```text
python scripts/Setup-Dotnet.py
python scripts/Run-Desktop.py
python scripts/Test.py --include-model --native-window
python scripts/Build-Release.py
```

Windows 已有项目内 Python 时，可用 `.\.tools\species-python\python.exe` 替代 `python`。只发布一个平台时使用 `python scripts/Build-Release.py --runtime win-x64`（另可选 `osx-arm64`、`linux-x64`）。默认生成三套完整包，存在同名输出时停止，不自动覆盖。

首次模型准备可在 Windows 运行 `scripts/Setup-Model.ps1`、`scripts/Setup-SpeciesModel.ps1`；已有完整 `models/` 可跨平台复制，发布时强制核对指纹。重建 BioCLIP 资源的固定 Python 依赖及过程在准备脚本中；最终用户无需这些构建工具。

Windows 交叉构建 Unix 原生组件使用项目内固定 Zig 0.17.1 和已校验 LibRaw 0.22.3 源码，首次需要联网。普通运行离线，不上传照片。依赖、模型及中国候选的不同许可条件见 [第三方说明](THIRD_PARTY_NOTICES.md)。

`docs/` 只作本地临时输出，不提交、不参与构建或打包。`.tools/`、`artifacts/`、`bin/obj`、`publish/` 都不上传 Git。

## 正式发布与分支

`dev_win` 保存旧 WPF 版本的历史源码；`dev` 开发跨平台版本，`main` 保存验收后的稳定版本。分支名不决定版本号：开发测试使用 `2.0.0-alpha.n`，接近发布可使用 `2.0.0-rc.n`，正式包使用 `2.0.0`。打 Git 标签不会自动修改程序中的版本号。

建议按以下顺序发布：

1. 在 `dev` 完成功能和目标平台测试，确认准备正式发布。
2. 在 `dev` 将 `Directory.Build.props` 的 `Version` 改为 `2.0.0`，同步帮助窗口、README 和版本说明，提交并完成回归。
3. 合并 `dev` 到最新的 `main`，解决可能的冲突并复查最终代码；从干净的 `main` 执行 `python scripts/Build-Release.py`。
4. 验收这次正式构建的安装目录/压缩包，核对 `release-manifest.json` 的提交、版本和 `SourceDirty: false`。不能只将 alpha 包重命名为正式包。
5. 在这次已构建和验收的 `main` 提交上创建附注标签并推送；在 Gitea/GitHub 创建对应 Release，上传三平台压缩包及 `.sha256`。
6. 将正式发布提交同步回 `dev`，再开始下一轮版本，例如修复版 `2.0.1-alpha.1` 或功能版 `2.1.0-alpha.1`。

第 5 步在确认当前就是发布提交后执行：

```text
git tag -a v2.0.0 -m "Release v2.0.0"
git push origin main
git push origin v2.0.0
```

标签指向提交，本身不属于分支。先在 dev 打标签在技术上可行，但合并可能产生不同提交；统一在最终 main 提交打正式标签，便于代码、构建清单、下载包一一对应。正式版标签发布后保留不动，后续修复使用新版本号。参见 [语义化版本](https://semver.org/lang/zh-CN/) 和 [Git 标签说明](https://git-scm.com/book/zh/v2/Git-基础-打标签)。

GitHub：[yyx263/BirdPhotoSelector](https://github.com/yyx263/BirdPhotoSelector)。

对外 Mac 包使用 `macos-arm64`；构建参数和清单内部仍使用 .NET 标准 `osx-arm64`，这也是 Apple 芯片 Mac 的 RID，并非 Intel 专用。参见 [微软 RID 说明](https://learn.microsoft.com/dotnet/core/rid-catalog)。
