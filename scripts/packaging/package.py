"""Publish self-contained software into an explicitly selected scratch directory."""
import argparse
import hashlib
from pathlib import Path
import plistlib
import re
import shutil
import subprocess
import tarfile
import zipfile

from moltenvk import install as install_moltenvk

ROOT = Path(__file__).resolve().parents[2]
RIDS = ("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-arm64", "osx-x64")
MACAPP_DIR = ROOT / "src" / "PGPMint.MacApp"


def run(args, **kwargs):
    print("+", " ".join(map(str, args)), flush=True)
    return subprocess.run(list(map(str, args)), check=True, **kwargs)


def version_numbers(tag):
    match = re.fullmatch(r"v(\d+\.\d+\.\d+)(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?", tag)
    if not match:
        raise ValueError("Expected vMAJOR.MINOR.PATCH[-prerelease]")
    return tag[1:], match[1] + ".0"


def single_executable(folder):
    files = sorted(path for path in folder.rglob("*") if path.is_file())
    expected = folder / "PGPMint.exe"
    if files != [expected]:
        raise ValueError(f"WinUI single-executable gate failed: {files}")
    return expected


def publish(project, output, rid, platform, tag, single=False):
    version, file_version = version_numbers(tag)
    args = ["dotnet", "publish", str(ROOT / "src" / project / (project + ".csproj")),
            "-c", "Release", "-r", rid, "--self-contained", "true", f"-p:Platform={platform}",
            f"-p:Version={version}", f"-p:FileVersion={file_version}",
            f"-p:AssemblyVersion={file_version}", "-p:DebugType=none", "-p:DebugSymbols=false",
            "-o", str(output)]
    if single:
        args += ["-p:PublishSingleFile=true", "-p:PublishTrimmed=true", "-p:TrimMode=partial",
                 "-p:EnableCompressionInSingleFile=true", "-p:IncludeAllContentForSelfExtract=true",
                 "-p:IncludeNativeLibrariesForSelfExtract=true",
                 "-p:AllowedReferenceRelatedFileExtensions=.none"]
    else:
        args += ["-p:PublishSingleFile=false", "-p:PublishTrimmed=false"]
    run(args, cwd=ROOT)


def build_macos_native_app(rid, scratch_build):
    arch = "arm64" if rid == "osx-arm64" else "x86_64"
    run(["swift", "build", "--package-path", str(MACAPP_DIR), "-c", "release",
         "--arch", arch, "--scratch-path", str(scratch_build)], cwd=ROOT)
    bin_result = run(["swift", "build", "--package-path", str(MACAPP_DIR), "-c", "release",
                      "--arch", arch, "--scratch-path", str(scratch_build), "--show-bin-path"],
                     cwd=ROOT, capture_output=True, text=True)
    bin_dir = Path(bin_result.stdout.strip())
    binary = bin_dir / "PGPMint"
    if not binary.is_file():
        raise ValueError(f"Missing built macOS native binary: {binary}")
    bundles = sorted(bin_dir.glob("*.bundle"))
    return binary, bundles


def is_macho(path):
    if not path.is_file():
        return False
    with path.open("rb") as stream:
        magic = stream.read(4)
    return magic in (b"\xfe\xed\xfa\xce", b"\xce\xfa\xed\xfe", b"\xfe\xed\xfa\xcf", b"\xcf\xfa\xed\xfe",
                     b"\xca\xfe\xba\xbe", b"\xbe\xba\xfe\xca", b"\xca\xfe\xba\xbf", b"\xbf\xba\xfe\xca")


