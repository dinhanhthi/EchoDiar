# EchoDiar for Windows

The Windows port of EchoDiar: a native WinUI 3 app on .NET 10, packaged as a single-project MSIX for x64 and ARM64 (Windows 11, 10.0.22000+). It reads and writes the same library layout and JSON as the macOS app. The macOS app and its setup are described in [CONTRIBUTING.md](../CONTRIBUTING.md).

## Layout

```
windows/
  EchoDiar.slnx                 solution (platforms x64, ARM64)
  global.json                   .NET SDK 10.0.100, rollForward latestFeature
  Directory.Build.props         nullable, implicit usings, warnings as errors
  Directory.Packages.props      central package versions
  src/EchoDiar.Core/            net10.0: models, JSON, settings, library store, CAF, ring buffer,
                                channel writer, recording engine, merger (portable, runs on macOS)
  src/EchoDiar.Platform/        net10.0-windows10.0.26100.0: WASAPI mic and loopback capture,
                                audio sessions, process info, shell files (CsWin32 interop)
  src/EchoDiar.App/             WinUI 3 app, single-project MSIX, win-x64 + win-arm64
  tests/EchoDiar.Tests/         net10.0 xUnit v3 tests for Core, plus the cross-platform Fixtures/
  scripts/vm-install.ps1        installs a CI build in a Windows VM
  scripts/qa-fixtures.sh        makes the QA audio and model fixtures on the Mac
  scripts/release.sh            from the Mac: dev builds (--dev), the release channel check (--preflight) and
                                the Windows step of a release (--attach)
  scripts/serve-update.sh       serves a local App Installer feed to test updates in the VM
  packaging/                    the release workflow's feed template, the release notes' Windows block
                                (release-notes.md) and the pinned certificates (dev-cert.sha256, release-cert.sha256)
  tools/make-icons.sh           re-run after a logo change: regenerates App/Assets from assets/logo.png
```

ProjectReference graph (no cycles):

- Core: no project references (NAudio.Core package only).
- Platform → Core.
- App → Platform (Core comes transitively). App also carries the native engine packages (Whisper.net, sherpa-onnx, LLamaSharp) so their DLLs ship in the MSIX.
- EchoDiar.Tests → Core. Tests never reference Platform or App, so they build and run on macOS.

Interfaces that Platform implements live in Core. CsWin32 types stay `internal` to Platform; App calls only Platform's public wrappers.

## Prerequisites

