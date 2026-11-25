"""Install a digest-pinned official MoltenVK build privately, never into the OS."""
import hashlib
from pathlib import Path
import shutil
import subprocess
import tarfile
import urllib.request

URL = "https://github.com/KhronosGroup/MoltenVK/releases/download/v1.4.2/MoltenVK-macos.tar"
SHA256 = "f95765a6229cb7b915990a2890ce12ebe36a730b021545d3d52ae69ce4c4024e"
# Revisions from MoltenVK v1.4.2/ExternalRevisions, including statically linked notices.
LICENSES = {
    "SPIRV-Cross": ("KhronosGroup", "6c09849fe88c48eaed08413aa022aaa136a3a057", "LICENSE"),
    "SPIRV-Tools": ("KhronosGroup", "0d6fd73ca73830ccab5fa1f00ed5ed40124e2c55", "LICENSE"),
    "SPIRV-Headers": ("KhronosGroup", "29981f65241605e08b0ede4cfeb999fe3b723c6a", "LICENSE"),
    "Vulkan-Headers": ("KhronosGroup", "e3b1eec08173d6b825cd3ac88c885a63b621504a", "LICENSE.md"),
    "cereal": ("USCiLab", "a56bad8bbb770ee266e930c95d37fff2a5be7fea", "LICENSE"),
}


def download(url, destination):
    with urllib.request.urlopen(url, timeout=120) as source, destination.open("wb") as target:
        shutil.copyfileobj(source, target)


def unpack(source, destination):
    with source.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    if digest != SHA256:
        raise ValueError("MoltenVK SHA256 mismatch")
    (destination / "licenses").mkdir(parents=True, exist_ok=True)
    members = {
        "MoltenVK/MoltenVK/dynamic/dylib/macOS/libMoltenVK.dylib": destination / "libMoltenVK.dylib",
        "MoltenVK/LICENSE": destination / "licenses/MoltenVK-LICENSE.txt",
    }
    with tarfile.open(source) as archive:
        for name, output in members.items():
            member = archive.getmember(name)
            if not member.isfile():
                raise ValueError(f"Expected regular archive member: {name}")
            with archive.extractfile(member) as content, output.open("wb") as target:
                shutil.copyfileobj(content, target)
    return destination


def install(scratch):
    scratch.mkdir(parents=True, exist_ok=True)
    source = scratch / "MoltenVK-macos.tar"
    if not source.exists():
        download(URL, source)
    destination = unpack(source, scratch / "runtime")
    for name, (owner, revision, filename) in LICENSES.items():
        url = f"https://raw.githubusercontent.com/{owner}/{name}/{revision}/{filename}"
        download(url, destination / "licenses" / (name + "-LICENSE.txt"))
    (destination / "licenses/PROVENANCE.txt").write_text(
        f"MoltenVK v1.4.2\n{URL}\nSHA256: {SHA256}\n"
        "Directly loaded from the application directory; no Vulkan SDK/loader dependency.\n"
        "Apple system frameworks are supplied by macOS, not redistributed.\n" +
        "\n".join(f"{name}: {revision}" for name, (_, revision, _) in LICENSES.items()) + "\n",
        encoding="utf-8")
    # Reject a future upstream binary that introduces an unbundled non-system dependency.
    library = destination / "libMoltenVK.dylib"
    output = subprocess.check_output(["otool", "-L", str(library)], text=True)
    for line in output.splitlines()[1:]:
        dependency = line.strip().split(" (", 1)[0]
        if dependency.endswith("/libMoltenVK.dylib"):
            continue
        if not dependency.startswith(("/usr/lib/", "/System/Library/")):
            raise ValueError(f"Unbundled MoltenVK dependency: {dependency}")
    (destination / "licenses/DEPENDENCIES.txt").write_text(output, encoding="utf-8")
    return destination