def macos_app(native_binary, resource_bundles, host_published, parent, tag):
    app = parent / "PGPMint.app"
    contents = app / "Contents"
    contents.mkdir(parents=True)
    for directory in ("MacOS", "Helpers", "Frameworks", "Resources"):
        (contents / directory).mkdir()

    # Pure native SwiftUI executable in Contents/MacOS.
    target_bin = contents / "MacOS" / "PGPMint"
    shutil.copy2(native_binary, target_bin)
    target_bin.chmod(0o755)

    for bundle in resource_bundles:
        dest = contents / "Resources" / bundle.name
        if bundle.is_dir():
            shutil.copytree(bundle, dest)
        else:
            shutil.copy2(bundle, dest)

    # Apple code locations require Mach-O; keep managed .NET host payload in Resources/pgpmint-host
    # and native libraries in Frameworks, linked via relative symlinks beside Contents/Helpers/pgpmint-host.
    host_resources = contents / "Resources" / "pgpmint-host"
    host_resources.mkdir()
    for item in sorted(host_published.iterdir()):
        if is_macho(item):
            if item.suffix == ".dylib":
                dest = contents / "Frameworks" / item.name
                shutil.move(str(item), dest)
                (contents / "Helpers" / item.name).symlink_to(Path("..") / "Frameworks" / item.name)
                (host_resources / item.name).symlink_to(Path("../..") / "Frameworks" / item.name)
            else:
                dest = contents / "Helpers" / item.name
                shutil.move(str(item), dest)
                dest.chmod(0o755)
                (host_resources / item.name).symlink_to(Path("../..") / "Helpers" / item.name)
        else:
            dest = host_resources / item.name
            shutil.move(str(item), dest)
            (contents / "Helpers" / item.name).symlink_to(
                Path("..") / "Resources" / "pgpmint-host" / item.name,
                target_is_directory=dest.is_dir(),
            )
    host_published.rmdir()

    app_icon = MACAPP_DIR / "Resources" / "AppIcon.icns"
    if app_icon.is_file():
        shutil.copy2(app_icon, contents / "Resources" / "AppIcon.icns")

    info = {
        "CFBundleIdentifier": "org.pgpmint.desktop", "CFBundleName": "PGPMint",
        "CFBundleDisplayName": "PGPMint", "CFBundlePackageType": "APPL",
        "CFBundleExecutable": "PGPMint", "CFBundleShortVersionString": tag[1:].split("-")[0],
        "CFBundleVersion": tag[1:].split("-")[0], "LSMinimumSystemVersion": "14.0",
        "NSHighResolutionCapable": True,
    }
    if app_icon.is_file():
        info["CFBundleIconFile"] = "AppIcon"
    with (contents / "Info.plist").open("wb") as stream:
        plistlib.dump(info, stream)
    return app


def sign_macos(folder):
    # Sign inside-out after all payload changes: dylibs -> helper executables -> main executable -> bundle.
    binaries = [path for path in folder.rglob("*") if not path.is_symlink() and is_macho(path)]
    for binary in sorted(binaries, key=lambda path: (path.name == "PGPMint", path.suffix != ".dylib", str(path))):
        run(["codesign", "--force", "--sign", "-", "--timestamp=none", binary])
    if folder.suffix == ".app":
        run(["codesign", "--force", "--sign", "-", "--timestamp=none", folder])
        run(["codesign", "--verify", "--deep", "--strict", str(folder)])


def archive(folder, destination, rid):
    destination.parent.mkdir(parents=True, exist_ok=True)
    if rid.startswith("win-"):
        output = Path(str(destination) + ".zip")
        with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as stream:
            for item in sorted(folder.rglob("*")):
                if item.is_file():
                    stream.write(item, item.relative_to(folder.parent))
    else:
        output = Path(str(destination) + ".tar.gz")
        def executable_modes(member):
            if member.issym():
                member.linkname = Path(member.linkname).as_posix()
            # Windows cross-publishing cannot preserve POSIX apphost mode bits.
            if member.isfile() and Path(member.name).name in ("pgpmint", "pgpmint-host", "PGPMint.Desktop", "PGPMint"):
                member.mode = 0o755
            return member
        with tarfile.open(output, "w:gz") as stream:
            stream.add(folder, arcname=folder.name, filter=executable_modes)
    return output


def write_checksums(dist, artifacts, rid):
    dist.mkdir(parents=True, exist_ok=True)
    manifest = dist / f"SHA256SUMS-{rid}.txt"
    lines = []
    for artifact in artifacts:
        with artifact.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        lines.append(f"{digest}  {artifact.name}\n")
    manifest.write_text("".join(lines), encoding="utf-8")
    return manifest


