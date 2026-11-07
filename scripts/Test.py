#!/usr/bin/env python3
"""新工程完整验证入口；五套检查均不引用 WPF，仅真实窗口探针需要图形桌面。"""
import argparse
from pathlib import Path
import sys
# 项目内嵌式 Python 启用隔离路径，明确只加入本脚本目录以加载公共构建工具。
sys.path.insert(0, str(Path(__file__).resolve().parent))
from ReleaseSupport import ROOT, run, sdk


def main():
    """只操作随机生成夹具；真实模型/RAW 可选，使用现有文件而不自动下载。"""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--include-model", action="store_true")
    parser.add_argument("--native-window", action="store_true")
    args = parser.parse_args(); dotnet, env = sdk()
    if (ROOT / ".tools/raw-samples").is_dir(): env.setdefault("BIRD_RAW_SAMPLES", str(ROOT / ".tools/raw-samples"))
    if args.include_model:
        env["BIRD_MODEL_PATH"] = str(ROOT / "models/yolov10n.onnx")
        env["BIRD_SAMPLE_PATH"] = str(ROOT / "Tests/Assets/great-tit.jpg")
        if not Path(env["BIRD_MODEL_PATH"]).exists(): raise RuntimeError("Local model required")
    run([sys.executable, ROOT / "scripts/Build-Native.py"], env)
    for project in ("Imaging.Core/BirdPhotoSelector.Imaging.Checks.csproj", "Application.Core/BirdPhotoSelector.Application.Checks.csproj", "PortableAdapters/BirdPhotoSelector.PortableAdapters.Checks.csproj", "Workflows/BirdPhotoSelector.Workflow.Checks.csproj", "Desktop/BirdPhotoSelector.Desktop.Checks.csproj"):
        run([dotnet, "run", "--project", ROOT / "Tests" / project, "-c", "Release"], env)
    if args.native_window: run([dotnet, "run", "--project", ROOT / "Tests/Desktop/BirdPhotoSelector.Desktop.Checks.csproj", "-c", "Release", "--no-build", "--", "--native-window"], env, timeout=120)


if __name__ == "__main__": main()
