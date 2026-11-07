#!/usr/bin/env python3
"""在 Linux/macOS 编译固定 LibRaw 与两个 C ABI 桥接；Windows 复用现有 SDK 校验和 CMake 构建入口。"""
import argparse
from concurrent.futures import ThreadPoolExecutor
import re
import zipfile
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
        source.extractall(cache, **({"filter": "data"} if hasattr(tarfile, "data_filter") else {}))


def main():
    """所有默认产物位于工程 .tools，输出为当前架构的 .so/.dylib；不安装系统级软件。"""
    root = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime", choices=("win-x64", "linux-x64", "osx-arm64"), help="Target RID; foreign Unix targets use the pinned Zig compiler")
    parser.add_argument("--zig", type=Path, help="Optional preinstalled Zig 0.17.1 compiler")
    parser.add_argument("--libraw-prefix", type=Path, help="Existing exact LibRaw 0.22.3 install prefix")
    parser.add_argument("--jobs", type=int, default=min(8, os.cpu_count() or 1))
    args = parser.parse_args()
    if args.jobs < 1:
        parser.error("--jobs must be positive")
    host = ("win" if sys.platform == "win32" else "osx" if sys.platform == "darwin" else "linux") + "-" + ("arm64" if platform.machine().lower() in ("aarch64", "arm64") else "x64")
    runtime = args.runtime or host
    if runtime != host:
        cross_build(root, runtime, args.jobs, args.zig)
        return
    if sys.platform == "win32":
        run(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", root / "scripts/Build-RawDecoder.ps1"])
        return
    if sys.platform not in ("linux", "darwin"):
        raise RuntimeError("Only Linux, macOS and Windows are supported")
    manifest = json.loads((root / "native/dependencies.json").read_text(encoding="utf-8"))
    version = manifest["LibRawVersion"]
    output = root / ".tools/native" / runtime
    cache = root / ".tools/libraw" / version
    cache.mkdir(parents=True, exist_ok=True)
    prefix = args.libraw_prefix.resolve() if args.libraw_prefix else cache / runtime / "prefix"
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
        build = cache / runtime / "build"
        build.mkdir(exist_ok=True)
        run([source / "configure", "--prefix=" + str(prefix), "--disable-static", "--disable-openmp", "--disable-jpeg", "--disable-zlib", "--disable-lcms", "--disable-examples"], cwd=build)
        run(["make", "-j" + str(args.jobs)], cwd=build)
        run(["make", "install"], cwd=build)
    build = root / ".tools/native-build" / runtime
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



def compiler(root, existing):
    """取得项目内固定交叉编译器；下载校验成功才解压，既不修改 PATH 也不安装系统组件。"""
    if existing:
        result = existing.resolve()
    else:
        if sys.platform != "win32" or platform.machine().lower() not in ("amd64", "x86_64"):
            raise RuntimeError("Foreign targets require --zig with Zig 0.17.1 on this host")
        record = json.loads((root / "scripts/NativeToolchain.json").read_text(encoding="utf-8"))
        directory = root / ".tools/zig-0.17.1"
        archive = root / ".tools/zig-0.17.1.zip"
        if not archive.exists():
            partial = archive.with_suffix(".partial")
            urllib.request.urlretrieve(record["url"], partial)
            if sha256(partial) != record["sha256"]:
                raise RuntimeError("Zig download SHA-256 mismatch")
            partial.replace(archive)
        if sha256(archive) != record["sha256"]:
            raise RuntimeError("Zig cache SHA-256 mismatch")
        result = directory / "zig.exe"
        if not result.exists():
            with zipfile.ZipFile(archive) as source:
                for entry in source.infolist():
                    target = directory.joinpath(*Path(entry.filename).parts[1:])
                    if not target.resolve().is_relative_to(directory.resolve()):
                        raise ValueError("Unsafe Zig archive entry")
                    if entry.is_dir(): target.mkdir(parents=True, exist_ok=True)
                    else:
                        target.parent.mkdir(parents=True, exist_ok=True)
                        target.write_bytes(source.read(entry))
    if subprocess.check_output([str(result), "version"], text=True).strip() != "0.17.1":
        raise RuntimeError("Pinned Zig 0.17.1 required")
    return result


def cross_build(root, runtime, jobs, existing):
    """交叉编译完整原生桥接，LibRaw 保持独立动态库；每个 RID 独立产物、对象及校验记录。"""
    targets = {"linux-x64": "x86_64-linux-gnu.2.35", "osx-arm64": "aarch64-macos.14.0"}
    if runtime not in targets:
        raise RuntimeError("This cross builder supports linux-x64 and osx-arm64 only")
    zig = compiler(root, existing)
    manifest = json.loads((root / "native/dependencies.json").read_text(encoding="utf-8"))
    cache = root / ".tools/libraw/cross-source"
    cache.mkdir(parents=True, exist_ok=True)
    archive = cache / ("LibRaw-" + manifest["LibRawVersion"] + ".tar.gz")
    if not archive.exists():
        partial = archive.with_suffix(".partial")
        urllib.request.urlretrieve(manifest["SourceUrl"], partial)
        if sha256(partial) != manifest["SourceSha256"]: raise RuntimeError("LibRaw SHA-256 mismatch")
        partial.replace(archive)
    if sha256(archive) != manifest["SourceSha256"]: raise RuntimeError("LibRaw cache SHA-256 mismatch")
    # 从已验证源码重新恢复指定文件，避免采用手工改过但仍保留旧版本号的缓存头文件。
    extract(archive, cache)
    source = cache / ("LibRaw-" + manifest["LibRawVersion"])
    makefile = (source / "Makefile.am").read_text(encoding="utf-8")
    listing = makefile.split("lib_libraw_a_SOURCES =", 1)[1].split("lib_libraw_r_a_CXXFLAGS", 1)[0]
    sources = [source / name for name in re.findall(r"src/[\w/]+\.cpp", listing)]
    if len(sources) < 70 or len(set(sources)) != len(sources): raise RuntimeError("Unexpected pinned LibRaw source list")
    output = root / ".tools/native" / runtime
    build = root / ".tools/native-build" / runtime
    output.mkdir(parents=True, exist_ok=True); build.mkdir(parents=True, exist_ok=True)
    env = os.environ.copy(); env["ZIG_GLOBAL_CACHE_DIR"] = str(root / ".tools/zig-cache")
    env["ZIG_LOCAL_CACHE_DIR"] = str(build / "zig-cache")
    digest = hashlib.sha256()
    inputs = [Path(__file__), *sorted((root / "native").rglob("*"))]
    for path in inputs:
        if path.is_file(): digest.update(path.read_bytes())
    digest.update((manifest["SourceSha256"] + runtime + "zig-0.17.1-O2-nothreads-no-optional-codecs").encode())
    fingerprint = digest.hexdigest(); stamp = output / "build-record.json"
    if stamp.exists():
        record = json.loads(stamp.read_text(encoding="utf-8"))
        if record.get("Fingerprint") == fingerprint and all((output / name).is_file() and sha256(output / name) == value for name, value in record["Files"].items()):
            print("Verified native cache:", output, flush=True); return
    target = targets[runtime]
    common = ["-target", target, "-O2", "-fPIC", "-std=c++17", "-DLIBRAW_NOTHREADS", "-I", str(source)]
    objects = []
    for item in sources:
        obj = build / (str(item.relative_to(source)).replace("/", "_").replace("\\", "_") + ".o")
        objects.append(obj)
    def compile_item(pair):
        """每次编译只有一个输入与一个输出，使用固定目标而非当前处理器指令集。"""
        item, obj = pair
        subprocess.run([str(zig), "c++", *common, "-c", str(item), "-o", str(obj)], env=env, check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    print(f"Compiling {len(sources)} pinned LibRaw sources for {runtime}…", flush=True)
    try:
        with ThreadPoolExecutor(max_workers=min(jobs, 4)) as pool:
            list(pool.map(compile_item, zip(sources, objects)))
    except subprocess.CalledProcessError as error:
        print(error.stderr.decode(errors="replace"), file=sys.stderr); raise
    mac = runtime.startswith("osx-")
    raw = output / ("libraw.dylib" if mac else "libraw.so")
    link = ["-Wl,-install_name,@rpath/libraw.dylib"] if mac else ["-Wl,-soname,libraw.so"]
    run([zig, "c++", "-shared", "-target", target, "-O2", *objects, *link, "-o", raw], env=env)
    suffix = ".dylib" if mac else ".so"
    bridge = output / ("libBirdPhotoSelector.Raw" + suffix)
    relative = ["-Wl,-rpath,@loader_path", "-Wl,-install_name,@rpath/" + bridge.name] if mac else ["-Wl,-rpath,$ORIGIN"]
    run([zig, "c++", "-shared", *common, root / "native/raw-decoder.cpp", "-L", output, "-lraw", *relative, "-o", bridge], env=env)
    system = output / ("libBirdPhotoSelector.Platform.Native" + suffix)
    platform_sources = [root / "native/platform.cpp"]
    frameworks = []
    if mac:
        platform_sources += [root / "native/platform-macos.mm"]
        imports = root / "native/macos-imports"
        frameworks = ["-F", imports, "-L", imports, "-framework", "Foundation", "-lobjc", "-fobjc-exceptions", "-Wl,-install_name,@rpath/" + system.name]
    run([zig, "c++", "-shared", *common, *platform_sources, *frameworks, "-o", system], env=env)
    record = {"Runtime": runtime, "Target": target, "Compiler": "Zig 0.17.1", "LibRaw": manifest["LibRawVersion"], "RawAbi": 3, "PlatformAbi": 1,
              "Fingerprint": fingerprint, "Files": {p.name: sha256(p) for p in (raw, bridge, system)}, "ValidatedOnTarget": False}
    stamp.write_text(json.dumps(record, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print("Cross-compiled native outputs:", output, "(target-machine tests still required)", flush=True)


if __name__ == "__main__":
    main()
