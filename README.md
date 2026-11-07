# 羽选 BirdPhotoSelector

本地鸟类照片筛选应用，当前开发版 **2.0.0-alpha.8**：修复大图显存缓存抖动并减少 JPG 显示转换，右上角批量按钮改为“全部识别”。仅保留 Avalonia 跨平台工程；旧 WPF 宿主已从 `dev` 移除，历史源码可在 Git 的 alpha.5 及更早提交查看。稳定分支 `main` 不变。

## 使用测试包

完整测试包位于 `publish/`（不进入 Git），每套包含 .NET、YOLO/BioCLIP 2、中国候选、原生解码器和许可文件，不需要安装 Python、.NET 或 Visual Studio。

| 目标 | 包 | 启动 |
| --- | --- | --- |
| Windows x64 | `BirdPhotoSelector-2.0.0-alpha.8-win-x64.zip` | 解压后运行 `BirdPhotoSelector.exe` |
| Apple 芯片 Mac | `BirdPhotoSelector-2.0.0-alpha.8-macos-arm64.tar.gz` | 解压后打开 `BirdPhotoSelector.app` |
| Ubuntu x64 | `BirdPhotoSelector-2.0.0-alpha.8-linux-x64.tar.gz` | 解压后运行 `./start.sh` |

Mac 目标为 macOS 14+，Ubuntu 目标为 22.04/24.04 图形桌面。Windows 有本机运行检查；Mac/Ubuntu 在 Windows 交叉构建并核对原生架构，仍需目标机器测试，不能把打包成功当作实机验收。本测试版未做 Apple 开发者证书签名与公证。

各包附 `测试说明.txt`、`release-manifest.json`；归档旁有 SHA-256。原生库、模型、每个文件和归档成员均进行校验。测试电脑可直接执行随包程序的 `--self-test 报告路径.json` 和 `--smoke-test 报告路径.json`，前者测试模型/运行库，后者最小化启动真实界面并导入预览后退出。

## 功能

- 直接选择 JPG/RAW，关联同目录同名原件；追加、去重、多选、移除和清空列表均不直接删除原件。
- 照片组/组内列表、连续导航、四色评级、全图与细节双预览、前后预加载及 512 MiB 保留帧缓存。
- 可调背景分组；中国候选鸟种识别、多鸟选区、70% 鸟视图、手动框选及鸟种添加/更换/删除/撤销。
- 中文、拼音/首字母、学名和简繁搜索，最近十种随工程保存。
- 按评级及 JPG/RAW 范围导出，可选按鸟种分类、多鸟种多份；相同内容跳过、同名不同内容询问覆盖/重命名。
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

GitHub：[yyx263/BirdPhotoSelector](https://github.com/yyx263/BirdPhotoSelector)。

对外 Mac 包使用 `macos-arm64`；构建参数和清单内部仍使用 .NET 标准 `osx-arm64`，这也是 Apple 芯片 Mac 的 RID，并非 Intel 专用。参见 [微软 RID 说明](https://learn.microsoft.com/dotnet/core/rid-catalog)。
