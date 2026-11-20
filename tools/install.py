#!/usr/bin/env python3
"""Install a tagged tgcli release after verifying its checksum and startup."""
import argparse
import hashlib
import http.client
import os
from pathlib import Path
import platform
import re
import shutil
import subprocess
import sys
import tempfile
import urllib.parse
import urllib.request

REPOSITORY = "podkolzzzin/tgcli"
RELEASE_BASE = f"https://github.com/{REPOSITORY}/releases/download"
MANIFEST = "SHA256SUMS"
ASSETS = {
    "linux-x64": "tgcli-linux-x64",
    "osx-x64": "tgcli-osx-x64",
    "win-x64": "tgcli-win-x64.exe",
}
VERSION = re.compile(r"[0-9]+\.[0-9]+\.[0-9]+")


class ReleaseRedirectHandler(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        if req.type == "https" and urllib.parse.urlsplit(newurl).scheme != "https":
            raise ValueError("HTTPS release download redirected to a non-HTTPS URL")
        return super().redirect_request(req, fp, code, msg, headers, newurl)


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def parse_manifest(path):
    checksums = {}
    for line in path.read_text(encoding="ascii").splitlines():
        fields = re.fullmatch(r"([a-f0-9]{64})  ([a-zA-Z0-9_.-]+)", line)
        if fields is None:
            raise ValueError(f"Malformed checksum entry in {MANIFEST}")
        checksum, name = fields.groups()
        if name in checksums:
            raise ValueError(f"Duplicate checksum for {name} in {MANIFEST}")
        checksums[name] = checksum
    return checksums


def download(url, path, *, limit=None):
    try:
        opener = urllib.request.build_opener(ReleaseRedirectHandler())
        with opener.open(url, timeout=60) as response, path.open("xb") as output:
            if response.status != 200:
                raise ValueError(f"Unexpected HTTP status {response.status} for {path.name}")
            length = response.headers.get("Content-Length")
            written = 0
            while chunk := response.read(1024 * 1024):
                written += len(chunk)
                if limit is not None and written > limit:
                    raise ValueError(f"Download exceeds {limit} bytes for {path.name}")
                output.write(chunk)
            if not written or (length is not None and written != int(length)):
                raise ValueError(f"Incomplete download for {path.name}")
    except http.client.IncompleteRead as error:
        raise ValueError(f"Incomplete download for {path.name}") from error


def smoke(executable, version, home):
    env = os.environ.copy()
    for key in ("TGCLI_API_ID", "TGCLI_API_HASH", "TGCLI_NATIVE_DIR"):
        env.pop(key, None)
    env.update(HOME=str(home), USERPROFILE=str(home), XDG_CONFIG_HOME=str(home / "config"),
               XDG_DATA_HOME=str(home / "data"), APPDATA=str(home / "appdata"),
               LOCALAPPDATA=str(home / "localappdata"), DOTNET_BUNDLE_EXTRACT_BASE_DIR=str(home / "bundle"))
    for argument in ("--version", "--help"):
        try:
            result = subprocess.run([str(executable), argument], cwd=home, env=env, stdin=subprocess.DEVNULL,
                                    capture_output=True, text=True, encoding="utf-8", timeout=60, check=False)
        except (OSError, subprocess.TimeoutExpired) as error:
            raise ValueError(f"Cannot run candidate {argument}: {error}") from error
        expected = result.stdout.strip() == version if argument == "--version" else "usage" in result.stdout.lower()
        if result.returncode != 0 or result.stderr or not expected:
            check = "version" if argument == "--version" else "help smoke"
            actual = result.stderr.strip() or result.stdout.strip()
            raise ValueError(f"Candidate {check} check failed (exit {result.returncode}): {actual[:500]}")


def install(base_url, asset, version, destination):
    destination = destination.absolute()
    destination.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".tgcli-update-", dir=destination.parent) as temporary:
        staging = Path(temporary)
        manifest = staging / MANIFEST
        download(f"{base_url}/{MANIFEST}", manifest, limit=16 * 1024)
        checksum = parse_manifest(manifest).get(asset)
        if checksum is None:
            raise ValueError(f"{MANIFEST} has no checksum for {asset}")
        candidate = staging / asset
        download(f"{base_url}/{asset}", candidate)
        if digest(candidate) != checksum:
            raise ValueError(f"Checksum mismatch for {asset}")
        candidate.chmod(0o755)
        home = staging / "smoke"
        home.mkdir()
        smoke(candidate, version, home)
        if os.name == "nt":
            # A same-volume rename preserves the candidate's ACL. Create the
            # final file as a sibling so it inherits the destination directory's
            # ACL instead of keeping the administrator-only staging permissions.
            fd, sibling = tempfile.mkstemp(prefix=".tgcli-install-", dir=destination.parent)
            ready = Path(sibling)
            try:
                with os.fdopen(fd, "wb") as output, candidate.open("rb") as source:
                    shutil.copyfileobj(source, output)
                if digest(ready) != checksum:
                    raise ValueError(f"Checksum mismatch for staged {asset}")
                os.replace(ready, destination)
            finally:
                ready.unlink(missing_ok=True)
        else:
            os.replace(candidate, destination)


def host_rid():
    system = {"Linux": "linux", "Darwin": "osx", "Windows": "win"}.get(platform.system())
    if system and platform.machine().lower() in {"x86_64", "amd64"}:
        return f"{system}-x64"
    raise ValueError(f"No release binary for {platform.system()} {platform.machine()}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tag", required=True, help="Release tag, e.g. v8.0.0")
    parser.add_argument("--rid", required=True, choices=ASSETS)
    parser.add_argument("--destination", required=True, type=Path, help="Full path to the executable to install")
    options = parser.parse_args()
    version = options.tag.removeprefix("v")
    if not options.tag.startswith("v") or VERSION.fullmatch(version) is None:
        parser.error("--tag must be a release tag of the form vX.Y.Z")
    try:
        host = host_rid()
        if options.rid != host:
            raise ValueError(f"Requested {options.rid} on a {host} host")
        if host == "win-x64" and sys.version_info < (3, 13):
            raise ValueError("Windows installation requires Python 3.13 or later for private staging")
        install(f"{RELEASE_BASE}/{options.tag}", ASSETS[options.rid], version, options.destination)
    except (OSError, ValueError) as error:
        print(f"tgcli install: {error}", file=sys.stderr)
        return 1
    print(f"Installed tgcli {version} at {options.destination}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
