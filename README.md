![SSPI PS4](docs/banner.png)

# SSPI PS4

Super Simple Package Installer is a package manager for homebrew-enabled PlayStation 4 consoles. It searches package sources, manages downloads and installs content from a controller-friendly interface.

[Releases](https://github.com/Xyhlo/SSPI/releases) · [Guide](https://xyhlo.github.io/SSPI/) · [Discord](https://discord.gg/hF2vw7ybRs) · [Issues](https://github.com/Xyhlo/SSPI/issues)

## Features

- Search installed package sources by title or CUSA ID, with grouped regions and source variants.
- Real-Debrid, TorBox, AllDebrid and Premiumize support. Each service requires your own account.
- Queue direct links, stored debrid files and packages or archives from USB.
- Background download, extraction and installation through a resident worker.
- RAR, ZIP and 7z extraction, including multipart and password-protected RAR archives.
- Staging on internal storage or an exFAT USB drive.
- Base, update and DLC ordering, with update detection for installed games.

## Requirements

- A PS4 running GoldHEN.
- Free storage for downloaded archives, extracted packages and installed content.
- An account with a supported service for hosted files.

## Installation

1. Download the `.pkg` file from the latest release. GitHub source archives are not installable packages.
2. Install it over any existing SSPI application.
3. Launch SSPI once, fully restart the PS4, re-enable GoldHEN and open SSPI again.
4. Check that the version and build ID in the footer match the release notes.

## Setup

1. Open **Settings → Connections** and scan the pairing QR code with a phone on the same network. Save your service keys and enable the services you want to use.
2. Open **Manage sources** and enable your installed package sources.
3. Use **Settings → Storage** to choose a staging location. Keep a selected USB drive connected until installation finishes.

Runtime data is stored under `/data/SSPI`. Settings and service keys stay on the console.

## Controls

| Button | Action |
| --- | --- |
| D-pad | Move focus or adjust a value |
| Cross | Open, select or run the displayed action |
| Circle | Back or close |
| L1 / R1 | Switch tabs or settings pages |
| Options | Open or close settings |
| Square | Selection, cancellation or removal |
| Triangle | Expand mirrors or refresh |
| L2 | Change region in details, close the Downloads drawer or cycle queue filters |
| R2 | Open the Downloads drawer or filter update versions |
| Touchpad | Add direct links, stored debrid files or USB packages from Downloads |

The action row at the bottom of each screen shows the current bindings.

## Reporting problems

Open an [issue](https://github.com/Xyhlo/SSPI/issues) with:

- The version and build ID from the footer
- Console model and firmware
- Package type and the stage that failed (download, extraction or installation)
- The full error message and steps to reproduce
- Relevant entries from `/data/SSPI/logs/combined.log`

Remove service keys, pairing tokens and signed download URLs before sharing logs or screenshots.

## Source

This repository is a production-source export. SDKs, build tooling, runtime assets, tests and source descriptors are not included, so a plain clone is not a complete build environment.

| Path | Contents |
| --- | --- |
| `app/` | Application, interface, sources, services and pairing |
| `native/` | Startup, module loading, cover decoding and video |
| `transfer/` | Native download engine |
| `https/` | HTTP range helpers |
| `resident/` | Background queue, archive and installation code |
| `product.json` | Version and application identity |

## License

Original SSPI code is released under the [MIT license](LICENSE). Third-party source keeps its own notices; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
