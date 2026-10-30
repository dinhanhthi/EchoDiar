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

Debug builds accept these launch arguments:

- `--capture-probe <seconds> [bundleID|pid:N|global]`: records a probe session, writes `probe.json` next to `Conversations/` and the same JSON to stdout, then quits. It needs the TCC grants, so launch it with `open` (`APP` is `EchoDiar.app` in the `BUILT_PRODUCTS_DIR` that `xcodebuild -showBuildSettings` prints; read the stdout file, not the exit code):

  ```sh
  open -n -W --stdout /tmp/echodiar-probe.txt -a "$APP" --args --capture-probe 5 global
  ```

- `--force-fallbacks`: forces the pre-macOS 26 code paths (no Liquid Glass, process taps without bundle IDs), to check them on macOS 26+.

- `--measure <caf> --out <dir> [--engine <catalog id>] [--diarizer <catalog id>] [--language <code>] [--pace <x>] [--diarization on|off] [--incremental on|off] [--tag <git sha>]`: copies a 2-channel 16 kHz Int16 CAF into a temp session folder (`EchoDiar-measure-<uuid>`, never `Conversations/`), runs the post-Stop sequence on it (transcription of the CAF, then compression) with in-memory settings (the saved ones are neither read nor written), writes `result.json` (Stop to transcript, Stop to compressed, pipeline steps, max footprint and CPU per phase, post-conditions) and a copy of `transcript.json` to `--out`, then quits: exit 0 when every post-condition holds, else 1 with the error on stderr. Cloud engines are refused. Defaults: the catalog's default ASR, the first installed diarizer (pyannote), English, pace 1, `--diarization on` and `--incremental off` (never the saved settings). `--incremental on` transcribes while recording: the fixture goes in 100 ms blocks at `--pace` into the recording transcriber, whose result goes to the post-Stop sequence, and `result.json` gains its stats (load and finish seconds, chunks, gap seconds, max pending seconds). Launch crash recovery and the update check are skipped. Add `--background` so no main window opens. `scripts/measure-transcribe.sh` runs it (see below).

## Measuring transcription latency

`scripts/measure-transcribe.sh` measures Stop to transcript, Stop to compressed, footprint, CPU and word error rate with the `--measure` harness, on this Mac or in the VMPal test VM:

```sh
scripts/measure-transcribe.sh fixture --minutes 15                    # make the 15-minute fixture and its reference
scripts/measure-transcribe.sh host --label baseline --runs 2          # build Debug, run the harness twice on this Mac
scripts/measure-transcribe.sh vm --label baseline                     # build Debug here, run the harness in the VM
scripts/measure-transcribe.sh vm --label baseline --install-models    # also copy the models into the VM first
scripts/measure-transcribe.sh report <result dir>...                  # one Markdown table row per run, with WER
```

`fixture` speaks `scripts/fixtures/measure-meeting.txt` with `say` into `~/EchoDiarQA/measure/fixture-<N>m.caf` plus `reference-<N>m.json`. `host` and `vm` take `--minutes` (default 15) to pick the fixture, `--runs` (default 1) and, after `--`, extra harness arguments (`-- --engine <catalog id>`); each run lands in `~/EchoDiarQA/measure/results/<label>-host-<n>` or `<label>-vm-<n>` and both end with the `report` table. `--install-models` copies whisperkit-large-v3-turbo-626 and pyannote-community-1 from this Mac's models folder into the VM (skipped when the VM has them).

