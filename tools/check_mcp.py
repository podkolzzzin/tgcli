"""Exercise a built tgcli executable without credentials or real client profiles.

python3 tools/check_mcp.py /absolute/path/to/tgcli [--native-install]
Native installation tests require claude/codex/copilot in PATH; absent CLIs are reported.
"""
import argparse
import json
import os
from pathlib import Path
import queue
import shutil
import subprocess
import tempfile
import threading

MODERN = "2026-07-28"


def protocol(executable, home, legacy=False, arguments=None):
    env = isolated_env(home)
    arguments = arguments or ["mcp", "--session", str(home / "session")]
    process = subprocess.Popen([executable, *arguments],
                               stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               text=True, encoding="utf-8", env=env)
    messages = queue.Queue()
    threading.Thread(target=lambda: [messages.put(line) for line in process.stdout], daemon=True).start()
    counter = 0

    def request(method, params, version=MODERN):
        nonlocal counter
        counter += 1
        if not legacy:
            params["_meta"] = {"io.modelcontextprotocol/protocolVersion": version,
                               "io.modelcontextprotocol/clientCapabilities": {}}
        process.stdin.write(json.dumps({"jsonrpc": "2.0", "id": counter, "method": method, "params": params}) + "\n")
        process.stdin.flush()
        try:
            response = json.loads(messages.get(timeout=20))
        except queue.Empty as exc:
            error = process.stderr.read() if process.poll() is not None else "process is still running"
            raise RuntimeError(("No MCP response", process.poll(), error)) from exc
        assert response.get("id") == counter and "method" not in response, response
        return response

    try:
        if legacy:
            result = request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                                           "clientInfo": {"name": "tgcli-smoke", "version": "1"}})["result"]
            assert result["protocolVersion"] == "2025-11-25"
            process.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
            process.stdin.flush()
        result = request("tools/list", {})["result"]
        assert len(result["tools"]) == 12
        assert ("ttlMs" in result) != legacy
        if not legacy:
            assert result["resultType"] == "complete"
            assert request("tools/list", {}, "2099-01-01")["error"]["code"] == -32022
        # No API credentials are provided: readiness must fail as a tool result,
        # without prompting for login or corrupting stdout.
        assert request("tools/call", {"name": "diagnostics"})["result"]["isError"]
        assert len(request("tools/list", {})["result"]["tools"]) == 12
        process.stdin.close()
        assert process.wait(timeout=20) == 0
        error = process.stderr.read()
        assert error == "", error
        print("PASS published stdio", "legacy 2025-11-25" if legacy else MODERN)
    finally:
        if process.poll() is None:
            process.kill()
            process.wait()


def isolated_env(home):
    env = os.environ.copy()
    env.update(HOME=str(home), USERPROFILE=str(home), XDG_CONFIG_HOME=str(home / ".config"),
               APPDATA=str(home / "AppData/Roaming"), LOCALAPPDATA=str(home / "AppData/Local"),
               CODEX_HOME=str(home / ".codex"), CLAUDE_CONFIG_DIR=str(home / ".claude"),
               COPILOT_HOME=str(home / ".copilot"), CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC="1")
    env["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = str(home / "bundle")
    for key in ("TGCLI_API_ID", "TGCLI_API_HASH"):
        env.pop(key, None)
    return env


def native_install(executable, home):
    env = isolated_env(home)
    for family in ("claude", "codex", "copilot"):
        if not shutil.which(family):
            print("SKIP native installer:", family, "is absent")
            continue
        Path(env[{"claude": "CLAUDE_CONFIG_DIR", "codex": "CODEX_HOME", "copilot": "COPILOT_HOME"}[family]]).mkdir(parents=True, exist_ok=True)
        command = [executable, "mcp", "install", family, "--session", str(home / "session with spaces")]
        for expected in ("installed", "already-installed"):
            result = subprocess.run(command, env=env, cwd=home, capture_output=True, text=True, encoding="utf-8", timeout=45)
            assert result.returncode == 0, (family, result.stdout, result.stderr)
            assert any(line.startswith(expected + "\t") for line in result.stdout.splitlines()), result.stdout
        print("PASS native installation and idempotency:", family)
        if family == "claude":
            result = subprocess.run([shutil.which("claude"), "mcp", "list"], env=env, cwd=home,
                                    capture_output=True, text=True, encoding="utf-8", timeout=45)
            assert result.returncode == 0 and "Connected" in result.stdout, (result.stdout, result.stderr)
            print("PASS Claude Code MCP connection health check")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("executable")
    parser.add_argument("--native-install", action="store_true")
    args = parser.parse_args()
    executable = str(Path(args.executable).resolve(strict=True))
    with tempfile.TemporaryDirectory(prefix="tgcli-mcp-smoke-") as root:
        home = Path(root)
        protocol(executable, home)
        protocol(executable, home, legacy=True)
        if args.native_install:
            native_install(executable, home)
