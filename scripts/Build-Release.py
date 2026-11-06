#!/usr/bin/env python3
"""生成 v2 独立目录及 ZIP/tar.gz：Windows x64、Apple Silicon、Ubuntu x64；完整校验后才完成发布。"""
import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import plistlib
import shutil
import subprocess
import sys
import tarfile
import uuid
import zipfile
# 项目内嵌式 Python 启用隔离路径，明确只加入本脚本目录以加载公共构建工具。
sys.path.insert(0, str(Path(__file__).resolve().parent))
from ReleaseSupport import ROOT, RUNTIMES, architectures, host_runtime, run, sdk, sha256, validate_payload, version


def verify_archive(archive, expected):
    """重新读取归档的每一个普通文件，逐项对照哈希，拒绝缺失、重复和额外成员。"""
    import hashlib
    seen = set()
    def verify(name, stream):
        if name in seen or name not in expected: raise RuntimeError("Unexpected archive member: " + name)
        seen.add(name); digest = hashlib.sha256()
        for block in iter(lambda: stream.read(4 * 1024 * 1024), b""): digest.update(block)
        if digest.hexdigest() != expected[name]: raise RuntimeError("Archive fingerprint mismatch: " + name)
    if archive.suffix == ".zip":
        with zipfile.ZipFile(archive) as source:
            for entry in source.infolist():
                if not entry.is_dir():
                    with source.open(entry) as stream: verify(entry.filename, stream)
    else:
        with tarfile.open(archive) as source:
            for entry in source:
                if entry.isfile():
                    with source.extractfile(entry) as stream: verify(entry.name, stream)
                elif not entry.isdir(): raise RuntimeError("Unsupported archive link: " + entry.name)
    if seen != expected.keys(): raise RuntimeError("Archive has missing members")


def archive_folder(folder, runtime):
    """Unix 使用 tar 保留可执行权限；校验通过后才写入外部 SHA-256 文件。"""
    archive = folder.with_name(folder.name + (".zip" if runtime.startswith("win-") else ".tar.gz"))
    if archive.exists(): raise FileExistsError("Archive already exists: " + str(archive))
    files = sorted(p for p in folder.rglob("*") if p.is_file())
    expected = {p.relative_to(folder.parent).as_posix(): sha256(p) for p in files}
    if runtime.startswith("win-"):
        with zipfile.ZipFile(archive, "x", compression=zipfile.ZIP_DEFLATED, compresslevel=1) as target:
            for path in files: target.write(path, path.relative_to(folder.parent).as_posix())
    else:
        with tarfile.open(archive, "w:gz", compresslevel=1) as target:
            for path in [folder, *sorted(folder.rglob("*"))]:
                info = target.gettarinfo(str(path), path.relative_to(folder.parent).as_posix())
                info.uid = info.gid = 0; info.uname = info.gname = ""
                info.mode = 0o755 if path.is_dir() or path.name == "BirdPhotoSelector" or path.suffix in (".sh", ".command", ".dylib") else 0o644
                if info.isfile():
                    with path.open("rb") as stream: target.addfile(info, stream)
                else: target.addfile(info)
    verify_archive(archive, expected)
    archive.with_name(archive.name + ".sha256").write_text(sha256(archive) + "  " + archive.name + "\n", encoding="utf-8")
    return archive


def make_layout(stage, runtime, release):
    """Mac 使用标准 .app，其他平台为独立目录；可直接移动整个目录，不要求额外安装 .NET。"""
    if runtime.startswith("osx-"):
        app = stage / "BirdPhotoSelector.app" / "Contents"
        payload = app / "MacOS"; (app / "Resources").mkdir(parents=True)
        shutil.copy2(ROOT / "Assets/App.icns", app / "Resources/App.icns")
        info = {"CFBundleName": "羽选", "CFBundleDisplayName": "羽选", "CFBundleIdentifier": "com.yyx263.birdphotoselector", "CFBundleExecutable": "BirdPhotoSelector", "CFBundlePackageType": "APPL", "CFBundleIconFile": "App.icns", "CFBundleShortVersionString": release.split("-", 1)[0], "CFBundleVersion": release.split("-", 1)[0], "LSMinimumSystemVersion": "14.0", "NSHighResolutionCapable": True}
        (app / "Info.plist").write_bytes(plistlib.dumps(info))
    else: payload = stage
    payload.mkdir(parents=True, exist_ok=True)
    return payload


