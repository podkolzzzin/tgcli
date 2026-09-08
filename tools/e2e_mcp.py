"""End-to-end tests for a published tgcli executable and isolated MCP clients."""

import argparse
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import tempfile
import tomllib

from check_mcp import isolated_env, protocol


def write(path, text):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")


def fake_client_source():
    return r'''import json
import os
from pathlib import Path
import sys

family = sys.argv[1]
args = sys.argv[2:]
separator = args.index("--")
command = args[separator + 1]
server_args = args[separator + 2:]
home = Path(os.environ["HOME"])
with (home / "fake-client-calls.jsonl").open("a", encoding="utf-8") as log:
    log.write(json.dumps({"family": family, "args": args}) + "\n")

if family == "claude":
    path = Path(os.environ["CLAUDE_CONFIG_DIR"]) / ".claude.json"
    root_key = "mcpServers"
elif family == "copilot":
    path = Path(os.environ["COPILOT_HOME"]) / "mcp-config.json"
    root_key = "mcpServers"
else:
    path = Path(os.environ["CODEX_HOME"]) / "config.toml"
    original = path.read_text(encoding="utf-8") if path.exists() else ""
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(original + "\n[mcp_servers.tgcli]\ncommand = " + json.dumps(command)
                    + "\nargs = " + json.dumps(server_args) + "\n", encoding="utf-8")
    raise SystemExit(0)

root = json.loads(path.read_text(encoding="utf-8")) if path.exists() else {}
entry = {"type": "stdio", "command": command, "args": server_args}
if family == "copilot":
    entry["tools"] = ["*"]
root.setdefault(root_key, {})["tgcli"] = entry
path.parent.mkdir(parents=True, exist_ok=True)
path.write_text(json.dumps(root, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
'''


def create_fake_clients(root):
    fake_bin = root / "fake-clients"
    fake_bin.mkdir()
    helper = fake_bin / "fake_mcp_client.py"
    write(helper, fake_client_source())
    for family in ("claude", "codex", "copilot"):
        if os.name == "nt":
            wrapper = fake_bin / f"{family}.cmd"
            write(wrapper, f'@echo off\r\n"{sys.executable}" "{helper}" {family} %*\r\n')
        else:
            wrapper = fake_bin / family
            write(wrapper, f'#!/bin/sh\nexec "{sys.executable}" "{helper}" {family} "$@"\n')
            wrapper.chmod(wrapper.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)
    return fake_bin


def client_paths(home):
    xdg = home / ".config"
    appdata = home / "AppData" / "Roaming"
    local = home / "AppData" / "Local"
    if sys.platform == "darwin":
        app_root = home / "Library" / "Application Support"
    elif os.name == "nt":
        app_root = appdata
    else:
        app_root = xdg
    return {
        "claude_cli": home / ".claude" / ".claude.json",
        "claude_desktop": app_root / "Claude" / "claude_desktop_config.json",
        "codex": home / ".codex" / "config.toml",
        "copilot_cli": home / ".copilot" / "mcp-config.json",
        "vscode": app_root / "Code" / "User" / "mcp.json",
        "vscode_profile": app_root / "Code" / "User" / "profiles" / "work" / "mcp.json",
        "insiders": app_root / "Code - Insiders" / "User" / "mcp.json",
        "insiders_profile": app_root / "Code - Insiders" / "User" / "profiles" / "work" / "mcp.json",
        "jetbrains": (local if os.name == "nt" else xdg) / "github-copilot" / "intellij" / "mcp.json",
        "xcode": home / ".config" / "github-copilot" / "xcode" / "mcp.json",
        "visual_studio": home / ".mcp.json",
        "visual_studio_signal": local / "Microsoft" / "VisualStudio",
        "eclipse": home / "eclipse workspace" / ".metadata" / ".plugins" / "org.eclipse.core.runtime" / ".settings" / "com.microsoft.copilot.eclipse.ui.prefs",
    }


def prepare_profiles(home, paths):
    originals = {}
    for name, path, content in (
        ("claude_cli", paths["claude_cli"], '{"keep":"claude-cli"}\n'),
        ("claude_desktop", paths["claude_desktop"], '{"keep":"claude-desktop"}\n'),
        ("codex", paths["codex"], '# keep\nmodel = "configured"\n'),
        ("copilot_cli", paths["copilot_cli"], '{"keep":"copilot-cli"}\n'),
        ("vscode", paths["vscode"], '{"keep":"vscode"}\n'),
        ("vscode_profile", paths["vscode_profile"], '{"keep":"vscode-profile"}\n'),
        ("insiders", paths["insiders"], '{"keep":"insiders"}\n'),
        ("insiders_profile", paths["insiders_profile"], '{"keep":"insiders-profile"}\n'),
        ("jetbrains", paths["jetbrains"], '{"keep":"jetbrains"}\n'),
        ("eclipse", paths["eclipse"], 'eclipse.preferences.version=1\nother=keep\n'),
    ):
        write(path, content)
        originals[name] = content
    for extension_root in (home / ".vscode" / "extensions", home / ".vscode-insiders" / "extensions"):
        (extension_root / "github.copilot-chat-e2e").mkdir(parents=True)
    if sys.platform == "darwin":
        write(paths["xcode"], '{"keep":"xcode"}\n')
        originals["xcode"] = '{"keep":"xcode"}\n'
    if os.name == "nt":
        paths["visual_studio_signal"].mkdir(parents=True)
        write(paths["visual_studio"], '{"keep":"visual-studio"}\n')
        originals["visual_studio"] = '{"keep":"visual-studio"}\n'
    return originals


