#!/usr/bin/env python3
"""Exercise the packaged CLI and installer in an isolated, credential-free profile."""
import argparse
from functools import partial
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
import json
import os
import shutil
from pathlib import Path
import subprocess
import tempfile
from threading import Thread
import unittest

from check_mcp import isolated_env
from install import ASSETS, MANIFEST, digest, host_rid, install
from release_checks import project_version

COMMANDS = (
    "bot create", "bot list", "bot remove", "bot token", "bot write",
    "channel comments", "channel metrics",
    "chat context", "chat export", "chat list", "chat messages", "chat migrations", "chat resolve", "chat search", "chat stats",
    "diagnostics", "download", "download-batch", "forum topics", "link message", "login",
    "mcp", "mcp install", "message get", "message transcribe", "search",
    "session export", "session import", "session status", "session unlock", "transcribe",
)
GROUPS = ("bot", "channel", "chat", "forum", "link", "message", "session")


class QuietHandler(SimpleHTTPRequestHandler):
    def log_message(self, *args):
        pass


class PackagedCliTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="tgcli-cli-e2e-")
        self.addCleanup(temporary.cleanup)
        self.home = Path(temporary.name) / "isolated profile"
        self.home.mkdir()
        self.env = isolated_env(self.home)
        self.env.update(XDG_DATA_HOME=str(self.home / "data"), PATH="")
        self.env.pop("TGCLI_NATIVE_DIR", None)
        self.session = self.home / "telegram session"

    def run_cli(self, *args, code=0, structured_error=False):
        result = subprocess.run([str(self.executable), *map(str, args)], cwd=self.home, env=self.env,
                                stdin=subprocess.DEVNULL, capture_output=True, text=True, encoding="utf-8",
                                timeout=60, check=False)
        self.assertEqual(result.returncode, code, (args, result.stdout, result.stderr))
        if code == 0:
            self.assertEqual(result.stderr, "", (args, result.stderr))
        else:
            if not structured_error:
                self.assertEqual(result.stdout, "", (args, result.stdout))
            self.assertTrue(result.stderr.strip(), args)
            self.assertNotIn("Unhandled exception", result.stderr)
            self.assertNotIn("   at ", result.stderr)
        return result

    def assert_no_session_created(self):
        self.assertFalse(self.session.exists())
        for pattern in ("config.json", "tdlib-db", "tdlib-files", "tdlib.lock", "tdlib.lock.owner"):
            self.assertEqual(list(self.home.rglob(pattern)), [], pattern)

    def test_help_tree_and_version_are_session_free(self):
        self.assertEqual(self.run_cli("--version").stdout.strip(), self.version)
        usage = self.run_cli("--help").stdout
        for name in sorted(set(command.split()[0] for command in COMMANDS)):
            self.assertRegex(usage, rf"(?m)^\s+{name}\b")
        for command in (*GROUPS, *COMMANDS):
            with self.subTest(command=command):
                self.assertIn("Usage:", self.run_cli(*command.split(), "--help").stdout)
        self.assert_no_session_created()

    def test_invalid_usage_and_values_leave_no_session(self):
        session = ("--session", self.session)
        cases = (
            ("no-such-tgcli-command",),
            ("chat", "messages", "--no-such-option"),
            ("chat", "messages", "--chat-id", "not-an-integer", *session),
            ("chat", "messages", "--format", "json", *session),
            ("chat", "messages", "--chat-id", "42", "--format", "invalid", *session),
            ("chat", "messages", "--chat-id", "42", "--request-timeout", "0", *session),
            ("channel", "comments", "--chat-id", "42", *session),
            ("bot", "remove", "--username", "synthetic_fixture_bot", *session),
            ("download-batch", "--input", self.home / "requests.jsonl", "--parallel", "0", *session),
            ("transcribe", "--input", self.home / "speech.wav", "--format", "invalid"),
            ("session", "unlock", *session),
            ("search", "--query", "synthetic", "--server", *session),
            ("chat", "messages", "--chat-id", "42", "--schema", "simple", *session),
            ("chat", "export", "--chat-id", "42", "--all-history", *session),
        )
        for args in cases:
            with self.subTest(args=args):
                try:
                    self.run_cli(*args, code=2)
                finally:
                    self.assert_no_session_created()

    def test_session_status_emits_only_one_json_object(self):
        result = self.run_cli("session", "status", "--format", "json", "--session", self.session)
        payload = json.loads(result.stdout)
        self.assertIs(payload["locked"], False)
        self.assertIsNone(payload["owner"])
        self.assert_no_session_created()

    def test_diagnostics_without_credentials_explains_login_requirement(self):
        result = self.run_cli("diagnostics", "--format", "json", "--session", self.session,
                              code=4, structured_error=True)
        payload = json.loads(result.stdout)
        self.assertIs(payload["healthy"], False)
        self.assertEqual(payload["error_code"], "login_required")
        self.assertRegex(result.stderr.lower(), r"login|auth|api[_ -]?id")

    def test_installer_downloads_and_runs_the_packaged_executable(self):
        self.assertEqual(host_rid(), self.rid)
        snapshot = self.home / "release"
        snapshot.mkdir()
        alias = snapshot / ASSETS[self.rid]
        # CI tests the final release asset by name. A manually supplied binary
        # may have its unrenamed project output name or be on another filesystem.
        executable_hash = digest(self.executable)
        shutil.copyfile(self.executable, alias)
        (snapshot / MANIFEST).write_text(f"{executable_hash}  {alias.name}\n", encoding="ascii")
        destination = self.home / "installed" / ("tgcli.exe" if self.rid == "win-x64" else "tgcli")
        destination.parent.mkdir()
        if self.rid == "win-x64":
            # A parent ACE absent from the private download directory must reach
            # the installed executable. Use a SID independent of Windows locale.
            system = Path(os.environ["SystemRoot"]) / "System32"
            subprocess.run([str(system / "icacls.exe"), str(destination.parent),
                            "/grant", "*S-1-5-11:(OI)(CI)(RX)"], check=True, timeout=30)
        destination.write_text("previous synthetic installation", encoding="ascii")
        handler = partial(QuietHandler, directory=snapshot)
        with ThreadingHTTPServer(("127.0.0.1", 0), handler) as http:
            server = Thread(target=http.serve_forever, daemon=True)
            server.start()
            try:
                install(f"http://127.0.0.1:{http.server_port}", alias.name, self.version, destination)
            finally:
                http.shutdown()
                server.join()
        self.assertEqual(digest(destination), executable_hash)
        self.assertEqual(list(destination.parent.iterdir()), [destination])
        if self.rid == "win-x64":
            powershell = system / "WindowsPowerShell" / "v1.0" / "powershell.exe"
            acl = subprocess.run([str(powershell), "-NoProfile", "-NonInteractive", "-Command",
                                  "(Get-Acl -LiteralPath $env:TGCLI_ACL_FILE).Access | ForEach-Object { "
                                  "$_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value }"],
                                 env=dict(os.environ, TGCLI_ACL_FILE=str(destination)), text=True,
                                 capture_output=True, check=True, timeout=30)
            self.assertIn("S-1-5-11", acl.stdout.splitlines())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    parser.add_argument("--rid", required=True, choices=ASSETS)
    options = parser.parse_args()
    PackagedCliTests.executable = options.binary.resolve(strict=True)
    PackagedCliTests.version = project_version()
    PackagedCliTests.rid = options.rid
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(PackagedCliTests)
    return int(not unittest.TextTestRunner(verbosity=2).run(suite).wasSuccessful())


if __name__ == "__main__":
    raise SystemExit(main())
