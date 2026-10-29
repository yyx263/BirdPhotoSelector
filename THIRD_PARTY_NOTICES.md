# 第三方组件和模型说明

本应用的第三方代码、运行库与模型各自受其原始许可证约束。保留发布目录中的许可和第三方说明文件。

## LibRaw 0.22.3 RAW 解码器

- 上游：[LibRaw](https://www.libraw.org/)，[许可说明](https://www.libraw.org/about#license)。上游允许选择 LGPL 2.1 或 CDDL 1.0；本项目使用 **CDDL 1.0** 选项，未修改 LibRaw 源码或 DLL。
- 固定 Win64 SDK（包含原始源码及二进制）：[LibRaw-0.22.3-Win64.zip](https://www.libraw.org/data/LibRaw-0.22.3-Win64.zip)。相同版本的对应源码可从此固定地址取得。
- SDK SHA-256：`4e2213a0b10e32199a30b9114a61376cdb40f94df861262b4a6dca659701276b`，是从官方 HTTPS 下载记录的指纹，不是上游签名。
- 运行文件 `libraw.dll` SHA-256：`2c4df36d9011267fd3ffd5c23e80d69d5f42c3d503f03ac02448f5c8a7fe0fa2`。
- 原始版权与许可副本：`licenses/LibRaw-COPYRIGHT.txt`、`licenses/LibRaw-CDDL-1.0.txt`、`licenses/LibRaw-LGPL-2.1.txt`。保留 LibRaw 所包含的 dcraw、DCB/FBDD、X3F、Adobe DNG 相关署名；DCB/X3F 的 BSD 条款原文另保存在 `licenses/LibRaw-ComponentNotices.txt`。
- 本项目的 ABI 桥接源码位于 `native/raw-decoder.cpp`，构建脚本为 `scripts/Build-RawDecoder.ps1`。桥接只调用官方库，不复制或改变库内部算法。

显示流程使用传感器 RAW 解码、适用的解拜耳、相机白平衡及 sRGB 输出；不将内嵌 JPEG 作为 RAW 解码结果。格式扩展名能被识别不代表所有机型或压缩模式都受支持。

原生 DLL 所需的 Microsoft C++ 运行库为 `msvcp140.dll`、`vcruntime140.dll`、`vcruntime140_1.dll`。构建时从已安装 Visual Studio 的 x64 CRT 可再分发目录复制到程序目录，不从系统目录任意收集 DLL；它们仍受 Microsoft 原始条款约束。分发规则与应用目录部署方式见 [Microsoft C++ 运行库分发文档](https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files)。这三个运行库不按本项目或 LibRaw 的开源许可重新授权。

## YOLOv10n ONNX 模型

- 模型文件：`models/yolov10n.onnx`
- 发布者：[ONNX Community / YOLOv10](https://huggingface.co/onnx-community/YOLOv10)
- 固定提交：[305deee9272dc7082fbc36e9f424a38d6ef23b8e](https://huggingface.co/onnx-community/YOLOv10/commit/305deee9272dc7082fbc36e9f424a38d6ef23b8e)
- 大小：9,386,116 bytes
- SHA-256：`a77dd863933f184a19e84361c64b788228a7c7dacc2c78939239a96ad3efca3b`
- 模型卡标注许可证：AGPL-3.0
- 原始实现与对应模型项目：[THU-MIG / yolov10](https://github.com/THU-MIG/yolov10)
- 上游许可：[GNU Affero General Public License v3](https://github.com/THU-MIG/yolov10/blob/main/LICENSE)
- 本地许可副本：`licenses/YOLOv10-AGPL-3.0.txt`

模型未经本项目训练或修改，应用只在本地执行推理。模型来源、版权和许可证不因转换为 ONNX 或单独放在文件夹中而改变。再次分发含该模型的程序时，应提供适用的许可文本和对应源码，满足上游许可条件。本说明不将模型重新许可为 MIT。

## BioCLIP 2 鸟种视觉模型

- 上游：[Imageomics / BioCLIP 2](https://huggingface.co/imageomics/bioclip-2)
- 固定提交：`2957b322090f9cb17ae72c71981c7218a28d81e0`
- 官方检查点：`open_clip_model.safetensors`，1,710,517,724 bytes。
- 官方检查点 SHA-256：`b7b2bf6fbc95799e42630e394cf95803892ab447c1a8ab629dbc82fbeaf7dfef`
- 运行文件：`models/bioclip2/vision.onnx`，由该检查点导出的 FP32 ViT-L/14 视觉编码器，没有重新训练。
- 上游许可证：MIT；原文位于 `models/bioclip2/licenses/BioCLIP2-MIT.txt`。

导出脚本、输入规格、裁剪策略、上游版本、资源 SHA-256 与数值对照信息随源码和 `models/bioclip2/metadata.json`、`asset-hashes.json` 保留。运行程序使用 ONNX Runtime，不需要 Python 或 PyTorch。

## 官方文本向量与中国鸟种候选资料

BioCLIP 2 文本向量和分类标签来自 [Imageomics / TreeOfLife-200M](https://huggingface.co/datasets/imageomics/TreeOfLife-200M)，固定提交 `5f2dc493b3dc0e544438a04038ab15faa646b749`。构建时校验官方完整向量和标签文件，再仅提取中国候选对应的向量；发布包不包含完整训练数据或训练照片。官方数据卡关于编译与元数据的 CC0 说明及原始图片各自的许可条件保存在 `models/bioclip2/licenses/TreeOfLife-source-card.txt`，CC0 原文位于同目录的 `TreeOfLife-CC0-1.0.txt`。

中国候选学名、中文名及分类关系来自 [Catalogue of Life China 2023](https://doi.org/10.15468/ae6ush)，使用固定的 v1.1 Darwin Core Archive，SHA-256 为 `9c17cdea98e210b2acc6ccf482580a22991a568455e0d6d3b58ba0ea5c675d1b`。该资料采用 **CC BY-NC 4.0**，具有署名和非商业使用条件；这项资料的许可不因模型采用 MIT 而改变。原始署名、发布者和许可记录位于 `models/bioclip2/licenses/ChinaChecklist-v1.1-EML.xml`，完整许可位于 `ChinaChecklist-CC-BY-NC-4.0.txt`。再次分发时保留这些文件，并单独评估候选资料是否适合预期用途。

候选整理优先使用学名精确匹配；仅在精确匹配缺失时使用经目、科和其他已接受名冲突检查的同义词。逐项来源、未匹配名称和排除原因随 `candidates.json`、`china-unmatched.json` 及相关审计文件保存。候选表只限定比对范围，不保证照片中一定有该物种，也不代表完整覆盖中国现行鸟种名录。

## Microsoft.ML.OnnxRuntime 1.20.1

来源：[microsoft/onnxruntime](https://github.com/microsoft/onnxruntime/tree/v1.20.1)

许可证：[MIT License](https://github.com/microsoft/onnxruntime/blob/v1.20.1/LICENSE)

该包包含具有独立许可证的第三方组件。原始 MIT 许可和该包完整第三方声明随附在 `licenses/ONNXRuntime-LICENSE.txt` 和 `licenses/ONNXRuntime-ThirdPartyNotices.txt`；请一并保留。

```text
MIT License

Copyright (c) Microsoft Corporation

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:
The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.
THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## .NET 8 与 Windows Presentation Foundation

本应用自包含发布时携带 Microsoft .NET Windows Desktop 运行库。

- [.NET Runtime 源码和许可](https://github.com/dotnet/runtime)
- [WPF 源码和许可](https://github.com/dotnet/wpf)
- 本次 Windows x64 发布运行库版本为 8.0.33，随附以下 NuGet 运行库包的原始声明：
  - `licenses/DotNetRuntime-LICENSE.txt`：`Microsoft.NETCore.App.Runtime.win-x64` 的 MIT 许可。
  - `licenses/DotNetRuntime-ThirdPartyNotices.txt`：同包完整第三方声明。
  - `licenses/WindowsDesktopRuntime-LICENSE.txt`：`Microsoft.WindowsDesktop.App.Runtime.win-x64` 的 MIT 许可。该版本的 WindowsDesktop 运行库包未另外提供第三方声明文件。

以上原文随程序一同分发。升级 .NET 运行库时，应从实际发布所用版本的 NuGet 包同步更新这些文件和版本记录。

开发 SDK 和 NuGet 缓存位于 `.tools`，属于本地构建工具，不需要随应用一起分发。