def parse_statuses(output):
    statuses = []
    for line in output.splitlines():
        fields = line.split("\t", 3)
        if len(fields) == 4 and fields[0] in {"installed", "already-installed", "not-installed", "failed"}:
            statuses.append(fields)
    return statuses


def run_install(executable, family, env, workspace, expected):
    command = [executable, "mcp", "install", family, "--session", str(Path(env["HOME"]) / "session with spaces")]
    if family == "copilot":
        command += ["--eclipse-workspace", str(workspace)]
    result = subprocess.run(command, env=env, capture_output=True, text=True, encoding="utf-8", timeout=60)
    assert result.returncode == 0, (command, result.stdout, result.stderr)
    assert result.stderr == "", result.stderr
    statuses = parse_statuses(result.stdout)
    assert statuses and all(row[0] != "failed" for row in statuses), statuses
    assert sum(row[0] == expected for row in statuses) >= 1, (expected, statuses)
    opposite = "already-installed" if expected == "installed" else "installed"
    assert all(row[0] != opposite for row in statuses), statuses
    return statuses


def unescape_property(value):
    result = []
    index = 0
    escapes = {"n": "\n", "r": "\r", "t": "\t", "f": "\f"}
    while index < len(value):
        if value[index] != "\\":
            result.append(value[index])
            index += 1
            continue
        index += 1
        character = value[index]
        if character == "u":
            result.append(chr(int(value[index + 1:index + 5], 16)))
            index += 5
        else:
            result.append(escapes.get(character, character))
            index += 1
    return "".join(result)


def read_entry(path, root_key):
    if path.suffix == ".toml":
        return tomllib.loads(path.read_text(encoding="utf-8"))["mcp_servers"]["tgcli"]
    if path.suffix == ".prefs":
        line = next(line for line in path.read_text(encoding="utf-8").splitlines() if line.startswith("mcp="))
        return json.loads(unescape_property(line[4:]))[root_key]["tgcli"]
    return json.loads(path.read_text(encoding="utf-8"))[root_key]["tgcli"]


def verify_installations(executable, home, paths, originals):
    session = str((home / "session with spaces").resolve())
    expected_command = str(Path(executable).resolve())
    entries = {}
    for name, path in paths.items():
        if name.endswith("signal") or not path.is_file():
            continue
        root_key = "mcpServers" if name in {"claude_cli", "claude_desktop", "copilot_cli"} else "servers"
        entries[name] = read_entry(path, root_key)
        assert entries[name]["command"] == expected_command, (name, entries[name])
        assert entries[name]["args"] == ["mcp", "--session", session], (name, entries[name])
        if name in originals:
            backups = list(path.parent.glob(path.name + ".tgcli-backup-*"))
            assert len(backups) == 1, (name, backups)
            assert backups[0].read_text(encoding="utf-8") == originals[name]
    assert entries["copilot_cli"]["tools"] == ["*"]
    return entries


def verify_native_calls(home, executable):
    calls = [json.loads(line) for line in (home / "fake-client-calls.jsonl").read_text(encoding="utf-8").splitlines()]
    assert [call["family"] for call in calls] == ["claude", "codex", "copilot"], calls
    expected = str(Path(executable).resolve())
    for call in calls:
        separator = call["args"].index("--")
        prefix = (["mcp", "add", "--transport", "stdio", "--scope", "user", "tgcli", "--"]
                  if call["family"] == "claude" else ["mcp", "add", "tgcli", "--"])
        assert call["args"][:separator + 1] == prefix, call
        assert call["args"][separator + 1] == expected
        assert call["args"][separator + 2:] == ["mcp", "--session", str((home / "session with spaces").resolve())]


def run(executable):
    executable = str(Path(executable).resolve(strict=True))
    with tempfile.TemporaryDirectory(prefix="tgcli-mcp-e2e-") as temporary:
        home = Path(temporary) / "profile root Андрій"
        home.mkdir()
        env = isolated_env(home)
        env["PATH"] = str(create_fake_clients(Path(temporary)))
        paths = client_paths(home)
        originals = prepare_profiles(home, paths)
        workspace = home / "eclipse workspace"

        protocol(executable, home)
        protocol(executable, home, legacy=True)
        for family in ("claude", "codex", "copilot"):
            run_install(executable, family, env, workspace, "installed")
        entries = verify_installations(executable, home, paths, originals)
        assert len(entries) == (11 if sys.platform == "darwin" or os.name == "nt" else 10), entries.keys()
        verify_native_calls(home, executable)
        for family in ("claude", "codex", "copilot"):
            run_install(executable, family, env, workspace, "already-installed")
        verify_installations(executable, home, paths, originals)
        verify_native_calls(home, executable)

        # Start the server using a command persisted by a Desktop adapter.
        saved = entries["claude_desktop"]
        protocol(saved["command"], home, arguments=saved["args"])
        print(f"PASS isolated client installation E2E ({len(entries)} configurations)")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("executable")
    run(parser.parse_args().executable)
