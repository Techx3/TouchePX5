<!--
Copyright (C) 2026 Touché PX5 contributors
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Touché PX5

<p align="center">
  <img src="./assets/images/touchepx5-logo.png" width="360" alt="Touché PX5 logo">
</p>

<p align="center">
  <strong>An experimental PlayStation 5 emulator</strong><br>
  Independent development focused on compatibility, graphics, video, audio, input, and real-game testing.
</p>

<p align="center">
  <a href="https://github.com/Techx3/TouchePX5/actions/workflows/workflow.yml"><img src="https://github.com/Techx3/TouchePX5/actions/workflows/workflow.yml/badge.svg?branch=main" alt="Build status"></a>
  <a href="./LICENSE"><img src="https://img.shields.io/badge/license-GPL--2.0--or--later-6d5dfc.svg" alt="GPL-2.0-or-later"></a>
  <img src="https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS-16a3ff.svg" alt="Platforms">
</p>

---

> [!NOTE]
> Touché PX5 supports Windows x64, Linux x64, and macOS x64. Apple Silicon Macs
> can run the macOS x64 build through Rosetta 2, and Windows on ARM devices
> (e.g. Snapdragon) can run the Windows x64 build through Windows' built-in
> x64 emulation.

> [!WARNING]
> Touché PX5 is in an early experimental stage. Games may fail to boot, render incorrectly, run slowly, lose audio synchronization, or crash.

## About Touché PX5

Touché PX5 is an independently maintained PlayStation 5 emulator written primarily in C#, with its own interface, roadmap, compatibility work, and public repository.

This project is developed purely for research and educational purposes. There are no commercial goals associated with it. We enjoy learning about system architecture and reverse engineering.

Touché PX5 focuses exclusively on the PlayStation 5.
Our goal is **not** to emulate PS4 games, as there is already an excellent emulator dedicated to that platform: **ShadPS4**.

## Tested games

These results describe development tests, not guaranteed compatibility. Behavior varies by game version, operating system, GPU, and driver.

| Game | Observed state |
| --- | --- |
| Castlevania Dominus Collection | Boots; menus, video, audio, gameplay, and save paths are under active testing |
| Dreaming Sarah | Boots and renders in-game |
| The Messenger | Boots and renders; presentation orientation corrected |
| Story of Seasons: A Wonderful Life | Boots into gameplay; performance and stability remain under investigation |
| Cat Quest III | Early boot and compatibility testing |

## Status

The emulator can currently load the `eboot.bin` of real games, execute native CPU instructions, and partially handle kernel-related functionality. However, several critical components are still missing.

Current capabilities include:

* Loading `eboot.bin` and `.elf` files
* Executing native CPU instructions
* Reading basic game metadata (title, version, etc.)
* Loading system modules (`prx` / `sys_module`)
* Partial support for kernel functions
* `Fiber` and `AMPR` exports
* PlayGo scenarios
* Initial AGC shader and resource submission
* Vulkan video output in supported paths
* AVPlayer video and audio handling
* NGS2 software mixing and HEVAG decoding
* Controller input and early save-data support

Compatibility remains title-specific and is actively evolving.

Touché PX5 supports Windows, Linux, and macOS hosts. Video output uses Vulkan on
Windows and Linux, and MoltenVK on macOS. Platform support is still experimental,
so compatibility and performance vary by game, operating system, and GPU driver.

## Using Touché PX5

