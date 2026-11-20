"""Exercise the installer with real loopback HTTP and a synthetic installation."""
from contextlib import contextmanager
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import os
import shlex
from pathlib import Path
import tempfile
from threading import Thread
import unittest
from urllib.request import Request

from install import ReleaseRedirectHandler, install

ASSET = "tgcli-win-x64.exe" if os.name == "nt" else "tgcli-linux-x64"
MANIFEST = "SHA256SUMS"
SENTINEL = b"previous installation\n"


@contextmanager
def serve(files):
    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            response = files.get(self.path.removeprefix("/"))
            if response is None:
                self.send_error(404)
                return
            data, length = response
            self.send_response(200)
            self.send_header("Content-Length", str(length))
            self.end_headers()
            self.wfile.write(data)

        def log_message(self, *args):
            pass

    with ThreadingHTTPServer(("127.0.0.1", 0), Handler) as server:
        runner = Thread(target=server.serve_forever, kwargs={"poll_interval": 0.01}, daemon=True)
        runner.start()
        try:
            yield f"http://127.0.0.1:{server.server_port}"
        finally:
            server.shutdown()
            runner.join()


def response(data):
    return data, len(data)


def checksum(data, asset=ASSET):
    return f"{hashlib.sha256(data).hexdigest()}  {asset}\n".encode()


class InstallerTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.destination = self.root / ("tgcli.exe" if os.name == "nt" else "tgcli")
        self.destination.write_bytes(SENTINEL)
        self.candidate = b"synthetic corrupt content"
        self.files = {MANIFEST: response(checksum(self.candidate)), ASSET: response(self.candidate)}

    def install(self):
        with serve(self.files) as url:
            install(url, ASSET, "8.0.0", self.destination)

    def assert_preserved(self, error):
        with self.assertRaisesRegex((OSError, ValueError), error):
            self.install()
        self.assertEqual(self.destination.read_bytes(), SENTINEL)
        self.assertEqual(list(self.root.iterdir()), [self.destination])

    def test_http_error_preserves_installed_binary(self):
        del self.files[ASSET]
        self.assert_preserved("404")

    def test_missing_manifest_preserves_installed_binary(self):
        del self.files[MANIFEST]
        self.assert_preserved("404")

    def test_truncated_manifest_preserves_installed_binary(self):
        data, length = self.files[MANIFEST]
        self.files[MANIFEST] = data, length + 10
        self.assert_preserved("Incomplete download.*SHA256SUMS")

    def test_truncated_binary_preserves_installed_binary(self):
        self.files[ASSET] = self.candidate[:-4], len(self.candidate)
        self.assert_preserved(f"Incomplete download.*{ASSET}")

    def test_checksum_mismatch_preserves_installed_binary(self):
        self.files[ASSET] = response(b"wrong contents")
        self.assert_preserved(f"Checksum mismatch.*{ASSET}")

    def test_manifest_without_candidate_preserves_installed_binary(self):
        self.files[MANIFEST] = response(checksum(self.candidate, "tgcli-osx-x64"))
        self.assert_preserved(f"has no checksum.*{ASSET}")

    def test_duplicate_manifest_entry_preserves_installed_binary(self):
        self.files[MANIFEST] = response(checksum(self.candidate) * 2)
        self.assert_preserved(f"Duplicate checksum.*{ASSET}")

    def test_unexecutable_candidate_preserves_installed_binary(self):
        self.assert_preserved("Cannot run candidate")

    def test_wrong_version_preserves_installed_binary(self):
        if os.name == "nt":
            # Use a system executable with no dependencies on Python DLLs.
            candidate = Path(os.environ["SystemRoot"]) / "System32" / "where.exe"
            self.serve_candidate(candidate.read_bytes())
        else:
            self.serve_candidate(b"#!/bin/sh\nprintf '7.0.1\\n'\n")
        self.assert_preserved("Candidate version check failed")

    @unittest.skipIf(os.name == "nt", "Shell fixture is Unix-only. CI installs a real packaged executable on Windows.")
    def test_failed_smoke_preserves_installed_binary(self):
        self.serve_candidate(b"#!/bin/sh\n[ \"$1\" = --version ] || exit 1\nprintf '8.0.0\\n'\n")
        self.assert_preserved("Candidate help smoke check failed")

    @unittest.skipIf(os.name == "nt", "Shell fixture is Unix-only. CI installs a real packaged executable on Windows.")
    def test_verified_candidate_replaces_installed_binary_as_executable(self):
        path = shlex.quote(str(self.destination))
        candidate = self.serve_candidate(f"""#!/bin/sh
[ "$(cat {path})" = "previous installation" ] || exit 1
case "$1" in
--version) echo 8.0.0;;
--help) echo 'Usage: tgcli';;
*) exit 1;;
esac
""".encode())
        self.install()
        self.assertEqual(self.destination.read_bytes(), candidate)
        self.assertEqual(self.destination.stat().st_mode & 0o777, 0o755)
        self.assertEqual(list(self.root.iterdir()), [self.destination])

    def serve_candidate(self, candidate):
        self.files[ASSET] = response(candidate)
        self.files[MANIFEST] = response(checksum(candidate))
        return candidate


class InstallerTransportTests(unittest.TestCase):
    def test_forbids_plaintext_redirect_from_github_https(self):
        request = Request("https://github.com/podkolzzzin/tgcli/releases/download/v8.0.0/SHA256SUMS")
        with self.assertRaisesRegex(ValueError, "HTTPS release download"):
            ReleaseRedirectHandler().redirect_request(request, None, 302, "Found", {}, "http://example.test/SHA256SUMS")


if __name__ == "__main__":
    unittest.main()
