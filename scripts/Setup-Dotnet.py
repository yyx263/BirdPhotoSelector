#!/usr/bin/env python3
"""校验固定官方 SHA-512，将当前平台 .NET SDK 安装到工程 .tools/dotnet，不修改系统环境。"""
import hashlib
import json
from pathlib import Path
import platform
import sys
import tarfile
import urllib.request
import zipfile


def main():
    """只解压当前系统与架构的已校验归档；缓存下载先写 partial，成功后原子改名。"""
    root = Path(__file__).resolve().parent.parent
    manifest = json.loads((root / "scripts/DotNetSdk.json").read_text(encoding="utf-8"))
    architecture = "arm64" if platform.machine().lower() in ("arm64", "aarch64") else "x64" if platform.machine().lower() in ("amd64", "x86_64") else None
    system = {"win32": "win", "linux": "linux", "darwin": "osx"}.get(sys.platform)
    if architecture is None or system is None:
        raise RuntimeError("Unsupported SDK host architecture")
    entry = manifest["files"][system + "-" + architecture]
    tools = root / ".tools"; tools.mkdir(exist_ok=True)
    target = tools / "dotnet"; target.mkdir(exist_ok=True)
    archive = tools / Path(entry["url"]).name
    if not archive.exists():
        partial = archive.with_suffix(archive.suffix + ".partial")
        print("Downloading", entry["url"], flush=True)
        urllib.request.urlretrieve(entry["url"], partial)
        verify(partial, entry["sha512"])
        partial.replace(archive)
    verify(archive, entry["sha512"])
    if archive.suffix == ".zip":
        with zipfile.ZipFile(archive) as source:
            for name in source.namelist():
                if not (target / name).resolve().is_relative_to(target.resolve()):
                    raise ValueError("Unsafe SDK archive path")
            source.extractall(target)
    else:
        # Official SDK tarballs contain safe relative symlinks. The data filter rejects escapes.
        # Python 3.12+ is required for the Unix installer, not for running the application.
        if sys.version_info < (3, 12):
            raise RuntimeError("SDK tar extraction requires Python 3.12+; alternatively install the pinned SDK using the official installer")
        with tarfile.open(archive) as source:
            source.extractall(target, filter="data")
    print("Project SDK ready:", target, manifest["version"])


def verify(path, expected):
    """分块校验完整归档，失败不解压到现有 SDK。"""
    digest = hashlib.sha512()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    if digest.hexdigest().lower() != expected.lower():
        raise RuntimeError("Official SDK SHA-512 mismatch: " + str(path))


if __name__ == "__main__":
    main()
