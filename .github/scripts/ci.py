"""Separate build/test gates; inventory never establishes hardware GPU acceptance."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
TESTS = ("PGPMint.Tests", "PGPMint.Cli.Tests", "PGPMint.Host.Tests", "PGPMint.Desktop.Tests")
MACAPP_DIR = ROOT / "src" / "PGPMint.MacApp"


def run(args, **kwargs):
    print("+", " ".join(map(str, args)), flush=True)
    return subprocess.run(list(map(str, args)), cwd=ROOT, check=True, **kwargs)


def build(platform):
    common = ["-c", "Release", f"-p:Platform={platform}"]
    if sys.platform == "win32":
        run(["dotnet", "build", "PGPMint.sln", *common])
    else:
        for name in ("PGPMint.Core", "PGPMint.Cli", "PGPMint.Host", "PGPMint.Desktop"):
            run(["dotnet", "build", f"src/{name}/{name}.csproj", "-f", "net10.0", *common])
    # Build every suite before the separate test step; never let dotnet test build.
    for name in TESTS:
        run(["dotnet", "build", f"tests/{name}/{name}.csproj", *common])
    if sys.platform == "darwin":
        run(["swift", "build", "--package-path", str(MACAPP_DIR), "--build-tests", "--disable-xctest"])


def test(platform, diagnostics):
    failed = []
    for name in TESTS:
        try:
            run(["dotnet", "test", f"tests/{name}/{name}.csproj", "-c", "Release",
                 f"-p:Platform={platform}", "--no-build", "--no-restore",
                 "--filter", "Category!=HardwareGpu", "--verbosity", "normal",
                 "--logger", f"trx;LogFileName={name}.trx", "--results-directory", diagnostics])
        except subprocess.CalledProcessError:
            failed.append(name)
    if sys.platform == "darwin":
        try:
            run(["swift", "test", "--package-path", str(MACAPP_DIR), "--skip-build", "--disable-xctest"])
        except subprocess.CalledProcessError:
            failed.append("PGPMint.MacApp")
    if failed:
        raise RuntimeError("Failed suites: " + ", ".join(failed))


def cli(platform):
    return ["dotnet", "run", "--project", "src/PGPMint.Cli/PGPMint.Cli.csproj",
            "-c", "Release", f"-p:Platform={platform}", "--no-build", "--no-restore", "--"]


def inventory(command, diagnostics, name):
    try:
        result = run([*command, "devices", "--json"], capture_output=True, text=True)
    except subprocess.CalledProcessError as failure:
        (diagnostics / f"{name}.json").write_text(failure.stdout or "", encoding="utf-8")
        (diagnostics / f"{name}-stderr.txt").write_text(failure.stderr or "", encoding="utf-8")
        raise
    (diagnostics / f"{name}.json").write_text(result.stdout, encoding="utf-8")
    (diagnostics / f"{name}-stderr.txt").write_text(result.stderr, encoding="utf-8")
    data = json.loads(result.stdout)
    if data.get("hardwareGpuVerified") is not False:
        raise ValueError("Inventory must explicitly report hardwareGpuVerified=false")
    print(result.stdout, flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("step", choices=("build", "test", "inventory", "metal"))
    parser.add_argument("--platform", required=True, choices=("x64", "ARM64"))
    parser.add_argument("--diagnostics", type=Path, default=Path(os.environ["RUNNER_TEMP"]) / "diagnostics")
    parser.add_argument("--packaged-cli", type=Path)
    args = parser.parse_args()
    diagnostics = args.diagnostics.resolve()
    diagnostics.mkdir(parents=True, exist_ok=True)
    if args.step == "build":
        build(args.platform)
    elif args.step == "test":
        test(args.platform, diagnostics)
    elif args.step == "inventory":
        command = [str(args.packaged_cli)] if args.packaged_cli else cli(args.platform)
        inventory(command, diagnostics, "devices-package" if args.packaged_cli else "devices-build")
    else:
        source = diagnostics / "PGPMint.metal"
        result = run([*cli(args.platform), "metal-source"], capture_output=True, text=True)
        source.write_text(result.stdout, encoding="utf-8")
        run(["xcrun", "-sdk", "macosx", "metal", "-std=macos-metal2.3", "-c", source,
             "-o", diagnostics / "PGPMint.air"])
    message = "Hardware GPU acceptance: NOT VERIFIED. HardwareGpu excluded; inventory/shader compilation are not GPU correctness or performance validation.\n"
    print(message)
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as stream:
            stream.write(message)


if __name__ == "__main__":
    main()