- Quit EchoDiar before `host`: the script refuses while it runs (in the VM too, for `vm`).
- Run host and VM one after the other, never at once: both share this Mac's CPU and memory.
- Nothing is built inside the VM: the script builds on this Mac and copies the zipped app and the fixture in, as `scripts/vm-test.sh` does.
- Before the first VM run, do a 1-minute warm-up (`fixture --minutes 1`, then `vm --label warmup --minutes 1 --install-models`): the first launch compiles the CoreML models, which would inflate the first real run.
- VM numbers are only good for before/after ratios: the VM has no Neural Engine, so its absolute times say nothing about a real Mac.
- CPU and footprint cover the EchoDiar process only: work done in other processes (such as CoreML's compiler) is not counted.
- Apple Speech live: `scripts/measure-transcribe.sh host --label apple-live -- --engine apple-speech --incremental on --diarization off` (the QA runs used no diarization). Its live text is display only, so `priorReturned` is `false`, the gaps show as n/a, "chunks done" counts its final lines, and the WER is the batch transcript's, made after Stop. `result.json` has no live latency: add a temporary `NSLog` where `RecordingTranscriber` publishes a tentative or final line (never commit it) and compute (samples seen at publish − phrase start or end sample) / 16000 / pace, with phrases cut by `SpeechChunker`'s energy VAD (start = speech onset, end = last speech frame before a 0.5 s pause). Apple Speech does not run in VMPal VMs (`SpeechTranscriber.isAvailable` is false there), so measure it on a real Mac with `host`, not `vm`.

## Xcode cache

Never pass `-derivedDataPath` to local `xcodebuild` builds and tests. The default DerivedData folder is the one Xcode uses, so Xcode's Run reuses the CLI build, and the app path stays stable so the permission grants survive rebuilds.

## Localization

The UI is in English (source), Vietnamese, French, Spanish and Simplified Chinese (`vi`, `fr`, `es`, `zh-Hans`). The user picks one in Settings › General › App language, which applies after a relaunch.

- `EchoDiar/Localizable.xcstrings` is the source of truth. Keys are the English text; write whole sentences (`String(localized: "Delete \(count) conversations?")`), not English words spliced into other sentences.
- SwiftUI string literals are localized; a `String` passed to `Text`, `Label` or `Section`, an `NSMenuItem` title or an `NSAlert` text is not, so use `String(localized:)`, `LocalizedStringKey` or `LocalizedStringResource` there.
- CLI `xcodebuild` builds never add new keys to the catalog (only Xcode IDE builds do). After a Debug CLI build, run `scripts/sync-strings.sh` to add them, as Xcode would. Never add keys by hand.
- `xcstringstool` sorts a few keys differently from Xcode (punctuation and case), so a sync after an IDE build, or the other way round, can reorder them without changing their content.
- To try a language without the picker: `defaults write dinh.thi.EchoDiar AppleLanguages '("fr")'`, relaunch, then `defaults delete dinh.thi.EchoDiar AppleLanguages` (and `AppleLocale`, which the picker also sets).
- EchoKit's error texts, Info.plist permission prompts and the default conversation titles stay English.

## Code layout

- `EchoDiar/`: the app target (SwiftUI and AppKit): window, menu bar item, onboarding, library list, settings. `EchoDiar/Info.plist` holds the permission prompts.
- `Packages/EchoKit/`: Swift package with the app's modules.
  - `EchoCore`: settings, recording session and conversation models, library store. No dependencies.
  - `EchoAudio`: process tap, mic capture, channel writer, recording engine, crash recovery, AAC compression. Depends on EchoCore.
  - Planned: `EchoTranscribe` (transcription, speaker separation), `EchoAI` (AI providers, summary), `EchoLocalLLM` (on-device models).
- `assets/`: `logo.icon` (app icon), `logo_square*.png` (README, About panel, website) and `logo_white.png` / `logo_black.png` (menu bar, dark / light).
- `scripts/`: `dev.sh`, `vm-test.sh`, `measure.sh` (memory and CPU targets), `measure-transcribe.sh` (transcription latency, with `fixtures/measure-meeting.txt`), and the release scripts `build-and-notarize.sh`, `bump-version.sh`, `publish-release.sh`, `publish-appcast.sh`, `ship.sh` (see docs/RELEASING.md).

## Commit style

One-line conventional commits: `<type>(<scope>): <summary>`, for example `fix(audio): keep the CAF when the encode fails`. Types: `feat`, `fix`, `docs`, `style`, `refactor`, `test`, `chore`. No body.

## Releasing

Releases are built, signed, notarized and published from a maintainer's Mac, not in CI. Run `/cf-ship` (guide: `.coding-friend/skills/cf-ship-custom/SKILL.md`): it picks the version, writes the `CHANGELOG.md` section, commits `chore(release): vX.Y.Z` and runs `scripts/ship.sh`, which chains `build-and-notarize.sh`, `publish-release.sh` and `publish-appcast.sh`. See [docs/RELEASING.md](docs/RELEASING.md) for the one-time setup (Developer ID, notary profile, Sparkle key), the manual steps and rollback.

## License

By contributing, you agree that your contributions are licensed under the [GNU AGPL v3.0](LICENSE).
