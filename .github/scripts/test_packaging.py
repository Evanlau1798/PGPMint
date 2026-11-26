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

    def test_metal_compiler_uses_the_macos_language_standard(self):
        import ci
        with patch.dict(os.environ, {"RUNNER_TEMP": str(self.root)}), \
             patch.object(sys, "argv", ["ci.py", "metal", "--platform", "x64"]), \
             patch.object(ci, "run", return_value=subprocess.CompletedProcess([], 0, stdout="// MSL")) as run:
            ci.main()
        command = run.call_args_list[-1].args[0]
        self.assertEqual(command[:5], ["xcrun", "-sdk", "macosx", "metal", "-std=macos-metal2.3"])

    def test_version_and_target_validation(self):
        self.assertEqual(self.package.version_numbers("v1.2.3-rc.1"), ("1.2.3-rc.1", "1.2.3.0"))
        for value in ("../../bad", "v1.0", "v1.2.3;bad", "v1.2.3\n"):
            with self.assertRaises(ValueError):
                self.package.version_numbers(value)

    def test_macos_bundle_isolates_native_app_and_sidecar_host_with_relative_links(self):
        native_bin = self.root / "PGPMint"
        native_bin.write_bytes(b"\xcf\xfa\xed\xfeswiftapp")
        res_bundle = self.root / "PGPMintMacApp_PGPMintMacCore.bundle"
        res_bundle.mkdir()
        (res_bundle / "en-US.json").write_text("{}")

        host_published = self.root / "host-published"
        host_published.mkdir()
        (host_published / "pgpmint-host").write_bytes(b"\xcf\xfa\xed\xfeapphost")
        (host_published / "libpgpmint_metal.dylib").write_bytes(b"\xcf\xfa\xed\xfelibrary")
        (host_published / "Microsoft.CSharp.dll").write_bytes(b"MZmanaged")
        (host_published / "pgpmint-host.runtimeconfig.json").write_text("{}")

        app = self.package.macos_app(native_bin, [res_bundle], host_published, self.root / "bundle", "v1.2.3")
        contents = app / "Contents"
        self.assertTrue((contents / "MacOS/PGPMint").is_file())
        self.assertEqual(sorted(p.name for p in (contents / "MacOS").iterdir()), ["PGPMint"])
        self.assertTrue((contents / "Helpers/pgpmint-host").is_file())
        self.assertTrue((contents / "Frameworks/libpgpmint_metal.dylib").is_file())
        self.assertTrue((contents / "Resources/PGPMintMacApp_PGPMintMacCore.bundle/en-US.json").is_file())
        self.assertTrue((contents / "Resources/pgpmint-host/Microsoft.CSharp.dll").is_file())
        self.assertTrue((contents / "Resources/pgpmint-host/libpgpmint_metal.dylib").is_symlink())
        for name in ("Microsoft.CSharp.dll", "pgpmint-host.runtimeconfig.json", "libpgpmint_metal.dylib"):
            link = contents / "Helpers" / name
            self.assertTrue(link.is_symlink())
            self.assertFalse(link.readlink().is_absolute())
            self.assertTrue(link.resolve().is_relative_to(contents.resolve()))
        output = self.package.archive(app, self.root / "desktop", "osx-arm64")
        with tarfile.open(output) as stream:
            link = stream.getmember("PGPMint.app/Contents/Helpers/Microsoft.CSharp.dll")
            self.assertTrue(link.issym())
            self.assertEqual(link.linkname, "../Resources/pgpmint-host/Microsoft.CSharp.dll")

    def test_single_executable_gate_rejects_extra_files(self):
        (self.root / "PGPMint.exe").write_bytes(b"exe")
        self.assertEqual(self.package.single_executable(self.root).name, "PGPMint.exe")
        (self.root / "unexpected.dll").write_bytes(b"dll")
        with self.assertRaises(ValueError):
            self.package.single_executable(self.root)
        cli_dir = self.root / "cli"
        cli_dir.mkdir()
        (cli_dir / "pgpmint.exe").write_bytes(b"cli")
        self.assertEqual(self.package.single_executable(cli_dir, "pgpmint.exe").name, "pgpmint.exe")

    def test_macos_bundle_has_native_executable_and_versioned_metadata(self):
        native_bin = self.root / "PGPMint"
        native_bin.write_bytes(b"\xcf\xfa\xed\xfeswiftapp")
        host_published = self.root / "host-published"
        host_published.mkdir()
        (host_published / "pgpmint-host").write_bytes(b"\xcf\xfa\xed\xfehost")
        (host_published / "libpgpmint_metal.dylib").write_bytes(b"\xcf\xfa\xed\xfenative")
        chmod = Path.chmod
        with patch.object(Path, "chmod", autospec=True, side_effect=chmod) as modes:
            app = self.package.macos_app(native_bin, [], host_published, self.root / "bundle", "v1.2.3-rc.1")
        with (app / "Contents" / "Info.plist").open("rb") as stream:
            info = plistlib.load(stream)
        self.assertEqual(info["CFBundleExecutable"], "PGPMint")
        modes.assert_any_call(app / "Contents/MacOS/PGPMint", 0o755)
        modes.assert_any_call(app / "Contents/Helpers/pgpmint-host", 0o755)
        self.assertEqual(info["CFBundleShortVersionString"], "1.2.3")
        self.assertEqual(info["LSMinimumSystemVersion"], "14.0")
        self.assertEqual(info["CFBundlePackageType"], "APPL")
        self.assertEqual(info["CFBundleIconFile"], "AppIcon")
        self.assertTrue((app / "Contents/Resources/AppIcon.icns").is_file())
        self.assertTrue((app / "Contents/Helpers/libpgpmint_metal.dylib").is_file())

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

    def test_macos_signs_nested_native_libraries_and_helper_before_main_app(self):
        folder = self.root / "PGPMint.app"
        macos = folder / "Contents/MacOS"
        helpers = folder / "Contents/Helpers"
        frameworks = folder / "Contents/Frameworks"
        for d in (macos, helpers, frameworks):
            d.mkdir(parents=True)
        library = frameworks / "libcoreclr.dylib"
        library.write_bytes(b"\xcf\xfa\xed\xfemacho")
        (helpers / library.name).symlink_to(Path("..") / "Frameworks" / library.name)
        (helpers / "pgpmint-host").write_bytes(b"\xcf\xfa\xed\xfemacho")
        (macos / "PGPMint").write_bytes(b"\xcf\xfa\xed\xfemacho")
        with patch.object(self.package, "run") as run:
            self.package.sign_macos(folder)
        calls = [call.args[0] for call in run.call_args_list]
        self.assertTrue(any("libcoreclr.dylib" in str(arg) for call in calls for arg in call))
        signed = [command[-1] for command in calls[:-2]]
        self.assertEqual(signed, [library, helpers / "pgpmint-host", macos / "PGPMint"])
        self.assertEqual(calls[-1], ["codesign", "--verify", "--deep", "--strict", str(folder)])

    def test_all_six_bundle_layouts_without_running_dotnet(self):
        def fake_publish(project, output, rid, platform, tag, single=False, trimmed=True):
            output.mkdir(parents=True)
            if project.endswith("Cli"):
                name = "pgpmint.exe" if rid.startswith("win-") else "pgpmint"
                if rid.startswith("win-"):
                    self.assertTrue(single)
                    self.assertFalse(trimmed)
            elif single:
                name = "PGPMint.exe"
                self.assertTrue(trimmed)
            elif project.endswith("Host"):
                name = "pgpmint-host"
            else:
                name = "PGPMint.Desktop"
            (output / name).write_bytes(b"\xcf\xfa\xed\xfefixture" if rid.startswith("osx-") else b"fixture")
            if rid.startswith("osx-") and not single:
                (output / "libpgpmint_metal.dylib").write_bytes(b"\xcf\xfa\xed\xfenative")

        def fake_swift_build(rid, scratch_build):
            scratch_build.mkdir(parents=True, exist_ok=True)
            binary = scratch_build / "PGPMint"
            binary.write_bytes(b"\xcf\xfa\xed\xfeswift")
            bundle = scratch_build / "PGPMintMacApp_PGPMintMacCore.bundle"
            bundle.mkdir()
            (bundle / "en-US.json").write_text("{}")
            return binary, [bundle]

        runtime = self.root / "moltenvk"
        (runtime / "licenses").mkdir(parents=True)
        (runtime / "licenses/LICENSE").write_text("license")
        (runtime / "libMoltenVK.dylib").write_bytes(b"\xca\xfe\xba\xbefixture")
        with patch.object(self.package, "publish", side_effect=fake_publish), \
             patch.object(self.package, "build_macos_native_app", side_effect=fake_swift_build), \
             patch.object(self.package, "install_moltenvk", return_value=runtime), \
             patch.object(self.package, "sign_macos"):
            for rid in self.package.RIDS:
                platform_name = self.package.release_platform_name(rid)
                stage = self.package.package(rid, "v1.2.3", self.root / rid,
                                             "ARM64" if rid.endswith("arm64") else "x64")
                dist = self.root / rid / "dist"
                manifest = dist / f"SHA256SUMS-{platform_name}.txt"
                self.assertTrue(manifest.is_file())
                self.assertFalse(any("osx-" in p.name or p.name.startswith("TESTING-") or p.suffix == ".zip" for p in dist.iterdir()))
                if rid.startswith("win-"):
                    self.assertTrue((dist / f"PGPMint-v1.2.3-{platform_name}.exe").is_file())
                    self.assertTrue((dist / f"PGPMint-cli-v1.2.3-{platform_name}.exe").is_file())
                else:
                    self.assertTrue((stage / "TESTING.txt").is_file())
                    self.assertIn("NOT VERIFIED", (stage / "TESTING.txt").read_text())
                    self.assertTrue((dist / f"PGPMint-v1.2.3-{platform_name}.tar.gz").is_file())
                    if rid.startswith("osx-"):
                        self.assertTrue((stage / "desktop/PGPMint.app/Contents/MacOS/PGPMint").is_file())
                        self.assertTrue((stage / "desktop/PGPMint.app/Contents/Resources/AppIcon.icns").is_file())
                        self.assertTrue((stage / "desktop/PGPMint.app/Contents/Helpers/pgpmint-host").is_file())
                        self.assertTrue((stage / "desktop/PGPMint.app/Contents/Helpers/libMoltenVK.dylib").is_file())
                        self.assertTrue((stage / "cli/licenses/LICENSE").is_file())
                    else:
                        self.assertTrue((stage / "desktop/pgpmint.svg").is_file())
                        self.assertTrue((stage / "desktop/pgpmint.png").is_file())
                for line in manifest.read_text().splitlines():
                    digest, name = line.split("  ")
                    self.assertEqual(hashlib.sha256((dist / name).read_bytes()).hexdigest(), digest)

    def test_ci_build_and_test_are_separate_and_cover_every_suite(self):
        import ci
        for host in ("win32", "linux", "darwin"):
            with patch.object(ci.sys, "platform", host), patch.object(ci, "run") as run:
                ci.build("ARM64")
                builds = [call.args[0] for call in run.call_args_list]
                dotnet_builds = [cmd for cmd in builds if cmd[0] == "dotnet"]
                self.assertTrue(all(command[1] == "build" for command in dotnet_builds))
                self.assertEqual("PGPMint.sln" in dotnet_builds[0], host == "win32")
                self.assertTrue(all(any(f"tests/{name}/{name}.csproj" in cmd for cmd in dotnet_builds) for name in ci.TESTS))
                self.assertEqual(any(cmd[0] == "swift" for cmd in builds), host == "darwin")
            with patch.object(ci.sys, "platform", host), patch.object(ci, "run") as run:
                ci.test("ARM64", self.root)
                commands = [call.args[0] for call in run.call_args_list]
                dotnet_tests = [cmd for cmd in commands if cmd[0] == "dotnet"]
                self.assertEqual(len(dotnet_tests), len(ci.TESTS))
                for command in dotnet_tests:
                    self.assertEqual(command[1], "test")
                    self.assertIn("--no-build", command)
                    self.assertIn("Category!=HardwareGpu", command)
                self.assertEqual(any(cmd[0] == "swift" and cmd[1] == "test" for cmd in commands), host == "darwin")

    def test_device_failure_keeps_stdout_and_stderr_for_diagnosis(self):
        import ci
        failure = subprocess.CalledProcessError(1, ["pgpmint"], output="partial inventory", stderr="driver failed")
        with patch.object(ci, "run", side_effect=failure), self.assertRaises(subprocess.CalledProcessError):
            ci.inventory(["pgpmint"], self.root, "devices")
        self.assertTrue((self.root / "devices.json").is_file(), "Failed CLI inventory must retain stdout")
        self.assertEqual((self.root / "devices.json").read_text(), "partial inventory")
        self.assertEqual((self.root / "devices-stderr.txt").read_text(), "driver failed")

    def test_macapp_localization_resources_match_canonical_strings(self):
        mac_res = ROOT / "src/PGPMint.MacApp/Sources/PGPMintMacCore/Resources"
        pairs = [
            (ROOT / "src/PGPMint.App/Strings/en-US.json", mac_res / "en-US.json"),
            (ROOT / "src/PGPMint.App/Strings/zh-Hant.json", mac_res / "zh-Hant.json"),
            (ROOT / "src/PGPMint.Desktop/Strings/Desktop.en-US.json", mac_res / "Desktop.en-US.json"),
            (ROOT / "src/PGPMint.Desktop/Strings/Desktop.zh-Hant.json", mac_res / "Desktop.zh-Hant.json"),
        ]
        for canonical, copy_path in pairs:
            self.assertEqual(copy_path.read_bytes(), canonical.read_bytes(), f"Drift detected in {copy_path.name}")


if __name__ == "__main__":
    unittest.main()