Download a build from [GitHub Actions](https://github.com/Techx3/TouchePX5/actions/workflows/workflow.yml),
extract it, and launch `TouchePx5.exe` without arguments to open the desktop game
library. You can also pass the path to a legally obtained game's `eboot.bin`.

Windows PowerShell:

```powershell
.\TouchePx5.exe --cpu-engine=native --log-level=info "C:\path\to\game\eboot.bin" 2>&1 |
  Tee-Object -FilePath "TouchePx5.log"
```

Linux and macOS:

```bash
chmod +x ./TouchePx5

./TouchePx5 --cpu-engine=native --log-level=info "/path/to/game/eboot.bin" 2>&1 |
  tee TouchePx5.log
```

A Vulkan-capable GPU and current graphics driver are required. The macOS
release includes the MoltenVK Vulkan implementation.

> [!IMPORTANT]
> This project does **not** support or condone piracy.
> Users are expected to use legally obtained copies of their games.

### Local firmware packages

The firmware development branch can validate and store a user-supplied
`PS5UPDATE.PUP` from **Options → Firmware → Install PUP**. Packages are copied
to `user/firmware/<sha256>/` and never uploaded or added to the repository.

The installer verifies the package SHA-256 and distinguishes an official
encrypted `SLB2` package from a previously decrypted PUP structure. For a
decrypted structure it validates bounded table and block entries, extracts
direct and zlib-compressed payloads into `entries/`, and records them in
`inventory.json`.

Recognized exFAT images are traversed through a bounded, read-only implementation
with FAT-chain, contiguous-allocation, checksum, cycle and path validation. Their
files are imported into the local content-addressed firmware profile and PS5
SELF modules are catalogued automatically. Touché PX5 does not decrypt protected
SELF content; cataloguing a module does not mean it can already replace its HLE
implementation.

For research with a legally obtained, already decrypted module, place the ELF
next to its protected module and append `.elf` to the complete name, for example
`common/lib/libkernel.sprx.elf`. During **Import extracted folder…**, Touché PX5
verifies that the sidecar is a bounded x86-64 ELF and that the matching protected
`libkernel.sprx` exists. The original SELF remains unchanged; only that exact
guest module can prefer the verified sidecar. A renamed file without a matching
SELF is not treated as a replacement. Compatibility approval per module hash,
firmware profile and core version is still required before LLE execution.

PS5 Backup and Restore files named `archive.dat` use the separate `SIECAF`
format. Touché PX5 identifies and rejects them as firmware; they cannot replace
a `PS5UPDATE.PUP` and may contain account-linked user data.

## Build from source

1. Install the .NET SDK version specified in [`global.json`](./global.json).
2. Clone the repository: `git clone https://github.com/Techx3/TouchePX5.git`
3. Restore dependencies: `dotnet restore TouchePx5.slnx`
4. Build: `dotnet build TouchePx5.slnx -c Release --no-restore`
5. Test: `dotnet test TouchePx5.slnx -c Release --no-build`

Build artifacts are written to the `artifacts` directory.

## Disclaimer

Touché PX5 is an experimental emulator intended for research and educational purposes.

This project does not contain any copyrighted system firmware, game data, or proprietary PlayStation assets.

## Origins and acknowledgements

Touché PX5 is independently maintained and has its own identity and direction.
It originated from the GPL-licensed [SharpEmu](https://github.com/sharpemu/sharpemu)
codebase, whose copyright and license notices are retained where required.

Public research and implementations from these projects have also been helpful:

* **[SharpEmu](https://github.com/sharpemu/sharpemu)** — Upstream project from which Touché PX5 originated.
* **[shadPS4](https://github.com/shadps4-emu/shadPS4)** — Reference for PlayStation architecture and emulation research.
* **[KytyPS5](https://github.com/KytyPS5/KytyPS5)** — Reference for PS5 native-code execution research.
* **[Ryujinx](https://github.com/ryujinx-mirror/ryujinx)** — Reference for filesystem handling and low-level C# patterns.
* **[vgmstream](https://github.com/vgmstream/vgmstream)** and **[FFmpeg](https://ffmpeg.org/)** — Media and audio compatibility work.

Touché PX5 is not affiliated with, endorsed by, or connected to Sony Interactive
Entertainment. PlayStation is a trademark of Sony Interactive Entertainment.

## Support Touché PX5

If you want to support development, voluntary cryptocurrency contributions can
be sent to the addresses below. Always verify the address and network before
sending funds.

### Bitcoin (BTC)

<p align="center">
  <img src="./assets/images/btc-donation-qr.png" width="280" alt="Bitcoin donation QR for Touché PX5">
</p>

```text
bc1qeur2u3qvnrczt90yfhjd58tc4uqnkqvp23kf5f
```

### Ethereum (ETH)

<p align="center">
  <img src="./assets/images/eth-donation-qr.png" width="280" alt="Ethereum donation QR for Touché PX5">
</p>

```text
0x8e2Fbc640DBCaf01BA314CaD3F619811136f2505
```

## License

Touché PX5 is distributed under the [GNU General Public License v2.0 or later](./LICENSE).
Third-party components and adapted code retain their respective copyright and license notices.

## Contributing

Before opening an issue or pull request, please read our contribution guidelines:

**[CONTRIBUTING.md](./CONTRIBUTING.md)**

The guide covers:
- Coding style and formatting
- AI-assisted contributions
- Pull request expectations
- Testing guidelines
- Legal and reverse engineering policy
