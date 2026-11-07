#!/usr/bin/env python3
"""构建并启动 Avalonia 桌面入口；--build-only 只构建，--check 执行真实渲染和输入检查。"""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import sys
# 项目内嵌式 Python 启用隔离路径，明确只加入本脚本目录以加载公共构建工具。
sys.path.insert(0, str(Path(__file__).resolve().parent))
from ReleaseSupport import host_runtime


def main():
    """选择项目 SDK，原生输出缺失时先构建；不覆盖 publish 或稳定版本。"""
    root = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--build-only", action="store_true")
    parser.add_argument("--check", action="store_true")
    parser.add_argument("files", nargs="*", help="Local JSON project or JPG/RAW files")
    args = parser.parse_args()
    local = root / ".tools/dotnet" / ("dotnet.exe" if sys.platform == "win32" else "dotnet")
    dotnet = str(local) if local.exists() else shutil.which("dotnet")
    if not dotnet:
        raise RuntimeError("Install the pinned .NET SDK with scripts/Setup-Dotnet.py first")
    env = os.environ.copy()
    env["DOTNET_CLI_HOME"] = str(root / ".tools/dotnet-home")
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    if local.exists():
        env["DOTNET_ROOT"] = str(local.parent)
    native = root / ".tools/native" / host_runtime()
    if sys.platform != "win32" and not any(native.glob("libBirdPhotoSelector.Platform.Native.*")):
        subprocess.run([sys.executable, str(root / "scripts/Build-Native.py")], cwd=root, env=env, check=True)
    project = root / ("Tests/Desktop/BirdPhotoSelector.Desktop.Checks.csproj" if args.check else "src/BirdPhotoSelector.Desktop/BirdPhotoSelector.Desktop.csproj")
    command = [dotnet, "build" if args.build_only else "run", "--project", str(project), "-c", "Release"]
    if args.build_only:
        command.remove("--project")
    if args.files and not args.build_only and not args.check:
        command.extend(["--", *map(lambda p: str(Path(p).resolve()), args.files)])
    subprocess.run(command, cwd=root, env=env, check=True)


if __name__ == "__main__":
    main()
