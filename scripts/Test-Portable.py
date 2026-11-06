#!/usr/bin/env python3
"""在当前系统执行图像、业务和真实适配验证，不构建或启动 WPF。"""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import sys


def main():
    """可选先编译原生组件；验证只操作生成文件，样本原件只读。"""
    root = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--build-native", action="store_true")
    args = parser.parse_args()
    env = os.environ.copy()
    env["DOTNET_CLI_HOME"] = str(root / ".tools/dotnet-home")
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    if args.build_native:
        subprocess.run([sys.executable, str(root / "scripts/Build-Native.py")], cwd=root, env=env, check=True)
    dotnet = root / ".tools/dotnet" / ("dotnet.exe" if sys.platform == "win32" else "dotnet")
    if not dotnet.exists():
        dotnet = shutil.which("dotnet")
    if not dotnet:
        raise RuntimeError("Install a supported .NET SDK first")
    if (root / ".tools/raw-samples").is_dir():
        env.setdefault("BIRD_RAW_SAMPLES", str(root / ".tools/raw-samples"))
    for project in ("Imaging.Core/BirdPhotoSelector.Imaging.Checks.csproj", "Application.Core/BirdPhotoSelector.Application.Checks.csproj", "PortableAdapters/BirdPhotoSelector.PortableAdapters.Checks.csproj"):
        subprocess.run([str(dotnet), "run", "--project", str(root / "Tests" / project), "-c", "Release"], cwd=root, env=env, check=True)


if __name__ == "__main__":
    main()
