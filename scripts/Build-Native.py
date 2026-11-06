#!/usr/bin/env python3
"""在 Linux/macOS 编译固定 LibRaw 与两个 C ABI 桥接；Windows 复用现有 SDK 校验和 CMake 构建入口。"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import tarfile
import urllib.request


def run(arguments, cwd=None, env=None):
    """仅用参数数组启动构建工具，不拼接文件名到 shell；失败立即停止，保留日志供复查。"""
    print("+", " ".join(map(str, arguments)), flush=True)
    subprocess.run(list(map(str, arguments)), cwd=cwd, env=env, check=True)


def sha256(path):
    """分块校验固定源码包，绝不采用未验证缓存或下载内容。"""
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def extract(archive, cache):
    """只解出预期源码根内的普通文件和目录，拒绝绝对路径、越界和链接成员。"""
    with tarfile.open(archive) as source:
        for member in source.getmembers():
            target = (cache / member.name).resolve()
            if not target.is_relative_to(cache.resolve()) or not (member.isdir() or member.isfile()):
                raise ValueError("Unsafe archive member: " + member.name)
        source.extractall(cache)


def main():
    """所有默认产物位于工程 .tools，输出为当前架构的 .so/.dylib；不安装系统级软件。"""
    root = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--libraw-prefix", type=Path, help="Existing exact LibRaw 0.22.3 install prefix")
    parser.add_argument("--jobs", type=int, default=min(8, os.cpu_count() or 1))
    args = parser.parse_args()
    if args.jobs < 1:
        parser.error("--jobs must be positive")
    if sys.platform == "win32":
        run(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", root / "scripts/Build-RawDecoder.ps1"])
        return
    if sys.platform not in ("linux", "darwin"):
        raise RuntimeError("Only Linux, macOS and Windows are supported")
    manifest = json.loads((root / "native/dependencies.json").read_text(encoding="utf-8"))
    version = manifest["LibRawVersion"]
    output = root / ".tools/raw-decoder"
    cache = root / ".tools/libraw" / version
    cache.mkdir(parents=True, exist_ok=True)
    prefix = args.libraw_prefix.resolve() if args.libraw_prefix else cache / "prefix"
    if not args.libraw_prefix and not (prefix / "include/libraw/libraw.h").exists():
        archive = cache / ("LibRaw-" + version + ".tar.gz")
        if not archive.exists():
            partial = archive.with_suffix(".partial")
            urllib.request.urlretrieve(manifest["SourceUrl"], partial)
            if sha256(partial) != manifest["SourceSha256"]:
                raise RuntimeError("LibRaw source SHA-256 mismatch")
            partial.replace(archive)
        if sha256(archive) != manifest["SourceSha256"]:
            raise RuntimeError("Cached LibRaw source SHA-256 mismatch")
        source = cache / ("LibRaw-" + version)
        if not (source / "configure").exists():
            extract(archive, cache)
        build = cache / "build"
        build.mkdir(exist_ok=True)
        run([source / "configure", "--prefix=" + str(prefix), "--disable-static", "--disable-openmp"], cwd=build)
        run(["make", "-j" + str(args.jobs)], cwd=build)
        run(["make", "install"], cwd=build)
    build = root / ".tools/native-build" / (sys.platform + "-" + platform.machine())
    output.mkdir(parents=True, exist_ok=True)
    run(["cmake", "-S", root / "native", "-B", build, "-DCMAKE_BUILD_TYPE=Release", "-DLIBRAW_ROOT=" + str(prefix), "-DCMAKE_INSTALL_PREFIX=" + str(output)])
    run(["cmake", "--build", build, "--parallel", args.jobs])
    run(["cmake", "--install", build])
    for pattern in ("libraw*.so*", "libraw*.dylib"):
        for library in (prefix / "lib").glob(pattern):
            if library.is_file():
                shutil.copy2(library, output / library.name)
    if sys.platform == "darwin":
        bridge = output / "libBirdPhotoSelector.Raw.dylib"
        # libtool normally uses an absolute install name. Make the LibRaw edge relocatable.
        listing = subprocess.check_output(["otool", "-L", str(bridge)], text=True)
        for line in listing.splitlines()[1:]:
            dependency = line.strip().split(" (", 1)[0]
            if Path(dependency).name.startswith("libraw"):
                run(["install_name_tool", "-change", dependency, "@loader_path/" + Path(dependency).name, bridge])
    print("Native outputs:", output)
    print("This creates development binaries; final packages still require per-OS dependency and signing validation.")


if __name__ == "__main__":
    main()
