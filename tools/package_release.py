"""Build release/StraftatTF2Rockets-<version>.zip: install.bat/.ps1, README, LICENSE and the built plugin under
BepInEx/plugins/StraftatRockets. Only the mod's own files go in (no BepInEx, nothing from STRAFTAT or TF2).

Usage: python tools/package_release.py   (after: dotnet build StraftatRockets -c Release)
"""
import os
import re
import zipfile

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
version = re.search(r'Version = "([^"]+)"', open(os.path.join(ROOT, "StraftatRockets", "Plugin.cs"), encoding="utf-8").read()).group(1)
dll = os.path.join(ROOT, "StraftatRockets", "bin", "Release", "net472", "StraftatRockets.dll")
name = f"StraftatTF2Rockets-{version}"
out = os.path.join(ROOT, "release", name + ".zip")
os.makedirs(os.path.dirname(out), exist_ok=True)


def crlf(path):
    return open(path, "rb").read().replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")


files = [
    ("install.bat", crlf(os.path.join(ROOT, "install", "install.bat"))),
    ("install.ps1", crlf(os.path.join(ROOT, "install", "install.ps1"))),
    ("README.md", open(os.path.join(ROOT, "README.md"), "rb").read()),
    ("LICENSE", open(os.path.join(ROOT, "LICENSE"), "rb").read()),
    ("BepInEx/plugins/StraftatRockets/StraftatRockets.dll", open(dll, "rb").read()),
]
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    for arc, data in files:
        info = zipfile.ZipInfo(f"{name}/{arc}", date_time=(2026, 1, 1, 0, 0, 0))
        info.compress_type = zipfile.ZIP_DEFLATED
        z.writestr(info, data)
print(out, os.path.getsize(out), "bytes")
