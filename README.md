![SSPI PS4](docs/banner.png)

# SSPI PS4

**Super Simple Package Installer** is a package manager for homebrew-enabled PlayStation 4 consoles. Search sources, compare packages, manage downloads and install content from a controller-friendly interface.

**Source version: 5.11 beta**

[Download the public 5.10 beta](https://github.com/Xyhlo/SSPI/releases/tag/v5.10-beta) · [Read the guide](https://xyhlo.github.io/SSPI/) · [Join Discord](https://discord.gg/hF2vw7ybRs) · [Report a bug](https://github.com/Xyhlo/SSPI/issues)

The public download and guide above describe 5.10. This repository now contains the 5.11 beta source. GitHub source archives are not installable PS4 packages.

## Changes since the public 5.10 beta

- **AllDebrid and Premiumize support.** These join Real-Debrid and TorBox. Save several services, choose which are enabled, and check account and host support from Connections. Each service still requires your own account.
- **More ways to queue files.** Paste direct package or archive links, browse ready files in Real-Debrid and TorBox accounts, or select multiple packages and archives from USB.
- **USB staging and stored files.** Choose internal storage or a connected exFAT drive for downloads and extraction. Browse stored files and remove unused items. Failed jobs retain recoverable files; borrowed USB originals are preserved.
- **Static catalogs and source browsing.** Browse embedded catalogs, manage installed sources and install source packages through the source browser. Package categories, versions and mirrors remain available when supplied by the source.
- **Library update detection.** Compare installed versions with official update metadata, including before adding a package source. Update metadata does not confirm that a compatible download or backport is available.
- **Reworked background transfers.** A shared chunk engine provides bounded parallel connections, saved checkpoints, retry handling and file verification. The resident queue handles download, extraction and installation in order and preserves pause and cancel requests across restarts.
- **Installation recovery.** Task ownership and completion checks improve duplicate-task handling, cancellation and stalled-install recovery. An interrupted archive extraction requires attention instead of repeatedly restarting the decoder. RAR, ZIP and 7z handling includes storage and archive checks.
- **Clearer download status.** Status rings distinguish downloading, resolving, processing, complete, failed and waiting jobs. Progress shows resident ownership, transfer rate and remaining time. Reduce Motion disables ring pulsing.
- **Startup and interface fixes.** Data migration retains conflicts, resident loading preserves the shell filesystem context, and framebuffer handling bounds waits and memory use. Cover decoding, cache identity, input handling and optional audio have also been refined.

## Using 5.11

1. Open **Settings → Connections** and scan the pairing QR code with a phone on the same network. Save your service keys and enable the services you want to use.
2. Open **Manage sources** and enable your installed package sources.
3. Search by title or CUSA ID, check the region and package type, then choose a package or mirror.
4. Open **Downloads** to view progress and file details. Press the touchpad to paste links, browse stored debrid files or install from USB.
5. Use **Settings → Storage** to choose a staging location or browse stored files. Keep a selected USB drive connected until installation finishes.

Match updates and DLC to the installed title ID. Missing version or firmware information is shown as unknown. A listed host or mirror does not guarantee service support or account availability.

Runtime data is stored under `/data/SSPI`. Older `/data/GameSearch` data is migrated without overwriting conflicting files. Settings and service keys stay on the console.

## Controls

The action row at the bottom of each screen shows the current bindings.

| Button | Action |
| --- | --- |
| D-pad | Move focus or adjust a value |
| Cross | Open, select or run the displayed action |
| Circle | Back or close |
| L1 / R1 | Switch tabs or settings pages |
| Options | Open or close settings |
| Square | Context action, including selection, cancellation or removal |
| Triangle | Context action, including mirrors or refresh |
| R2 | File details or update-version filtering |
| Touchpad | Add files from Downloads |

## Beta status

Host builds and focused regression checks cover the transfer, archive, installation and interface changes. They do not establish console compatibility or confirm that the reported system-software crash is resolved. Firmware and resident behavior still need testing on the target console.

Background work requires a matching resident worker and the console capabilities it uses. Large archives need room for downloaded volumes and extracted files. Transfer speed depends on the console, storage, network, host and service; a displayed rate is not a sustained-speed guarantee. Installation is complete only after the installer confirms it.

## Source export

This is a stripped production-source export. It contains application code, project manifests, required license notices and the public guide. Local build inputs, SDKs, dependency trees, runtime assets, source descriptors, tests, internal tooling and compiled packages are excluded. A plain clone is not a complete build environment.

| Path | Contents |
| --- | --- |
| `app/` | Application, interface, sources, services and pairing |
| `native/` | Startup, module loading, cover decoding and video changes |
| `transfer/` | Shared native download engine |
| `https/` | HTTP range helpers |
| `resident/` | Background queue, archive and installation code |
| `product.json` | Version and application identity |

Distribution endpoints and mirror preferences are supplied by the local build configuration. They are not bundled in this source export.

## Reporting problems

Include the footer version and build ID, console model and firmware, package type, full error code and steps to reproduce the problem. State whether it failed during download, extraction or installation. For speed reports, include connection type and sustained speed in MB/s. Remove service keys, pairing tokens and signed download URLs from shared logs or screenshots.

## License

Original SSPI contributions use the [MIT license](LICENSE). Retained third-party source keeps its own notices; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
