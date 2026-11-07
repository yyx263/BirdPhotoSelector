"""发布共用的 SDK、校验和与二进制架构检查；不修改系统环境或用户图片。"""
from pathlib import Path
import hashlib
import json
import os
import platform
import shutil
import struct
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
RUNTIMES = ("win-x64", "osx-arm64", "linux-x64")


def host_runtime():
    """只根据实际构建进程选宿主，不把跨平台发布等同于目标平台验证。"""
    return ("win" if sys.platform == "win32" else "osx" if sys.platform == "darwin" else "linux") + "-" + ("arm64" if platform.machine().lower() in ("aarch64", "arm64") else "x64")


def sdk():
    """优先工程内 SDK，找不到才用 PATH，固定版本仍由 global.json 检查。"""
    local = ROOT / ".tools/dotnet" / ("dotnet.exe" if sys.platform == "win32" else "dotnet")
    executable = str(local) if local.exists() else shutil.which("dotnet")
    if not executable: raise RuntimeError("Run scripts/Setup-Dotnet.py or install the pinned .NET SDK first")
    env = os.environ.copy(); env["DOTNET_CLI_HOME"] = str(ROOT / ".tools/dotnet-home"); env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    if local.exists(): env["DOTNET_ROOT"] = str(local.parent)
    return executable, env


def run(arguments, env=None, timeout=None):
    """所有外部命令均使用参数数组，文件名不进入 shell 拼接。"""
    print("+", " ".join(map(str, arguments)), flush=True)
    subprocess.run(list(map(str, arguments)), cwd=ROOT, env=env, check=True, timeout=timeout)


def version():
    """发布名称与程序集共用唯一版本来源。"""
    return ET.parse(ROOT / "Directory.Build.props").getroot().findtext("PropertyGroup/Version")


def sha256(path):
    """流式处理大模型和包，避免把 GB 级资源读入内存。"""
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(4 * 1024 * 1024), b""): digest.update(block)
    return digest.hexdigest()


def architectures(path):
    """读取 PE/ELF/Mach-O 头的目标架构；托管 PE 返回空，避免把 AnyCPU 当作原生 x86。"""
    with Path(path).open("rb") as stream:
        data = stream.read(4096)
        if data[:2] == b"MZ":
            offset = struct.unpack_from("<I", data, 60)[0]
            stream.seek(offset); header = stream.read(264)
            if header[:4] != b"PE\0\0": raise ValueError("Invalid PE: " + str(path))
            magic = struct.unpack_from("<H", header, 24)[0]
            cli = 24 + (112 if magic == 0x20B else 96) + 14 * 8
            if len(header) >= cli + 8 and struct.unpack_from("<I", header, cli)[0]: return set()
            return { {0x8664: "win-x64", 0xAA64: "win-arm64"}.get(struct.unpack_from("<H", header, 4)[0], "unknown-pe") }
        if data[:4] == b"\x7fELF":
            return { {62: "linux-x64", 183: "linux-arm64"}.get(struct.unpack_from("<H", data, 18)[0], "unknown-elf") }
        if data[:4] in (b"\xcf\xfa\xed\xfe", b"\xce\xfa\xed\xfe"):
            return { {0x100000C: "osx-arm64", 0x1000007: "osx-x64"}.get(struct.unpack_from("<I", data, 4)[0], "unknown-macho") }
        if data[:4] in (b"\xca\xfe\xba\xbe", b"\xca\xfe\xba\xbf"):
            count = struct.unpack_from(">I", data, 4)[0]; stride = 32 if data[3] == 0xBF else 20
            return {{0x100000C: "osx-arm64", 0x1000007: "osx-x64"}.get(struct.unpack_from(">I", data, 8 + i * stride)[0], "unknown-macho") for i in range(count)}
    return set()


def validate_payload(payload, runtime):
    """拒绝缺失关键库、混入异平台原生文件或旧 WPF；校验完整模型指纹。"""
    suffix = ".dll" if runtime.startswith("win-") else ".dylib" if runtime.startswith("osx-") else ".so"
    prefix = "" if runtime.startswith("win-") else "lib"
    required = ["BirdPhotoSelector" + (".exe" if runtime.startswith("win-") else ""), "BirdPhotoSelector.dll", "BirdPhotoSelector.runtimeconfig.json", "BirdPhotoSelector.deps.json",
                prefix + "BirdPhotoSelector.Raw" + suffix, prefix + "BirdPhotoSelector.Platform.Native" + suffix,
                ("libraw.dll" if runtime.startswith("win-") else "libraw" + suffix), "libSkiaSharp" + suffix, prefix + "onnxruntime" + suffix,
                "THIRD_PARTY_NOTICES.md", "使用说明.md", "RELEASE_NOTES.md", "licenses/Avalonia-MIT.txt", "licenses/LibRaw-CDDL-1.0.txt"]
    if runtime.startswith("win-"): required += ["msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll", "coreclr.dll"]
    else: required += ["libcoreclr" + suffix]
    for name in required:
        if not (payload / name).is_file(): raise RuntimeError("Required package file missing: " + name)
    forbidden = {"PresentationCore.dll", "PresentationFramework.dll", "ReachFramework.dll", "System.Xaml.dll", "BirdPhotoSelector.Imaging.Windows.dll", "wpfgfx_cor3.dll", "PenImc_cor3.dll"}
    binaries = {}
    for path in payload.rglob("*"):
        if not path.is_file(): continue
        if path.name in forbidden: raise RuntimeError("Old WPF file in package: " + str(path))
        targets = architectures(path)
        if targets:
            if runtime not in targets: raise RuntimeError(f"Wrong native architecture: {path}: {targets}")
            binaries[path.relative_to(payload).as_posix()] = sorted(targets)
    if sha256(payload / "models/yolov10n.onnx") != "a77dd863933f184a19e84361c64b788228a7c7dacc2c78939239a96ad3efca3b": raise RuntimeError("YOLO fingerprint mismatch")
    expected = json.loads((payload / "models/bioclip2/asset-hashes.json").read_text(encoding="utf-8-sig"))
    for name, expected_hash in expected.items():
        if sha256(payload / "models/bioclip2" / name) != expected_hash: raise RuntimeError("BioCLIP fingerprint mismatch: " + name)
    return binaries


def package_platform(runtime):
    """对外采用 macos 命名；内部保留 .NET 标准 osx-arm64 RID，不能按名称猜测芯片。"""
    return "macos-arm64" if runtime == "osx-arm64" else runtime