- .NET 10 SDK. On the Mac it is used as `~/.dotnet/dotnet` (installed with `dotnet-install.sh`).
- macOS can build and test Core and EchoDiar.Tests only. Platform, App and the MSIX target Windows and build on Windows (in practice on GitHub Actions, see [CI](#ci)).
- Swift toolchain (Xcode) for the cross-platform fixture test in `Packages/EchoKit`.

## Tests

From the repo root, on the Mac:

```sh
~/.dotnet/dotnet test windows/tests/EchoDiar.Tests/EchoDiar.Tests.csproj --filter "Category!=Native&Category!=NativeLLM"
```

Expect 0 failed. The filter skips the native engine test categories. The first run restores NuGet packages (about 100 MB, needs network). Tests always use a temp root: on macOS the default `AppPaths` root is the real `~/Library/Application Support/EchoDiar` library.

### Cross-platform fixtures

The file-format contract with the macOS app is checked both ways through [tests/EchoDiar.Tests/Fixtures/](tests/EchoDiar.Tests/Fixtures/): C# reads `macos-written.json` and `macos-audio.caf`, and Swift ([CrossPlatformFixturesTests.swift](../Packages/EchoKit/Tests/EchoCoreTests/CrossPlatformFixturesTests.swift)) reads `windows-written.json` and `windows-sine.caf`.

To regenerate the fixtures after a format change, write them with `ECHODIAR_WRITE_FIXTURES=1` on each side, then run both test suites without it:

```sh
# C#: writes windows-written.json and windows-sine.caf into the source Fixtures/ folder
ECHODIAR_WRITE_FIXTURES=1 ~/.dotnet/dotnet test windows/tests/EchoDiar.Tests/EchoDiar.Tests.csproj --filter "Category!=Native&Category!=NativeLLM"

# Swift: writes macos-written.json
cd Packages/EchoKit && ECHODIAR_WRITE_FIXTURES=1 swift test --build-system native --filter CrossPlatformFixturesTests

# Check: both sides read the other's fixtures
cd Packages/EchoKit && swift test --build-system native --filter CrossPlatformFixturesTests
```

With the variable set, the output must show the writer tests ran, not "skipped". `--build-system native` is required: the default build system fails to link `EchoTranscribeTests`.

## CI

[.github/workflows/windows.yml](../.github/workflows/windows.yml) runs on pushes to `main` and the plan branches in its `on.push.branches` that touch `windows/**` or the workflow file, on pull requests touching the same paths, and on `workflow_dispatch`. Jobs:

- `test` (windows-latest) and `test-arm64` (windows-11-arm): the same `dotnet test` and filter as above, run from `windows/` so `global.json` applies.
- `package` (matrix x64, arm64): builds the Debug MSIX with MSBuild (`SideloadOnly`, no bundle, manifest Version `0.0.<run number>.0`) and signs it with the dev certificate from the `windows-dev-signing` environment (pull requests run without the environment and build unsigned). A package contents check then fails the job unless every native DLL (Whisper.net, sherpa-onnx, LLamaSharp) is in the `.msix` for the job's architecture, and the only `onnxruntime.dll` is sherpa-onnx's (the Windows App SDK metapackage is not used for this reason: its ML component ships another `onnxruntime.dll`).

Each `package` job uploads the artifact `EchoDiar-win-<arch>` (kept 14 days): the `.msix`, the public `EchoDiarDev.cer` and `vm-install.ps1`. The private key never leaves the runner; staging fails if a `.pfx` or `.p12` lands in the artifact.

Download the ARM64 artifact of the latest run on `main` (or another branch of the workflow):

```sh
RUN=$(gh run list --workflow windows.yml --branch main --limit 1 --json databaseId -q '.[0].databaseId')
gh run download "$RUN" -n EchoDiar-win-arm64 -D ~/Downloads/EchoDiar-win/$RUN
```

## Testing in a Windows VM

The VM only installs and runs CI artifacts. Never copy the source, build, or install an SDK inside it. The Mac's `~/Downloads` shows up in the VM as `Z:\Downloads`.

1. Download the artifact as above.
2. In an elevated (administrator) PowerShell, trust the dev certificate (`LocalMachine\TrustedPeople`), once per certificate:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File Z:\Downloads\EchoDiar-win\<RUN>\vm-install.ps1 -Folder Z:\Downloads\EchoDiar-win\<RUN> -TrustCert
   ```

   An elevated session may not see the `Z:` drive. If the script says the folder is not reachable, copy the folder to `C:\EchoDiarQA\builds\<RUN>` and pass that path.
3. As the normal user, install and launch:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File Z:\Downloads\EchoDiar-win\<RUN>\vm-install.ps1 -Folder Z:\Downloads\EchoDiar-win\<RUN> -Install -Launch
   ```

   `-Install` picks the `.msix` for the machine's architecture, stops a running EchoDiar, and installs over any earlier version.

Notes:

- The package declares a framework dependency on `Microsoft.VCLibs.140.00.UWPDesktop` (the MSVC runtime the Whisper.net and LLamaSharp natives need). Machines with Store apps usually have it. If the install fails for that dependency, put the framework `.appx` (Windows SDK, `ExtensionSDKs\Microsoft.VCLibs.Desktop`) in a `Dependencies\<arch>\` folder next to the `.msix`: `-Install` passes every package found there as a dependency. Current artifacts stage none.
- The first microphone use shows the Windows microphone consent dialog. Allow it once; afterwards it is controlled in Settings > Privacy & security > Microphone.
- [scripts/vm-install.ps1](scripts/vm-install.ps1) runs on Windows PowerShell 5.1 and exits non-zero with a message on every failure.

### QA fixtures

[scripts/qa-fixtures.sh](scripts/qa-fixtures.sh) runs on the Mac and writes `~/Downloads/EchoDiarQA/` (override with `ECHODIAR_QA_DIR`), which the VM sees as `Z:\Downloads\EchoDiarQA`:

- `sample.wav` (one voice) and `two-voice.wav` (two voices, at least 20 s), 16 kHz mono 16-bit.
- `models/`: `ggml-tiny.bin`, the pyannote segmentation `model.onnx` and the campplus speaker embedding `.onnx`, pinned to Hugging Face commits and SHA-256 checked. Files with the right hash are skipped on a re-run.

```sh
bash windows/scripts/qa-fixtures.sh
```

Play a fixture in the VM (for system audio capture):

```powershell
powershell -c "(New-Object Media.SoundPlayer 'Z:\Downloads\EchoDiarQA\two-voice.wav').PlaySync()"
```

### DEBUG harness

Debug builds (every CI package) accept headless harness arguments: no window opens and the app exits when done. The app is on the PATH as the `echodiar.exe` execution alias. Stop a running EchoDiar first:

```powershell
Get-Process EchoDiar* -ErrorAction SilentlyContinue | Stop-Process -Force
echodiar.exe --capture-probe 10 all
echodiar.exe --native-probe Z:\Downloads\EchoDiarQA\models
```

- `--capture-probe <seconds> <all|pid:N|exe:name.exe>`: records the mic and the system source (`all` = every process except EchoDiar's own, `pid:N` or `exe:name.exe` = that process tree) for that many seconds. Writes `Probes\<yyyyMMdd-HHmmss>\mic.caf`, `system.caf` and `probe-result.json`.
- `--native-probe <modelsDir>`: loads Whisper (`ggml-tiny.bin` on `sample.wav`), sherpa-onnx diarization (on `two-voice.wav`) and llama.cpp once each, with the WAV files read from `<modelsDir>\..` (the QA fixtures layout). Writes `Probes\native-<yyyyMMdd-HHmmss>.json`.

`Probes\` is `%LOCALAPPDATA%\EchoDiar\Probes`. Every Debug launch also writes `Probes\hello.txt`, which checks that the data root is the real path.

## Data root

The library lives at the real `%LOCALAPPDATA%\EchoDiar`, with the same layout and JSON as macOS `~/Library/Application Support/EchoDiar` (`settings.json`, `Conversations\`, `Models\`, `RAG\`, `GlobalChat\`, plus `Probes\` in Debug). [Package.appxmanifest](src/EchoDiar.App/Package.appxmanifest) turns file write virtualization off (`desktop6:FileSystemWriteVirtualization` disabled, `rescap:unvirtualizedResources`), so the library survives uninstalling the app and other processes (Explorer, the Recycle Bin) see the real files.

## Dev signing certificate (one-time)

CI signs the MSIX with a self-signed dev certificate stored in two secrets of the GitHub environment `windows-dev-signing`, `WINDOWS_DEV_CERT_PFX` (the base64 PFX) and `WINDOWS_DEV_CERT_PASSWORD` ([docs/RELEASING.md, Protect the signing secrets](../docs/RELEASING.md#protect-the-signing-secrets)). They are not repository secrets. The certificate subject must be exactly `CN=EchoDiar Dev`, the `Publisher` of the manifest `Identity`, or signing fails. To create or rotate it, on the Mac:

```sh
cd "$(mktemp -d)"
openssl req -x509 -newkey rsa:3072 -sha256 -days 730 -nodes -subj "/CN=EchoDiar Dev" \
  -addext "basicConstraints=critical,CA:FALSE" -addext "keyUsage=critical,digitalSignature" \
  -addext "extendedKeyUsage=codeSigning" -keyout key.pem -out cert.pem

export PFX_PASSWORD="$(openssl rand -base64 24)"
mkdir -p ~/.config/echodiar
openssl pkcs12 -export -inkey key.pem -in cert.pem -out ~/.config/echodiar/windows-dev-cert.pfx -passout env:PFX_PASSWORD
chmod 600 ~/.config/echodiar/windows-dev-cert.pfx
rm key.pem cert.pem

# Keep the password in the Keychain (read it back with: security find-generic-password -s echodiar-windows-dev-cert -a ci -w)
security add-generic-password -U -s echodiar-windows-dev-cert -a ci -w "$PFX_PASSWORD"

base64 -i ~/.config/echodiar/windows-dev-cert.pfx | gh secret set WINDOWS_DEV_CERT_PFX --env windows-dev-signing
printf '%s' "$PFX_PASSWORD" | gh secret set WINDOWS_DEV_CERT_PASSWORD --env windows-dev-signing
unset PFX_PASSWORD
```

Check with `gh secret list --env windows-dev-signing` and:

```sh
PFX_PASSWORD="$(security find-generic-password -s echodiar-windows-dev-cert -a ci -w)" \
  openssl pkcs12 -in ~/.config/echodiar/windows-dev-cert.pfx -nokeys -passin env:PFX_PASSWORD | openssl x509 -noout -subject
```

The private key and the PFX never go in git. After a rotation, update the pin [packaging/dev-cert.sha256](packaging/dev-cert.sha256) (the lowercase SHA-256 of the new certificate's DER, e.g. `shasum -a 256 EchoDiarDev.cer` on a new CI artifact) and commit it: `release.sh --dev` refuses any other signer. Then run `vm-install.ps1 -TrustCert` again in every VM with the new `EchoDiarDev.cer`.

## Release signing certificate (one-time)

Releases are signed with a separate self-signed release certificate (subject `CN=Anh-Thi Dinh`, the `WINDOWS_PUBLIC_PUBLISHER` variable), never the dev one. Its PFX lives in `~/.config/echodiar/windows-release-cert.pfx` on your Mac, in the secrets of the protected GitHub environment `windows-release-signing` and in your offline backup, and its pin is `packaging/release-cert.sha256` (committed by that setup). Create it once, with the environment, the secrets, the variable and the `v*` tag ruleset, as in [docs/RELEASING.md, §3.0 One-time setup](../docs/RELEASING.md#30-one-time-setup); then `windows/scripts/release.sh --preflight` must pass. `/cf-ship` refuses to start until it does.

## Releasing and updating

[docs/RELEASING.md, §3 Windows](../docs/RELEASING.md#3-windows) has the full steps, the one-time setup and the rollback. macOS and Windows share one version and one GitHub Release `vX.Y.Z`. In short:

- `/cf-ship` (or `scripts/ship.sh`) ships macOS first. The tag `vX.Y.Z` it pushes runs [.github/workflows/windows-release.yml](../.github/workflows/windows-release.yml) on the release channel: unsigned Release builds for x64 and ARM64, then a `sign` job (the only one with the release key) that signs both packages and bundles them into one signed `EchoDiar_X.Y.Z.0.msixbundle`, then an `assemble` job that stages the signed bundle and the public `EchoDiar.cer`, renders `EchoDiar.appinstaller` from [packaging/EchoDiar.appinstaller.template](packaging/EchoDiar.appinstaller.template) and writes `SHA256SUMS-windows.txt`: four files.
- The last ship step, [scripts/release.sh](scripts/release.sh) `--attach vX.Y.Z --yes`, approves that run, downloads the four files into `release/windows/vX.Y.Z/`, checks them (signers against the pin), uploads the ones the release lacks (never overwriting) and commits the feed `windows/EchoDiar.appinstaller` on `main`. It is idempotent: when it fails, re-run it.
- The bundle is not signed by a certificate authority, so users first import the release certificate `EchoDiar.cer` (once per PC), then open the feed `EchoDiar.appinstaller` from the release, following the steps on the [website](https://ed.dinhanhthi.com/docs.html#install-windows).
- Installed apps update through App Installer from `https://ed.dinhanhthi.com/windows/EchoDiar.appinstaller`, the feed committed on `main` and deployed by the Pages workflow. The website shows its Windows download only once that file is live, and takes the release tag of its download link from it.
- Dev builds: `scripts/release.sh --dev --version 0.0.N` (N above the CI run number) builds the dev channel (tag `windows-v0.0.N-dev`, dev certificate) through the same jobs, and `--publish` creates a prerelease, never Latest. Its feed's Uri is that prerelease's own `EchoDiar.appinstaller` (App Installer always reads a feed from its Uri), so opening it installs that prerelease, and its installs check that same feed for updates.
- In the app, [Updater](src/EchoDiar.App/Services/Updater.cs) checks at launch and every 6 hours, only in a copy installed from an `.appinstaller` (a sideloaded `.msix`, like the CI builds above, never updates). An available update shows the toolbar's Update button; the tray menu has Check for updates…, and Settings > About shows the last check.
- To test an update in the VM without a release, [scripts/serve-update.sh](scripts/serve-update.sh) serves one signed package from `~/Downloads/EchoDiar-update/` with a rendered feed on port 8765 (only on the Mac's VM bridge address or the IPv4 `--host`; only the package, the feed and `EchoDiarDev.cer`; HTTP Range requests as App Installer makes them); install it in the VM with `Add-AppxPackage -AppInstallerFile http://<host>:8765/EchoDiar.appinstaller`.

## Constraints

- Never enable `PublishTrimmed` or NativeAOT for the App: Platform uses built-in COM interop (CsWin32 classic `[ComImport]` interfaces), which trimming and AOT break.
- Test projects use `xunit.v3.mtp-off`, not `xunit.v3`: the Microsoft Testing Platform runner of `xunit.v3` refuses `dotnet test` in VSTest mode on .NET 10, and the `--filter` syntax above needs VSTest.
- The App references the Windows App SDK component packages, never the `Microsoft.WindowsAppSDK` metapackage (see [CI](#ci)).

## Build cache

The macOS shared caches (the shared Cargo target dir, Xcode's DerivedData) do not apply here. .NET builds into each project's `bin/` and `obj/`, and NuGet uses its default per-user package cache (`~/.nuget/packages`, `%USERPROFILE%\.nuget\packages` on Windows). There is no repo `nuget.config`.
