#!/usr/bin/env python3
"""Prepare pinned assets for embedding; Python and network are build-time only."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import tempfile
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
MODEL_REVISION = "5359861c739e955e79d9a303bcbc70fb988958b1"
MODEL_URL = f"https://huggingface.co/ggerganov/whisper.cpp/resolve/{MODEL_REVISION}/ggml-base.bin"
MODEL_SHA256 = "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe"
DECODERS = {
    "linux-x64": (
        "https://files.pythonhosted.org/packages/a0/2d/43c8522a2038e9d0e7dbdf3a61195ecc31ca576fb1527a528c877e87d973/imageio_ffmpeg-0.6.0-py3-none-manylinux2014_x86_64.whl",
        "c7e46fcec401dd990405049d2e2f475e2b397779df2519b544b8aab515195282"),
    "osx-x64": (
        "https://files.pythonhosted.org/packages/da/58/87ef68ac83f4c7690961bce288fd8e382bc5f1513860fc7f90a9c1c1c6bf/imageio_ffmpeg-0.6.0-py3-none-macosx_10_9_intel.macosx_10_9_x86_64.whl",
        "9d2baaf867088508d4a3458e61eeb30e945c4ad8016025545f66c4b5aaef0a61"),
    "win-x64": (
        "https://files.pythonhosted.org/packages/2c/c6/fa760e12a2483469e2bf5058c5faff664acf66cadb4df2ad6205b016a73d/imageio_ffmpeg-0.6.0-py3-none-win_amd64.whl",
        "02fa47c83703c37df6bfe4896aab339013f62bf02c5ebf2dce6da56af04ffc0a"),
}


def digest(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def download(url, destination, expected_hash):
    destination = Path(destination)
    if destination.exists() and digest(destination) == expected_hash:
        return
    destination.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary = tempfile.mkstemp(dir=destination.parent, suffix=".download")
    try:
        with os.fdopen(fd, "wb") as output, urllib.request.urlopen(url, timeout=120) as response:
            shutil.copyfileobj(response, output)
        if digest(temporary) != expected_hash:
            raise ValueError(f"Checksum mismatch for {destination.name}")
        os.replace(temporary, destination)
    finally:
        Path(temporary).unlink(missing_ok=True)


def extract_decoder(wheel, destination, windows):
    with zipfile.ZipFile(wheel) as archive:
        candidates = [p for p in archive.namelist()
                      if p.startswith("imageio_ffmpeg/binaries/ffmpeg-") and not p.endswith("/")]
        if len(candidates) != 1:
            raise ValueError("Expected exactly one bundled FFmpeg executable in the pinned wheel")
        target = Path(destination) / ("ffmpeg.exe" if windows else "ffmpeg")
        target.parent.mkdir(parents=True, exist_ok=True)
        with archive.open(candidates[0]) as source, target.open("wb") as output:
            shutil.copyfileobj(source, output)
        target.chmod(0o755)
        for entry in archive.namelist():
            if "license" in entry.lower() and not entry.endswith("/"):
                license_path = target.parent / "licenses" / Path(entry).name
                license_path.parent.mkdir(exist_ok=True)
                license_path.write_bytes(archive.read(entry))


def copy_runtime(nuget_root, destination, rid):
    native_rid = "macos-x64" if rid == "osx-x64" else rid
    packages = [("whisper.net.runtime", "")]
    if not rid.startswith("osx"):
        packages.append(("whisper.net.runtime.noavx", "noavx"))
    for package, variant in packages:
        package_root = Path(nuget_root) / package / "1.9.1"
        # Whisper.net 1.9.1 packages keep files under build/<rid>; their MSBuild
        # targets normally copy these into runtimes. We embed them ourselves.
        source = package_root / "build" / native_rid
        if not source.is_dir():
            raise FileNotFoundError(f"Restore {package} 1.9.1 first; no runtime for {native_rid} in {package_root}")
        if (source / "native").is_dir():
            source /= "native"
        target = Path(destination) / "runtimes"
        if variant:
            target /= variant
        target /= native_rid
        shutil.copytree(source, target, dirs_exist_ok=True)
        if not any("whisper" in p.name.lower() for p in target.rglob("*")):
            raise ValueError(f"No native Whisper library in {source}")


def prepare(rid, assets_root, nuget_root):
    assets_root = Path(assets_root)
    download(MODEL_URL, assets_root / "model.bin", MODEL_SHA256)
    url, checksum = DECODERS[rid]
    wheel = assets_root / "downloads" / f"ffmpeg-{rid}.whl"
    download(url, wheel, checksum)
    # Replace the complete RID directory so a previous runtime cannot leave stale libraries behind.
    assets_root.mkdir(parents=True, exist_ok=True)
    staging = Path(tempfile.mkdtemp(dir=assets_root, prefix=rid + "-"))
    try:
        extract_decoder(wheel, staging, rid.startswith("win"))
        copy_runtime(nuget_root, staging, rid)
        for source in (ROOT / "third_party" / "transcription").glob("*"):
            target = staging / "licenses" / source.name
            target.parent.mkdir(exist_ok=True)
            shutil.copyfile(source, target)
        manifest = {p.relative_to(staging).as_posix(): digest(p)
                    for p in sorted(staging.rglob("*")) if p.is_file()}
        (staging / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
        destination = assets_root / rid
        if destination.exists():
            shutil.rmtree(destination)
        staging.rename(destination)
    finally:
        if staging.exists():
            shutil.rmtree(staging)
    print(f"Prepared embedded transcription assets: {assets_root / rid}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", required=True, choices=DECODERS)
    parser.add_argument("--assets-root", type=Path, default=ROOT / ".transcription")
    parser.add_argument("--nuget-root", type=Path, default=Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget/packages")))
    args = parser.parse_args()
    prepare(args.rid, args.assets_root, args.nuget_root)


if __name__ == "__main__":
    main()
