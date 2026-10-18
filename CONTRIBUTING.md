# Contributing to EchoDiar

Thanks for helping. EchoDiar is in early development: please open an issue before larger changes.

## Prerequisites

- macOS 15+ on Apple Silicon to run the app.
- Xcode 27 to compile it (the app icon is an Icon Composer `.icon`).
- XcodeGen:

  ```sh
  brew install xcodegen
  ```

## Setup

Generate the Xcode project:

```sh
xcodegen generate
```

`project.yml` is the source of truth. `EchoDiar.xcodeproj` is generated from it and gitignored: change settings, targets and packages in `project.yml`, never in the `.xcodeproj`.

## Run

```sh
scripts/dev.sh           # xcodegen generate, Debug build, launch
scripts/dev.sh --clean   # clean build first
```

Debug is signed with the "Developer ID Application" identity, like Release, so the Microphone and System Audio Recording grants survive rebuilds. Without that certificate, override `CODE_SIGN_STYLE`, `CODE_SIGN_IDENTITY` and `DEVELOPMENT_TEAM` on the `xcodebuild` command line with your own identity.

### Testing permissions

macOS (TCC) gives the Microphone and System Audio Recording grants to the app that launched the process. Test permissions only with an app launched from Finder or with `open`, never from Xcode's Run or by running the binary from Terminal: those grants go to Xcode or Terminal, not to EchoDiar.

## VM testing

```sh
scripts/vm-test.sh                              # build, install and launch in the VMPal test VM
scripts/vm-test.sh --fixtures                   # also make the audio fixtures and copy them to ~/EchoDiarQA
scripts/vm-test.sh --reset-tcc                  # reset the Microphone and System Audio grants first
scripts/vm-test.sh --args "--force-fallbacks"   # pass arguments to the app
```

The script builds Debug on this Mac, zips the app with `ditto`, copies it into `~/EchoDiarQA` in the VM, unzips it there and launches it with `open`. Nothing is ever built inside the VM. It needs VMPal with agent control turned on for the test VM (the VM id is set at the top of the script).

## Tests

```sh
xcodebuild test -project EchoDiar.xcodeproj -scheme EchoDiar
swift test --package-path Packages/EchoKit
```

Tests use Swift Testing and live in `Packages/EchoKit/Tests/`. They cover the guarded behaviors only: settings store, crash recovery of an interrupted recording, and compression (the original is deleted only after a verified encode). Tests never touch the real Application Support folder.

### DEBUG harness

Debug builds accept two launch arguments:

- `--capture-probe <seconds> [bundleID|pid:N|global]`: records a probe session, writes `probe.json` next to `Conversations/` and the same JSON to stdout, then quits. It needs the TCC grants, so launch it with `open` (`APP` is `EchoDiar.app` in the `BUILT_PRODUCTS_DIR` that `xcodebuild -showBuildSettings` prints; read the stdout file, not the exit code):

  ```sh
  open -n -W --stdout /tmp/echodiar-probe.txt -a "$APP" --args --capture-probe 5 global
  ```

- `--force-fallbacks`: forces the pre-macOS 26 code paths (no Liquid Glass, process taps without bundle IDs), to check them on macOS 26+.

## Xcode cache

Never pass `-derivedDataPath` to local `xcodebuild` builds and tests. The default DerivedData folder is the one Xcode uses, so Xcode's Run reuses the CLI build, and the app path stays stable so the permission grants survive rebuilds.

## Code layout

- `EchoDiar/`: the app target (SwiftUI and AppKit): window, menu bar item, onboarding, library list, settings. `EchoDiar/Info.plist` holds the permission prompts.
- `Packages/EchoKit/`: Swift package with the app's modules.
  - `EchoCore`: settings, recording session and conversation models, library store. No dependencies.
  - `EchoAudio`: process tap, mic capture, channel writer, recording engine, crash recovery, AAC compression. Depends on EchoCore.
  - Planned: `EchoTranscribe` (transcription, speaker separation), `EchoAI` (AI providers, summary), `EchoLocalLLM` (on-device models).
- `assets/`: `logo.icon` (app icon) and `logo.png` (README and About panel).
- `scripts/`: `dev.sh` and `vm-test.sh`.

## Commit style

One-line conventional commits: `<type>(<scope>): <summary>`, for example `fix(audio): keep the CAF when the encode fails`. Types: `feat`, `fix`, `docs`, `style`, `refactor`, `test`, `chore`. No body.

## License

By contributing, you agree that your contributions are licensed under the [GNU AGPL v3.0](LICENSE).
