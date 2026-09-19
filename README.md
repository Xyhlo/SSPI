![SSPI PS4](docs/banner.png)

# SSPI PS4

**Super Simple Package Installer** is a package manager for homebrew-enabled PlayStation 4 consoles. Search sources, compare packages, manage downloads and install content from a controller-friendly interface.

**Current release: 5.11.2b beta · Build `13ca8ab42e3b`**

[Download the 5.11.2b beta](https://github.com/Xyhlo/SSPI/releases/tag/v5.11.2b) · [Read the guide](https://xyhlo.github.io/SSPI/) · [Join Discord](https://discord.gg/hF2vw7ybRs) · [Report a bug](https://github.com/Xyhlo/SSPI/issues)

Download `sspi5.11.2b.pkg` from the release assets. GitHub source archives are not installable PS4 packages. This release retains the in-app version label `BETA 5.11`; the build ID identifies the updated package.

## Changes in 5.11.2b

- **Queue ordering and recovery.** Base, update and DLC dependencies work when added out of order. Provider preparation can wait while ready downloads continue. Pause, cancel and remove requests survive reopening SSPI, with task-ownership checks before stopping an installation or releasing files.
- **Clearer installation stages.** Eligible completed base packages on internal storage can use direct BGFT storage registration. Preparation, copying and confirmation are shown separately. Accepted installations retain their recovery records until completion is confirmed; the PS4 still performs its own installation work.
- **Transfer stability.** Temporary interruptions use paced recovery, provider cooldowns are respected and connection counts can recover after a slowdown. Resume identity checks preserve existing data, interrupted lock records can recover, and TLS verification remains enabled with updated chain inputs.
- **Cloud files and multipart preparation.** Stored-file imports retain the owning account and cloud file identity. TorBox reports sent and ready part counts, reuses existing jobs and waits through temporary capacity limits. AllDebrid delayed links also yield the queue while preparing.
- **Provider support.** Usable mirrors are filtered against enabled services' reported support. Checks show three animated dots, individual unsupported services stay red, and failures identify the affected archive part instead of hiding the provider's error.
- **Package lookup and catalog refresh.** Matching-title fallback, regional lookup and numeric/Roman-numeral searches improve discovery. Compatible sources can refresh new entries and cache them while keeping existing results available.
- **Archive and drawer feedback.** RAR password retry decisions use decoder-phase evidence. Part checks name the current volume. The drawer has compact rows, longer bars and a size-weighted summary; progress fills stay neutral when changing the accent.
- **Navigation and artwork.** The existing software renderer targets 60 FPS, with improved frame pacing, controller repeat handling, framebuffer copying and staged artwork uploads. Missing covers try an installed icon and exact-title metadata. A locked 60 FPS on every console remains unverified.

See the [complete 5.11.2b changelog](https://github.com/Xyhlo/SSPI/releases/tag/v5.11.2b) for every fix, updated controls, package checksum and testing details.

## Included from 5.11.1

- **Downloads drawer.** Press R2 to open Packages, Files and Errors directly under the selected game. Controls stay in the drawer until L2 closes it. Rounded cards keep their state-colored outline, and the separate transfer circle has been removed. Thanks to The Fantastic Loki for the drawer idea.
- **Clearer errors and actions.** Failed jobs open on Errors, with scrollable error text and an attention strip. Updated action and confirmation dialogs show what will happen. Square on a game removes its queued packages together; Square inside the drawer targets the selected package. Installed games and original USB files are kept.
- **Downloads responsiveness.** Queue snapshots no longer perform filesystem reconciliation or native installation checks. Queue changes and installation preparation run away from rendering. The selected-artwork draw path has been changed, with additional error recovery and stall diagnostics.
- **Direct packages and archives.** File headers determine the preparation path, so a direct PKG supplied by an archive-marked source skips extraction. Incoming filenames reflect the detected format, including RAR volume suffixes.
- **RAR extraction and passwords.** Native RAR handling is shared by in-app and resident extraction, with fixes for multipart archives, encrypted headers, solid archives and Unicode filenames. Source passwords survive queuing and handoff. General settings can retry supplied alternative passwords, including exact bracketed forms, after a password rejection.
- **Extraction progress and large files.** Progress uses expanded package sizes from archive headers. Buffered writes, 64-bit offsets, throttled reporting and cancellation checks support large jobs. Missing volumes, integrity failures and archives without packages produce clearer errors; failed archives remain available for retry.
- **Installation tracking.** Individual extracted packages show completion from installation receipts. DLC identity and recovery records are retained before the installer consumes a source file, and unconfirmed work is checked before another attempt. Base, update and DLC ordering uses package metadata.
- **Grouped search results.** Matching titles keep their regions and source variants in one result. In details, L2 cycles the available variants using a compact region hint. Triangle still expands mirrors, and R2 filters update versions.
- **Pink and ASCII Flowers.** Appearance settings now include a Pink accent and an accent-colored ASCII Flowers background. Both save between launches and are available through phone setup.

See the [release notes](https://github.com/Xyhlo/SSPI/releases/tag/v5.11.1-beta) for the full changelog, controls and verification details.

## Included from 5.11

- **AllDebrid and Premiumize support.** These join Real-Debrid and TorBox. Save several services, choose which are enabled, and check account and host support from Connections. Each service still requires your own account.
- **More ways to queue files.** Paste direct package or archive links, browse ready files in Real-Debrid and TorBox accounts, or select multiple packages and archives from USB.
- **USB staging and stored files.** Choose internal storage or a connected exFAT drive for downloads and extraction. Browse stored files and remove unused items. Failed jobs retain recoverable files; borrowed USB originals are preserved.
- **Static catalogs and source browsing.** Browse embedded catalogs, manage installed sources and install source packages through the source browser. Package categories, versions and mirrors remain available when supplied by the source.
- **Library update detection.** Compare installed versions with official update metadata, including before adding a package source. Update metadata does not confirm that a compatible download or backport is available.
- **Reworked background transfers.** A shared chunk engine provides bounded parallel connections, saved checkpoints, retry handling and file verification. The resident queue handles download, extraction and installation in order and preserves pause and cancel requests across restarts.
- **Installation recovery.** Task ownership and completion checks improve duplicate-task handling, cancellation and stalled-install recovery. An interrupted archive extraction requires attention instead of repeatedly restarting the decoder. RAR, ZIP and 7z handling includes storage and archive checks.
- **Clearer download status.** Progress shows resident ownership, transfer rate and remaining time. In 5.11.1, state colors and optional pulsing are carried by the rounded card and drawer outline. Reduce Motion disables pulsing.
- **Startup and interface fixes.** Data migration retains conflicts, resident loading preserves the shell filesystem context, and framebuffer handling bounds waits and memory use. Cover decoding, cache identity, input handling and optional audio have also been refined.

## Installing and using 5.11.2b

Install `sspi5.11.2b.pkg` over the existing SSPI application. Launch SSPI once to stage the updated resident, fully restart the PS4, re-enable GoldHEN and reopen SSPI. Check for `BETA 5.11 / 13ca8ab42e3b` in the footer. Closing SSPI alone leaves the previous resident loaded.

1. Open **Settings → Connections** and scan the pairing QR code with a phone on the same network. Save your service keys and enable the services you want to use.
2. Open **Manage sources** and enable your installed package sources.
3. Search by title or CUSA ID. In details, press **L2** to change region/source variant, **Triangle** to expand mirrors and **Cross** to queue the selected package.
4. Open **Downloads** and press **R2** for the file drawer. Use **Left/Right** for Packages, Files and Errors, **Up/Down** to select or scroll, and **L2** to close. With the drawer closed, the touchpad opens direct links, stored debrid files and USB installs.
5. Use **Settings → Storage** to choose a staging location or browse stored files. Keep a selected USB drive connected until installation finishes.

Match updates and DLC to the installed title ID. Missing version or firmware information is shown as unknown. Reported host support does not guarantee that a particular file is available. Cloud preparation and transfer to the PS4 are separate stages; a multipart set needs every volume.

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
| L2 | Change region in details; close the Downloads drawer; cycle queue filters when the drawer is closed |
| R2 | Open the Downloads drawer or filter update versions in details |
| Touchpad | Add files from Downloads |

## Beta status

The managed/native build, package verification and focused regression checks passed for this release. Host tests cover queue recovery, provider preparation, source refresh, installation routing, artwork and frame pacing; native PS4 APIs are mocked in those tests. Console testing under download and extraction load is still needed to measure sustained frame rate, transfer speed and installation time across models and firmware versions.

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

Include the footer version and build ID, console model and firmware, package type, full error code and steps to reproduce the problem. Include the relevant entries from `/data/SSPI/logs/combined.log` and state whether it failed during download, extraction or installation. For speed reports, include connection type and sustained speed in MB/s. Remove service keys, pairing tokens and signed download URLs from shared logs or screenshots.

## License

Original SSPI contributions use the [MIT license](LICENSE). Retained third-party source keeps its own notices; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
