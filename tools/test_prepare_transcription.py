import hashlib
from pathlib import Path
import tempfile
import unittest
import zipfile

import prepare_transcription as assets


class PreparationTests(unittest.TestCase):
    def test_checksum_failure_preserves_previous_file_and_removes_partial_download(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source, target = root / "source", root / "target"
            source.write_bytes(b"new bytes")
            target.write_bytes(b"old bytes")
            with self.assertRaisesRegex(ValueError, "Checksum mismatch"):
                assets.download(source.as_uri(), target, "0" * 64)
            self.assertEqual(target.read_bytes(), b"old bytes")
            self.assertFalse(list(root.glob("*.download")))
            assets.download(source.as_uri(), target, hashlib.sha256(b"new bytes").hexdigest())
            self.assertEqual(target.read_bytes(), b"new bytes")
            # Verified cache works even when the original download is no longer available.
            source.unlink()
            assets.download(source.as_uri(), target, hashlib.sha256(b"new bytes").hexdigest())

    def test_extracts_only_decoder_and_license_without_archive_path_traversal(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            wheel = root / "decoder.whl"
            with zipfile.ZipFile(wheel, "w") as archive:
                archive.writestr("imageio_ffmpeg/binaries/ffmpeg-test", b"decoder")
                archive.writestr("pkg.dist-info/licenses/LICENSE", b"license")
                archive.writestr("../../outside", b"unexpected")
            assets.extract_decoder(wheel, root / "output", False)
            self.assertEqual((root / "output/ffmpeg").read_bytes(), b"decoder")
            self.assertEqual((root / "output/licenses/LICENSE").read_bytes(), b"license")
            self.assertFalse((root / "outside").exists())

    def test_missing_runtime_refuses_to_make_incomplete_bundle(self):
        with tempfile.TemporaryDirectory() as temporary:
            with self.assertRaisesRegex(FileNotFoundError, "Restore whisper.net.runtime"):
                assets.copy_runtime(temporary, Path(temporary) / "output", "linux-x64")

    def test_runtime_layout_maps_macos_and_preserves_noavx_on_linux(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for package, variant, platform in [
                ("whisper.net.runtime", "", "macos-x64"),
                ("whisper.net.runtime", "", "linux-x64"),
                ("whisper.net.runtime.noavx", "noavx", "linux-x64")]:
                source = root / package / "1.9.1/build" / platform
                source.mkdir(parents=True)
                (source / "libwhisper.so").write_bytes(b"native")
            assets.copy_runtime(root, root / "output", "linux-x64")
            self.assertEqual((root / "output/runtimes/noavx/linux-x64/libwhisper.so").read_bytes(), b"native")
            assets.copy_runtime(root, root / "mac-output", "osx-x64")
            self.assertEqual((root / "mac-output/runtimes/macos-x64/libwhisper.so").read_bytes(), b"native")


if __name__ == "__main__":
    unittest.main()
