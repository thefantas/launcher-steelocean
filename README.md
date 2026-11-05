# Steel Ocean launcher (Linsoft)

**[Download the launcher](https://github.com/thefantas/launcher-steelocean/releases/latest/download/SteelOceanLinsoft.exe)** · [All versions](https://github.com/thefantas/launcher-steelocean/releases)

Launcher for the private Steel Ocean server. This is an unofficial project. You need the game installed from Steam.

It finds your installation, applies the signed patch, keeps itself and the patch up to date, shows the patch versions and their notes, and starts the game. It shows English, or Spanish on a Spanish Windows.

## First run

Windows may show a SmartScreen warning, because the launcher is not code-signed. To run it, choose **More info**, then **Run anyway**. You can compare the file with the SHA-256 published in each release.

The launcher asks before changing anything the first time. Later updates install on their own when you open it, and **Check for updates** looks for them at any time. To restore the original game, use **Steam > Properties > Installed Files > Verify integrity**.

If the game crashes, it sends the Linsoft server a report with the game log and computer details: Windows version, graphics card, MAC address and disk serial number.

## How updates are protected

`bin/patch.json` lists every patched file and the current launcher, with their SHA-256 hashes. It is signed with an RSA 2048 key, and the launcher carries the public key. A file or manifest that does not match is rejected, and nothing is applied. The launcher never replaces itself with an older version.

## Contents

| Path | What it is |
|---|---|
| `src/SteelOceanLinsoft.cs` | The launcher (C# 5, WinForms, no dependencies) |
| `src/build.py` | Builds it with the `csc.exe` included in Windows |
| `src/make_icon.py` | Draws `src/icon.ico` |
| `keys/patch_public.xml` | The public key that verifies the patch |
| `bin/` | The published launcher and the signed manifest |

The patched game files (`*.gz`) are served by the game server, and the private signing key is not in this repository.

## Build

You need Windows and Python 3.10 or later:

```
python src/build.py
```
