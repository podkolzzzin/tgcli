#!/usr/bin/env python3
"""Prepare and verify a release using only the artifacts gated by CI."""
import argparse
import json
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

from install import ASSETS, MANIFEST, REPOSITORY, VERSION, digest, parse_manifest


def project_version():
    version = ET.parse("TgCli.csproj").findtext("./PropertyGroup/Version", default="").strip()
    if VERSION.fullmatch(version) is None:
        raise ValueError(f"Invalid TgCli.csproj Version: {version!r}")
    return version


def installation_block(text, tag):
    match = re.search(r"^<!-- install:start -->\n(.*?)^<!-- install:end -->$", text, re.MULTILINE | re.DOTALL)
    if match is None:
        raise ValueError("Missing the versioned installation block")
    block = match[1]
    snippets = re.findall(r"^```(bash|powershell)\n(.*?)^```$", block, re.MULTILINE | re.DOTALL)
    urls = re.findall(r"https?://[^\s\"']+", block)
    url = f"https://raw.githubusercontent.com/{REPOSITORY}/{tag}/tools/install.py"
    if urls != [url] * len(ASSETS):
        raise ValueError(f"Install commands must use the tagged installer {url}")
    expected = [("bash", "linux-x64"), ("powershell", "win-x64"), ("bash", "osx-x64")]
    if len(snippets) != len(expected) or any(
        language != shell or url not in command or f"--tag {tag} --rid {rid} " not in command.replace("\n", " ") + " "
        for (language, command), (shell, rid) in zip(snippets, expected)
    ):
        raise ValueError("Install commands must cover linux-x64, win-x64 and osx-x64 with the current tag")
    return block


def release_notes(tag):
    version = project_version()
    if tag != f"v{version}":
        raise ValueError(f"Project version {version} does not match tag {tag}")
    changelog = Path("CHANGELOG.md").read_text(encoding="utf-8")
    headings = list(re.finditer(r"^## (.+)$", changelog, re.MULTILINE))
    if not headings or headings[0][1].split()[0] != version:
        raise ValueError(f"Top changelog entry must be {version}")
    end = headings[1].start() if len(headings) > 1 else len(changelog)
    notes = changelog[headings[0].end():end].strip() + "\n"
    readme = Path("README.md").read_text(encoding="utf-8")
    if installation_block(notes, tag) != installation_block(readme, tag):
        raise ValueError("The install commands in the changelog and README differ")
    repo = re.escape(REPOSITORY)
    tagged_links = (rf"https://(?:github\.com/{repo}/(?:blob|tree|releases/(?:download|tag))"
                    rf"|raw\.githubusercontent\.com/{repo})/(v[0-9]+\.[0-9]+\.[0-9]+)\b")
    for linked_tag in re.findall(tagged_links, notes):
        if linked_tag != tag:
            raise ValueError(f"Release link uses {linked_tag}, expected {tag}")
    return notes


def prepare(tag, assets, notes):
    body = release_notes(tag)
    lines = []
    for name in sorted(ASSETS.values()):
        asset = assets / name
        if not asset.is_file() or asset.stat().st_size == 0:
            raise ValueError(f"Missing or empty tested artifact {name}")
        lines.append(f"{digest(asset)}  {name}\n")
    (assets / MANIFEST).write_text("".join(lines), encoding="ascii", newline="\n")
    notes.write_text(body, encoding="utf-8", newline="\n")


def verify(tag, assets, notes, downloaded, state):
    release = json.loads(state.read_text(encoding="utf-8"))
    if release.get("tagName") != tag or release.get("isDraft") is not True:
        raise ValueError(f"Expected a draft for tag {tag}")
    if release.get("body", "").strip() != notes.read_text(encoding="utf-8").strip():
        raise ValueError("Draft release body differs from generated notes")
    expected = {*ASSETS.values(), MANIFEST}
    attached = [asset["name"] for asset in release["assets"]]
    if len(attached) != len(expected) or set(attached) != expected:
        raise ValueError(f"Draft asset names differ from expected: {attached}")
    if (downloaded / MANIFEST).read_bytes() != (assets / MANIFEST).read_bytes():
        raise ValueError(f"Downloaded {MANIFEST} does not match the build")
    checksums = parse_manifest(assets / MANIFEST)
    for name in ASSETS.values():
        if digest(downloaded / name) != checksums[name]:
            raise ValueError(f"Checksum mismatch for downloaded draft asset {name}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="action", required=True)
    for action in ("prepare", "verify"):
        command = commands.add_parser(action)
        command.add_argument("--tag", required=True)
        command.add_argument("--assets", required=True, type=Path)
        command.add_argument("--notes", required=True, type=Path)
    command.add_argument("--downloaded", required=True, type=Path)
    command.add_argument("--state", required=True, type=Path)
    options = parser.parse_args()
    try:
        if options.action == "prepare":
            prepare(options.tag, options.assets, options.notes)
        else:
            verify(options.tag, options.assets, options.notes, options.downloaded, options.state)
    except (KeyError, OSError, ValueError, ET.ParseError) as error:
        print(f"Release checks: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
