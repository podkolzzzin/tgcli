#!/usr/bin/env python3
"""Build the self-contained tgcli executable with the same flags as CI."""
import argparse
from pathlib import Path
import subprocess
import sys

from install import ASSETS

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", choices=ASSETS, required=True)
    options = parser.parse_args()
    rid = options.rid
    commands = (
        ["dotnet", "restore", "TgCli.Tests/TgCli.Tests.csproj"],
        [sys.executable, "tools/prepare_transcription.py", "--rid", rid],
        ["dotnet", "publish", "TgCli.csproj", "-c", "Release", "-r", rid, "--self-contained", "true",
         "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
         "-p:PublishReadyToRun=true", "-p:EnableCompressionInSingleFile=false", "-p:StripSymbols=true",
         "-p:DebugType=none", "-p:DebugSymbols=false", "-o", f"artifacts/{rid}"],
    )
    for command in commands:
        result = subprocess.run(command, cwd=ROOT, check=False)
        if result.returncode:
            return result.returncode
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
