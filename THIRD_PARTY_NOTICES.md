# Third-party notices and redistribution status

This file describes the inputs observed in the active `build-sdl.ps1` package
staging path on 2026-08-27. It is an engineering inventory, not legal advice.
The repository's MIT license covers only original Game Search contributions; it
does not relicense third-party code, binaries, artwork, trademarks, or data.

**Public binary redistribution is currently blocked.** Do not publish the PKG
or a binary bundle until every item marked `BLOCKED` below has documented
redistribution authority and the required license/notice texts are shipped.
See `docs/PKG_PROVENANCE.json` for the machine-readable inventory and hashes.

## Reviewed components

### SDL2# / SDL2-CS — notice present, binary origin incomplete

- Observed files: `SDL2-CS.dll` and source under
  `ps4/app/lib/SDL2-CS/`.
- Copyright: 2013-2021 Ethan Lee, as stated in the local license.
- License: zlib, verified from `ps4/app/lib/SDL2-CS/LICENSE`.
- Native `toolchain/ps4/pkg-tools/DotNetTemplate/binaries/Common/sce_module/libSDL2.sprx`: the local SDL2# README says SDL2
  uses zlib, but this binary has no version, source commit, or build record.
- Status: `BLOCKED` for the native binary until its exact source/revision and
  reproducible build provenance are recorded. The managed wrapper notice must
  remain with distributions.

### SixLabors.ImageSharp backport — conditional license path

- Observed file: `SixLabors.ImageSharp.dll` (assembly/file version `0.0.1.0`)
  and vendored source under `ps4/app/lib/SDL2-CS/ImageSharp/`.
- Copyright: Six Labors.
- Local license: Six Labors Split License 1.0, June 2022.
- The local license offers Apache-2.0 only when one of its listed qualification
  criteria is met. An MIT-licensed Game Search release appears to meet its
  open-source/source-available criterion, but that conclusion depends on the
  actual distributor and release remaining eligible.
- Status: `BLOCKED` until the vendored revision and modifications are recorded,
  the distributor confirms the applicable Split License criterion, and all
  Apache-2.0/Six Labors notice obligations are packaged. A commercial license
  may be required if the distributor does not qualify.

### Microsoft System compatibility packages — metadata found, notices incomplete

The ImageSharp project names these NuGet inputs, and the local NuGet `.nuspec`
files point to the dotnet/corefx license URL:

| Package | Package version | Staged assembly SHA-256 |
| --- | --- | --- |
| System.Buffers | 4.5.1 | `423200010a7684763451473a4fb206dfa074fc8249676621ef9d9a13417d364d` |
| System.Memory | 4.5.2 | `b078917b36fdaecbffee5fe54f3c274971f324fb775dd80f01e000b29c27e15e` |
| System.Numerics.Vectors | 4.5.0 | `671b682dde1d554d898bf34004c4dc2fef2da6985078c77311308b858b704828` |
| System.Runtime.CompilerServices.Unsafe | 4.5.2 | `a79e1c30e9308afe4d680f0bfb82de3e8c1fe94aeca453ec4092c3ed4789ae6b` |
| System.ValueTuple | 4.5.0 | `d6fb0dcfee1490a8168117ed1b55758f11db38475417b3668d19f89dcb55cbdd` |

Status: `BLOCKED` pending capture of the license/notice text applicable to
these exact package versions and confirmation that the staged DLLs came from
those packages rather than an unrecorded rebuild or substitution.

### PS4-OpenOrbis-Mono entrypoint and runtime bundle — unresolved

- `PS4-OpenOrbis-Mono/LICENSE` dedicates that project's own work to the public
  domain under the Unlicense.
- The active package stages `eboot.bin`, a 196-file `mono/` tree, and native
  modules copied from `toolchain/ps4/pkg-tools/DotNetTemplate/binaries/Common` / `binaries/Release`.
- That upstream project's own README explicitly warns that shipping its known
  PS4 Mono runtime may share a Sony binary and "can be a license issue."
- The `mono/4.5` tree is heterogeneous. It includes Mono assemblies plus
  Microsoft, Newtonsoft, ReactNative/Vsh, RabbitMQ, SharpZipLib, websocket-sharp
  and other assemblies. A single Mono license cannot clear the whole tree.
- Status: `BLOCKED`. Exact source commits, build recipes, per-file licenses,
  required notices, and redistribution authority are not recorded.

### PS4 native/system modules — no redistribution grant found

The active staging tree contains `libc.prx`, `libSceFios2.prx`,
`libmonosgen-2.0.prx`, `libSDL2.sprx`, and `sce_sys/about/right.sprx`.
No file in the reviewed tree establishes redistribution permission for the
Sony-named modules or `right.sprx`. Status: `BLOCKED`; remove them from any
public artifact unless the distributor can document a valid redistribution
grant. A jailbreak/homebrew use case does not itself supply such a grant.

### Artwork and other assets — authorship unknown

The package stages 21 files from `assets/` plus `icon0.png`, `pic0.png`
and `pic1.png` under `sce_sys/`. No authorship, source, or asset license record
was found. Status: `BLOCKED` pending first-party authorship confirmation or a
third-party license record for every asset.

### Packaging tools — repository-only, provenance unresolved

`create-gp4.exe`, `PkgTool.Core.*`, and `LibOrbisPkg.Core.dll` are build-time
tools and are not copied into the PKG by `build-sdl.ps1`. They are nevertheless
present in the repository. Their redistribution status was not established in
this pass. Status: `BLOCKED` for inclusion in a public source archive; omit them
or add verified provenance and notices.

## Release rule

A clean-room/public release should be generated from an explicit allowlist.
Absence from this notice is not approval. Unknown or unreviewed inputs default
to `BLOCKED`, and package-source results/content are not bundled merely because
the application can discover them.
