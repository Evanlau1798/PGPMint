"""Small offline packaging checks; never publish or start hosted workflows."""
import hashlib
import os
from pathlib import Path
import plistlib
import subprocess
import sys
import tarfile
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts" / "packaging"))


class PackagingTests(unittest.TestCase):
    def setUp(self):
        import package
        self.package = package
        self.temp = tempfile.TemporaryDirectory(prefix="pgpmint-packaging-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def test_version_and_target_validation(self):
        self.assertEqual(self.package.version_numbers("v1.2.3-rc.1"), ("1.2.3-rc.1", "1.2.3.0"))
        for value in ("../../bad", "v1.0", "v1.2.3;bad", "v1.2.3\n"):
            with self.assertRaises(ValueError):
                self.package.version_numbers(value)

    def test_single_executable_gate_rejects_extra_files(self):
        (self.root / "PGPMint.exe").write_bytes(b"exe")
        self.assertEqual(self.package.single_executable(self.root).name, "PGPMint.exe")
        (self.root / "unexpected.dll").write_bytes(b"dll")
        with self.assertRaises(ValueError):
            self.package.single_executable(self.root)

    def test_macos_bundle_has_launcher_and_versioned_metadata(self):
        published = self.root / "published"
        published.mkdir()
        (published / "PGPMint.Desktop").write_bytes(b"app")
        (published / "libpgpmint_metal.dylib").write_bytes(b"native")
        chmod = Path.chmod
        with patch.object(Path, "chmod", autospec=True, side_effect=chmod) as modes:
            app = self.package.macos_app(published, self.root / "bundle", "v1.2.3-rc.1")
        with (app / "Contents" / "Info.plist").open("rb") as stream:
            info = plistlib.load(stream)
        self.assertEqual(info["CFBundleExecutable"], "PGPMint.Desktop")
        launcher = app / "Contents" / "MacOS" / "PGPMint"
        self.assertTrue(launcher.is_file())
        modes.assert_any_call(launcher, 0o755)
        if os.name != "nt":
            self.assertTrue(launcher.stat().st_mode & 0o111)
        self.assertIn('exec "$HERE/PGPMint.Desktop" "$@"', launcher.read_text())
        self.assertEqual(info["CFBundleShortVersionString"], "1.2.3")
        self.assertEqual(info["LSMinimumSystemVersion"], "14.0")
        self.assertEqual(info["CFBundlePackageType"], "APPL")
        self.assertTrue((app / "Contents/MacOS/libpgpmint_metal.dylib").is_file())

    def test_cross_built_linux_apphosts_have_execute_bits_even_when_source_has_none(self):
        stage = self.root / "linux"
        for name in ("cli/pgpmint", "desktop/PGPMint.Desktop"):
            executable = stage / name
            executable.parent.mkdir(parents=True, exist_ok=True)
            executable.write_bytes(b"apphost")
            executable.chmod(0o644)
        result = self.package.archive(stage, self.root / "cross-built", "linux-x64")
        with tarfile.open(result) as stream:
            for name in ("linux/cli/pgpmint", "linux/desktop/PGPMint.Desktop"):
                self.assertEqual(stream.getmember(name).mode & 0o777, 0o755)

    def test_tar_roundtrip_and_manifest(self):
        stage = self.root / "stage"
        stage.mkdir()
        launcher = stage / "pgpmint"
        launcher.write_text("#!/bin/sh\nexit 0\n")
        launcher.chmod(0o755)
        archive = self.package.archive(stage, self.root / "PGPMint-linux-x64", "linux-x64")
        with tarfile.open(archive) as stream:
            member = stream.getmember("stage/pgpmint")
            self.assertEqual(stream.extractfile(member).read(), launcher.read_bytes())
        manifest = self.package.write_checksums(self.root / "dist", [archive], "linux-x64")
        self.assertEqual(manifest.read_text().strip(),
                         hashlib.sha256(archive.read_bytes()).hexdigest() + "  " + archive.name)

    def test_moltenvk_digest_rejects_modified_download(self):
        import moltenvk
        source = self.root / "bad.tar"
        source.write_bytes(b"tampered archive")
        with self.assertRaisesRegex(ValueError, "SHA256"):
            moltenvk.unpack(source, self.root / "runtime")

    def test_macos_signs_nested_native_libraries_before_bundle(self):
        folder = self.root / "PGPMint.app"
        nested = folder / "Contents/MacOS"
        nested.mkdir(parents=True)
        (nested / "libcoreclr.dylib").write_bytes(b"macho")
        (nested / "PGPMint.Desktop").write_bytes(b"macho")
        with patch.object(self.package, "run") as run:
            self.package.sign_macos(folder)
        calls = [call.args[0] for call in run.call_args_list]
        self.assertTrue(any("libcoreclr.dylib" in str(arg) for call in calls for arg in call))
        self.assertEqual(calls[-1], ["codesign", "--verify", "--deep", "--strict", str(folder)])

    def test_all_six_bundle_layouts_without_running_dotnet(self):
        def fake_publish(project, output, rid, platform, tag, single=False):
            output.mkdir(parents=True)
            name = "PGPMint.exe" if single else "pgpmint" if project.endswith("Cli") else "PGPMint.Desktop"
            (output / name).write_bytes(b"fixture")
            if rid.startswith("osx-"):
                (output / "libpgpmint_metal.dylib").write_bytes(b"native")
        runtime = self.root / "moltenvk"
        (runtime / "licenses").mkdir(parents=True)
        (runtime / "licenses/LICENSE").write_text("license")
        (runtime / "libMoltenVK.dylib").write_bytes(b"fixture")
        with patch.object(self.package, "publish", side_effect=fake_publish), \
             patch.object(self.package, "install_moltenvk", return_value=runtime), \
             patch.object(self.package, "sign_macos"):
            for rid in self.package.RIDS:
                stage = self.package.package(rid, "v1.2.3", self.root / rid,
                                             "ARM64" if rid.endswith("arm64") else "x64")
                self.assertTrue((stage / "TESTING.txt").is_file())
                self.assertIn("NOT VERIFIED", (stage / "TESTING.txt").read_text())
                if rid.startswith("osx-"):
                    self.assertTrue((stage / "desktop/PGPMint.app/Contents/MacOS/libMoltenVK.dylib").is_file())
                    self.assertTrue((stage / "cli/licenses/LICENSE").is_file())
                dist = self.root / rid / "dist"
                manifest = dist / f"SHA256SUMS-{rid}.txt"
                self.assertTrue(manifest.is_file())
                for line in manifest.read_text().splitlines():
                    digest, name = line.split("  ")
                    self.assertEqual(hashlib.sha256((dist / name).read_bytes()).hexdigest(), digest)

    def test_ci_build_and_test_are_separate_and_cover_every_suite(self):
        import ci
        for host in ("win32", "linux", "darwin"):
            with patch.object(ci.sys, "platform", host), patch.object(ci, "run") as run:
                ci.build("ARM64")
                builds = [call.args[0] for call in run.call_args_list]
                self.assertTrue(all(command[1] == "build" for command in builds))
                self.assertEqual("PGPMint.sln" in builds[0], host == "win32")
                self.assertTrue(all(any(f"tests/{name}/{name}.csproj" in cmd for cmd in builds) for name in ci.TESTS))
            with patch.object(ci, "run") as run:
                ci.test("ARM64", self.root)
                commands = [call.args[0] for call in run.call_args_list]
                self.assertEqual(len(commands), 3)
                for command in commands:
                    self.assertEqual(command[1], "test")
                    self.assertIn("--no-build", command)
                    self.assertIn("Category!=HardwareGpu", command)

    def test_device_failure_keeps_stdout_and_stderr_for_diagnosis(self):
        import ci
        failure = subprocess.CalledProcessError(1, ["pgpmint"], output="partial inventory", stderr="driver failed")
        with patch.object(ci, "run", side_effect=failure), self.assertRaises(subprocess.CalledProcessError):
            ci.inventory(["pgpmint"], self.root, "devices")
        self.assertTrue((self.root / "devices.json").is_file(), "Failed CLI inventory must retain stdout")
        self.assertEqual((self.root / "devices.json").read_text(), "partial inventory")
        self.assertEqual((self.root / "devices-stderr.txt").read_text(), "driver failed")


if __name__ == "__main__":
    unittest.main()