def platform_files(stage, payload, runtime):
    """添加各平台启动及反馈说明；不运行安装、提权或安全设置修改。"""
    if runtime == "linux-x64":
        shutil.copy2(ROOT / "Assets/App.png", payload / "App.png")
        (payload / "start.sh").write_text('#!/bin/sh\nset -eu\napp_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)\nexec "$app_dir/BirdPhotoSelector" "$@"\n', encoding="utf-8", newline="\n")
    relative = "BirdPhotoSelector.app/Contents/MacOS/BirdPhotoSelector" if runtime.startswith("osx-") else "BirdPhotoSelector.exe" if runtime.startswith("win-") else "./BirdPhotoSelector"
    notes = f"羽选 {version()} · {runtime}\n\n解压后保留完整目录。内含 .NET、定位/鸟种模型和本平台原生组件，不需要安装 Python。\n"
    notes += "Windows 双击 BirdPhotoSelector.exe；Mac 双击 BirdPhotoSelector.app；Ubuntu 执行 ./start.sh。\n\n"
    if runtime.startswith("osx-"):
        notes += "适用 Apple 芯片、macOS 14 或更高版本。本测试包尚未做 Apple 开发者证书签名与公证；系统阻止首次打开时，可在系统设置的隐私与安全性中允许打开。\n"
    if runtime.startswith("linux-"):
        notes += "适用 Ubuntu 22.04/24.04 x64 图形桌面（glibc >= 2.35）。需要系统 OpenSSL、ICU、Fontconfig、X11/XCB、EGL/GL、GIO 和中文字体。依赖安装示例见随包使用说明。\n"
    notes += f"\n在终端运行自检：\n{relative} --self-test self-test.json\n{relative} --smoke-test desktop-test.json\n第一项验证模型和原生库，第二项短暂打开最小化窗口后自动关闭。JSON 中 Passed 应为 true。\n\n再手动测试：导入 JPG/RAW、连续切图、识别/手动选区、评级、工程保存恢复和导出。先使用照片副本验证移除原件和回收站行为。\n\nWindows 已有本机检查；Mac/Ubuntu 交叉构建包需在目标机器完成桌面、中文输入和回收站验收。请反馈系统版本、操作步骤和自检 JSON。\n"
    (stage / "测试说明.txt").write_text(notes, encoding="utf-8", newline="\n")


def build(runtime):
    """固定目标并逐个平台顺序发布，避免多个 RID 同时覆盖 MSBuild 中间资源。"""
    release = version(); publish = (ROOT / "publish").resolve(); publish.mkdir(exist_ok=True)
    name = f"BirdPhotoSelector-{release}-{runtime}"; final = publish / name
    if final.exists() or final.with_name(name + (".zip" if runtime.startswith("win-") else ".tar.gz")).exists(): raise FileExistsError("Release output already exists: " + str(final))
    stage = publish / (".staging-" + name + "-" + uuid.uuid4().hex)
    payload = make_layout(stage, runtime, release)
    dotnet, env = sdk()
    try:
        run([sys.executable, ROOT / "scripts/Build-Native.py", "--runtime", runtime], env)
        run([dotnet, "publish", ROOT / "src/BirdPhotoSelector.Desktop/BirdPhotoSelector.Desktop.csproj", "-c", "Release", "-r", runtime, "--self-contained", "true", "-p:PublishSingleFile=false", "-p:PublishTrimmed=false", "-p:PublishReadyToRun=false", "-p:DebugType=None", "-p:DebugSymbols=false", "-p:BpsSkipNativeBuild=true", "-o", payload], env)
        for path in payload.rglob("*.pdb"): path.unlink()
        binaries = validate_payload(payload, runtime)
        platform_files(stage, payload, runtime)
        executable = payload / ("BirdPhotoSelector.exe" if runtime.startswith("win-") else "BirdPhotoSelector")
        if not runtime.startswith("win-"): executable.chmod(0o755)
        if runtime.startswith("osx-") and sys.platform == "darwin":
            run(["codesign", "--force", "--deep", "--sign", "-", stage / "BirdPhotoSelector.app"], env)
        tested = runtime == host_runtime()
        if tested:
            reports = publish / "validation"; reports.mkdir(exist_ok=True)
            for flag, label in (("--self-test", "runtime"), ("--smoke-test", "desktop")):
                report = reports / (name + "-" + label + ".json")
                run([executable, flag, report], env, timeout=180)
                if not json.loads(report.read_text(encoding="utf-8")).get("Passed"): raise RuntimeError("Published application check failed: " + str(report))
        files = [{"Path": p.relative_to(stage).as_posix(), "Bytes": p.stat().st_size, "SHA256": sha256(p)} for p in sorted(stage.rglob("*")) if p.is_file()]
        source = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
        dirty = bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT, text=True).strip())
        manifest = {"SchemaVersion": 1, "Version": release, "Runtime": runtime, "SourceCommit": source, "SourceDirty": dirty, "BuiltAtUtc": datetime.now(timezone.utc).isoformat(), "BuildHost": host_runtime(), "ExecutedOnTarget": tested, "NativeArchitectures": binaries, "Files": files}
        (stage / "release-manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        stage.rename(final)
        archive = archive_folder(final, runtime)
        print("PACKAGE:", archive, flush=True)
    finally:
        # 只允许清理本函数生成的随机 staging 子目录，输出包或用户路径不受影响。
        if stage.exists():
            if stage.resolve().parent != publish or not stage.name.startswith(".staging-BirdPhotoSelector-"): raise RuntimeError("Unsafe stage cleanup")
            shutil.rmtree(stage)


def main():
    """默认生成三套完整测试包；已存在输出时停止，避免静默覆盖。"""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime", action="append", choices=RUNTIMES, help="Repeat for several targets; default: all three")
    args = parser.parse_args()
    for runtime in dict.fromkeys(args.runtime or RUNTIMES): build(runtime)


if __name__ == "__main__": main()