def instructions(folder, rid, tag):
    windows = rid.startswith("win-")
    mac = rid.startswith("osx-")
    command = r".\cli\pgpmint.exe" if windows else "./cli/pgpmint"
    backends = ["vulkan", "opencl"] + (["metal"] if mac else [])
    lines = [f"PGPMint {tag} / {rid}", "", "HARDWARE GPU ACCEPTANCE: NOT VERIFIED.",
             "HardwareGpu tests are excluded on hosted CI. devices is inventory, never a GPU pass.",
             "All runtimes are self-contained; .NET installation is not required.",
             "Extract the complete archive; keep the CLI's native libraries beside the executable.",
             "Install the matching GPU vendor driver; software Vulkan/CPU devices are not acceptance.",
             "Use a dedicated vault directory. Existing Unix vault directories must have mode 0700;",
             "broader directory permissions are rejected without changing the directory.",
             "macOS private vault/export paths with ACL entries are refused, including inherited ACLs.",
             "", "Inventory (retain diagnostics and driver/runtime details):",
             f"{command} devices --json", "", "Run on actual hardware for EACH available backend:"]
    for backend in backends:
        lines += [f"{command} verify --backend {backend}",
                  f"{command} benchmark --backend {backend} --seconds 60 --rounds 3 --mode find-one",
                  f"{command} benchmark --backend {backend} --mode enumerate-all"]
    lines += ["No GPU: devices may exit 0, but verify/benchmark must exit 1; record UNVERIFIED.",
              "Benchmark uses isolated disposable data, never the normal vault.",
              "Save commands, exit codes, devices JSON, stdout/stderr and benchmark mode with results.",
              "SHA256SUMS alongside the archive verifies download integrity."]
    if mac:
        lines += ["", "GUI: open desktop/PGPMint.app (native SwiftUI/AppKit app with bundled self-contained pgpmint-host).",
                  "macOS 14+ (.NET 10); CLI/Host include MoltenVK v1.4.2 and license notices.",
                  "No Vulkan SDK or global loader setup is required.",
                  "Ad-hoc signed only. No Developer ID signature; NOT NOTARIZED.",
                  "Gatekeeper may require a user-approved Open action in System Settings."]
    elif windows:
        lines += ["", "GUI: use the separately supplied standalone PGPMint-*.exe (WinUI)."]
    else:
        lines += ["", "GUI: ./desktop/PGPMint.Desktop (requires a graphical session).",
                  "Ubuntu 24.04 runtime libraries: libx11-6 libice6 libsm6 libfontconfig1",
                  "libxrender1 libxrandr2 libxi6 libxcursor1 libxinerama1 libgl1 libegl1",
                  "libglib2.0-0t64 libicu74 libssl3t64; GPU drivers may need libvulkan1/ocl-icd-libopencl1."]
    (folder / "TESTING.txt").write_text("\n".join(lines) + "\n", encoding="utf-8")
    license_file = ROOT / "LICENSE"
    if license_file.is_file():
        shutil.copy2(license_file, folder / "LICENSE")


def package(rid, tag, scratch, platform):
    version_numbers(tag)
    if rid not in RIDS:
        raise ValueError(f"Unsupported RID: {rid}")
    scratch = scratch.resolve()
    if scratch == ROOT or ROOT in scratch.parents:
        raise ValueError("Packaging scratch must be outside the repository")
    work = scratch / rid
    work.mkdir(parents=True, exist_ok=False)
    dist = scratch / "dist"
    stage = work / f"PGPMint-{tag}-{rid}"
    stage.mkdir()
    cli = stage / "cli"
    publish("PGPMint.Cli", cli, rid, platform, tag)
    artifacts = []
    if rid.startswith("win-"):
        winui = work / "winui"
        publish("PGPMint.App", winui, rid, platform, tag, single=True)
        exe = single_executable(winui)
        dist.mkdir(parents=True, exist_ok=True)
        output = dist / f"PGPMint-{tag}-{rid}.exe"
        shutil.copy2(exe, output)
        artifacts.append(output)
    elif rid.startswith("osx-"):
        host_dir = work / "host-publish"
        publish("PGPMint.Host", host_dir, rid, platform, tag)
        runtime = install_moltenvk(scratch / "moltenvk")
        for destination in (cli, host_dir):
            if not (destination / "libpgpmint_metal.dylib").is_file():
                raise ValueError(f"Missing transitive Metal bridge in {destination}")
            shutil.copy2(runtime / "libMoltenVK.dylib", destination)
            shutil.copytree(runtime / "licenses", destination / "licenses")
        native_bin, bundles = build_macos_native_app(rid, work / "swift-build")
        app = macos_app(native_bin, bundles, host_dir, stage / "desktop", tag)
        sign_macos(cli)
        sign_macos(app)
    else:
        desktop = stage / "desktop"
        publish("PGPMint.Desktop", desktop, rid, platform, tag)
        svg_icon = ROOT / "assets" / "pgpmint-icon.svg"
        if svg_icon.is_file():
            shutil.copy2(svg_icon, desktop / "pgpmint.svg")
        png_icon = ROOT / "src" / "PGPMint.Desktop" / "Assets" / "pgpmint-icon.png"
        if png_icon.is_file():
            shutil.copy2(png_icon, desktop / "pgpmint.png")
    instructions(stage, rid, tag)
    dist.mkdir(parents=True, exist_ok=True)
    testing = dist / f"TESTING-{rid}.txt"
    shutil.copy2(stage / "TESTING.txt", testing)
    artifacts.append(testing)
    artifacts.append(archive(stage, dist / stage.name, rid))
    write_checksums(dist, artifacts, rid)
    return stage


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", required=True, choices=RIDS)
    parser.add_argument("--tag", default="v0.0.0-dev")
    parser.add_argument("--scratch", required=True, type=Path)
    parser.add_argument("--platform", required=True, choices=("x64", "ARM64"))
    args = parser.parse_args()
    package(args.rid, args.tag, args.scratch, args.platform)
