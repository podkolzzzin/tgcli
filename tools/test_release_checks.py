"""Verify release inputs and the bytes downloaded back from the draft."""
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

SCRIPT = Path(__file__).with_name("release_checks.py").resolve()
INSTALLER = "https://raw.githubusercontent.com/podkolzzzin/tgcli/v8.0.0/tools/install.py"
INSTALL = f"""<!-- install:start -->
Linux x64:
```bash
install-linux {INSTALLER} --tag v8.0.0 --rid linux-x64
```
Windows x64:
```powershell
install-windows {INSTALLER} --tag v8.0.0 --rid win-x64
```
macOS Intel:
```bash
install-macos {INSTALLER} --tag v8.0.0 --rid osx-x64
```
<!-- install:end -->"""
ASSETS = ("tgcli-linux-x64", "tgcli-osx-x64", "tgcli-win-x64.exe")


class ReleaseChecksTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.notes = f"Changes for v8.0.0.\n\n{INSTALL}\n"
        (self.root / "CHANGELOG.md").write_text(f"# Changelog\n\n## 8.0.0\n\n{self.notes}\n## 7.0.1\n\nOld.\n")
        (self.root / "README.md").write_text(f"# tgcli\n\n{INSTALL}\n")
        (self.root / "TgCli.csproj").write_text("<Project><PropertyGroup><Version>8.0.0</Version></PropertyGroup></Project>")
        self.packages = self.root / "packages"
        self.packages.mkdir()
        for asset in ASSETS:
            (self.packages / asset).write_bytes(asset.encode())

    def check(self, action, *arguments, error=None):
        result = subprocess.run([sys.executable, str(SCRIPT), action, *map(str, arguments)], cwd=self.root,
                                capture_output=True, text=True, check=False)
        if error:
            self.assertNotEqual(result.returncode, 0)
            self.assertIn(error, result.stderr)
        else:
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        return result

    def test_generates_notes_from_only_current_entry_and_deterministic_checksums(self):
        self.check("prepare", "--tag", "v8.0.0", "--assets", self.packages, "--notes", "notes.md")
        self.assertEqual((self.root / "notes.md").read_text(), self.notes)
        manifest = "".join(f"{hashlib.sha256(name.encode()).hexdigest()}  {name}\n" for name in ASSETS)
        self.assertEqual((self.packages / "SHA256SUMS").read_bytes(), manifest.encode())

    def test_rejects_tag_project_version_mismatch(self):
        self.check("prepare", "--tag", "v8.0.1", "--assets", self.packages, "--notes", "notes.md",
                   error="Project version 8.0.0 does not match tag v8.0.1")
        self.assertFalse((self.root / "notes.md").exists())

    def test_rejects_tag_that_is_not_the_current_changelog_entry(self):
        changelog = self.root / "CHANGELOG.md"
        changelog.write_text(changelog.read_text().replace("## 8.0.0", "## 8.0.1"))
        self.check("prepare", "--tag", "v8.0.0", "--assets", self.packages, "--notes", "notes.md",
                   error="Top changelog entry must be 8.0.0")

    def test_rejects_stale_documentation_even_when_its_install_commands_match(self):
        for filename in ("README.md", "CHANGELOG.md"):
            doc = self.root / filename
            doc.write_text(doc.read_text().replace("/v8.0.0/tools/", "/v7.0.0/tools/"))
        self.check("prepare", "--tag", "v8.0.0", "--assets", self.packages, "--notes", "notes.md",
                   error="Install commands must use the tagged installer")

    def test_rejects_wrong_platform_install_line(self):
        for filename in ("README.md", "CHANGELOG.md"):
            doc = self.root / filename
            doc.write_text(doc.read_text().replace("--rid osx-x64", "--rid linux-x64"))
        self.check("prepare", "--tag", "v8.0.0", "--assets", self.packages, "--notes", "notes.md",
                   error="Install commands must cover linux-x64, win-x64 and osx-x64")

    def test_rejects_a_migration_link_to_another_release(self):
        changelog = self.root / "CHANGELOG.md"
        changelog.write_text(changelog.read_text().replace("Changes for v8.0.0.",
                             "[Migration](https://github.com/podkolzzzin/tgcli/blob/v7.0.0/docs/v8-migration.md)"))
        self.check("prepare", "--tag", "v8.0.0", "--assets", self.packages, "--notes", "notes.md",
                   error="Release link uses v7.0.0, expected v8.0.0")

    def test_requires_every_platform_artifact(self):
        (self.packages / "tgcli-osx-x64").unlink()
        self.check("prepare", "--tag", "v8.0.0", "--assets", self.packages, "--notes", "notes.md",
                   error="tgcli-osx-x64")

    def test_rejects_modified_draft_asset_even_with_matching_downloaded_manifest(self):
        self.check("prepare", "--tag", "v8.0.0", "--assets", self.packages, "--notes", "notes.md")
        retrieved = self.root / "downloaded"
        retrieved.mkdir()
        for asset in self.packages.iterdir():
            (retrieved / asset.name).write_bytes(asset.read_bytes())
        state = self.root / "release.json"
        state.write_text(json.dumps({"tagName": "v8.0.0", "isDraft": True, "body": self.notes.rstrip(),
                                     "assets": [{"name": name} for name in (*ASSETS, "SHA256SUMS")]}))
        args = ("verify", "--tag", "v8.0.0", "--assets", self.packages, "--notes", "notes.md",
                "--downloaded", retrieved, "--state", state)
        self.check(*args)
        wrong = b"another build"
        (retrieved / ASSETS[0]).write_bytes(wrong)
        self.check(*args, error="Checksum mismatch for downloaded draft asset tgcli-linux-x64")
        manifest = retrieved / "SHA256SUMS"
        manifest.write_text(manifest.read_text().replace(hashlib.sha256(ASSETS[0].encode()).hexdigest(),
                                                        hashlib.sha256(wrong).hexdigest()))
        self.check(*args, error="Downloaded SHA256SUMS does not match the build")


if __name__ == "__main__":
    unittest.main()
