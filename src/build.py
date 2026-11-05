#!/usr/bin/env python3
"""Builds the launcher with the csc.exe included in Windows, with the patch public key inside.

    python src/build.py [--output PATH] [--force]

It only compiles when the source, the key or the icon changes: csc never produces the same binary twice, and a different .exe for no reason would make every player update. If the source
changes, it requires a new Patch.LauncherVersion, which is what makes installed launchers update themselves.
"""
import argparse
import hashlib
import json
import re
import subprocess
import tempfile
from pathlib import Path

SRC = Path(__file__).resolve().parent
ROOT = SRC.parent
CSC = r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
OUTPUT = ROOT / "bin" / "SteelOceanLinsoft.exe"
ICON = SRC / "icon.ico"
SOURCE = SRC / "SteelOceanLinsoft.cs"
KEY = ROOT / "keys" / "patch_public.xml"
REFERENCES = ("System.Web.Extensions.dll", "System.Windows.Forms.dll", "System.Drawing.dll", "Microsoft.CSharp.dll", "System.Core.dll", "System.Security.dll")


def version() -> str:
    return re.search(r'LauncherVersion = "([\d.]+)"', SOURCE.read_text(encoding="utf-8")).group(1)


def _fingerprint() -> str:
    h = hashlib.sha256()
    for f in (SOURCE, KEY, ICON, Path(__file__)):
        h.update(f.read_bytes() if f.is_file() else b"-")
    return h.hexdigest()


def build(output: Path = OUTPUT, force: bool = False, forced_version: str | None = None) -> Path:
    """`forced_version` builds with another version number: only for testing the self-update, and it leaves no record."""
    stamp = output.with_suffix(".build.json")
    if not force and not forced_version and output.is_file() and stamp.is_file():
        previous = json.loads(stamp.read_text(encoding="utf-8"))
        if previous["fingerprint"] == _fingerprint():
            return output
        if previous["version"] == version():
            raise SystemExit(f"The launcher changed but is still version {version()}: raise Patch.LauncherVersion and AssemblyVersion.")
    source = SOURCE.read_text(encoding="utf-8").replace("__PUBLIC_KEY__", KEY.read_text(encoding="ascii"))
    if forced_version:
        source = source.replace(f'LauncherVersion = "{version()}"', f'LauncherVersion = "{forced_version}"')
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as t:
        cs, exe = Path(t) / "SteelOceanLinsoft.cs", Path(t) / "SteelOceanLinsoft.exe"
        cs.write_text(source, encoding="utf-8-sig")
        command = [CSC, "/nologo", "/optimize+", "/target:winexe", f"/out:{exe}", "/codepage:65001", *[f"/r:{r}" for r in REFERENCES]]
        if ICON.is_file():
            command.append(f"/win32icon:{ICON}")
        r = subprocess.run(command + [str(cs)], capture_output=True, text=True, encoding="cp850", errors="replace")
        if r.returncode:
            raise SystemExit(r.stdout[-3000:])
        output.write_bytes(exe.read_bytes())
    if not forced_version:
        stamp.write_text(json.dumps({"fingerprint": _fingerprint(), "version": version()}), encoding="utf-8")
    return output


if __name__ == "__main__":
    a = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    a.add_argument("--output", type=Path, default=OUTPUT)
    a.add_argument("--force", action="store_true")
    o = a.parse_args()
    print("built", build(o.output, o.force), "version", version())
