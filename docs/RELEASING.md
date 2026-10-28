# Releasing EchoDiar

The macOS app is built, signed, notarized and published from your own Mac. The Windows app is built and signed on GitHub Actions by `.github/workflows/windows-release.yml`, which the release's tag `vX.Y.Z` starts; `scripts/ship.sh` then attaches its files to the same GitHub Release from your Mac ([§3 Windows](#3-windows)). `.github/workflows/pages.yml` publishes the website, `appcast.xml` and the Windows feed `windows/EchoDiar.appinstaller` to GitHub Pages, so installed apps find updates at `https://ed.dinhanhthi.com/appcast.xml` (`SUFeedURL`) and `https://ed.dinhanhthi.com/windows/EchoDiar.appinstaller`.

macOS and Windows share one version and one GitHub Release `vX.Y.Z`, marked Latest. `scripts/bump-version.sh` sets that version for both apps. A release whose changes concern only one OS still ships both, with the same version.

Each release ships two notarized macOS assets on the GitHub Release:

- `EchoDiar-X.Y.Z.dmg`: drag-to-install download for new users.
- `EchoDiar-X.Y.Z.zip`: the Sparkle update archive referenced by the appcast.

The five Windows files (`EchoDiar-Setup.exe` and the others, [§3](#3-windows)) are attached to the same release a while later.

EchoDiar is Apple Silicon only. `build-and-notarize.sh` passes `ARCHS=arm64` on the `xcodebuild` command line (speech-swift's Float16 code does not build for x86_64, and SPM package targets ignore a project-level `ARCHS`) and refuses an app whose `lipo -archs` is not exactly `arm64`. Pass `ARCHS=arm64` to any manual Release build too.

## §0 One-time setup

Never paste passwords, app-specific passwords or private keys into an AI chat. Run the commands below in your own terminal.

### Tools

- Xcode 27+ selected (`sudo xcode-select -s /Applications/Xcode.app`); older Xcode ships a blank app icon (the icon is an Icon Composer `.icon`) and the build script refuses it.
- XcodeGen: `brew install xcodegen`.
- GitHub CLI: `brew install gh`, then `gh auth login`.

### Apple Developer account and Developer ID certificate

- Paid Apple Developer account, Team ID `86H6CNLN4C` (see developer.apple.com/account > Membership details).
- Create a "Developer ID Application" certificate in Xcode > Settings > Accounts > select the team > Manage Certificates > + > Developer ID Application. It must be in the login Keychain.
- Verify:

  ```sh
  security find-identity -v -p codesigning
  ```

  The list must contain `Developer ID Application: ... (86H6CNLN4C)`.

### Notarization credentials

1. Create an app-specific password at account.apple.com > Sign-In and Security > App-Specific Passwords.
2. Store it in a notarytool Keychain profile (notarytool prompts for the app-specific password interactively, so it never lands in shell history or `ps`):

   ```sh
   xcrun notarytool store-credentials echodiar-notary --apple-id <email> --team-id 86H6CNLN4C
   ```

To reuse an existing profile of the same team instead (for example one created for another app), point the scripts at it, on the same command line as the script:

```sh
ECHODIAR_NOTARY_PROFILE=<name> scripts/ship.sh
```

Check the profile with `xcrun notarytool history --keychain-profile echodiar-notary`.

### Sparkle EdDSA key

Sparkle only installs updates signed with the private key that matches `SUPublicEDKey` in the app. EchoDiar uses its own Keychain account (`EchoDiar`), so it never reuses another app's key stored under Sparkle's default account.

1. Build the app once in Debug (`scripts/dev.sh`) so Xcode downloads the Sparkle tools into DerivedData.
2. Generate the key pair (the private key goes into the login Keychain; only the public key is printed):

   ```sh
   G=$(find ~/Library/Developer/Xcode/DerivedData/EchoDiar-*/SourcePackages/artifacts -name generate_keys -type f 2>/dev/null | head -1)
   "$G" --account EchoDiar
   ```

3. Paste the printed public key into `SUPublicEDKey` in `EchoDiar/Info.plist` and commit it. The updater does not start unless it is a base64 32-byte key, and `build-and-notarize.sh` refuses to build while it is empty. `"$G" --account EchoDiar -p` prints the public key again.
4. Back up the private key. Export it into a private temp dir under `umask 077` (never Desktop or Documents: they may sync to iCloud, and a file created before `chmod` is briefly world-readable), store the file's content in a password manager, then delete it:

   ```sh
   G=$(find ~/Library/Developer/Xcode/DerivedData/EchoDiar-*/SourcePackages/artifacts -name generate_keys -type f 2>/dev/null | head -1)
   d=$(mktemp -d) && (umask 077 && "$G" --account EchoDiar -x "$d/sparkle_private_key.txt")
   # copy the content of "$d/sparkle_private_key.txt" into your password manager, then:
   rm -P "$d/sparkle_private_key.txt" && rmdir "$d"
   ```

5. Restore it on another Mac (write the key from the password manager into a fresh private temp dir, import, then delete it):

   ```sh
   G=$(find ~/Library/Developer/Xcode/DerivedData/EchoDiar-*/SourcePackages/artifacts -name generate_keys -type f 2>/dev/null | head -1)
   d=$(mktemp -d) && (umask 077 && pbpaste > "$d/sparkle_private_key.txt")   # key copied from the password manager
   "$G" --account EchoDiar -f "$d/sparkle_private_key.txt"
   pbcopy </dev/null   # clear the clipboard, it still holds the private key
   rm -P "$d/sparkle_private_key.txt" && rmdir "$d"
   ```

Never print the private key in a terminal you share, and never commit it. As a safety net, `.gitignore` ignores `release/`, `sparkle_private_key*`, `*_sparkle_private_key*`, `*private_key*`, `*.p12`, `*.p8`, `*.pfx`, `*.cer`, `*.pem`, `*.key`, `*.mobileprovision`, `*.keychain*` and `.env*`; keep key files outside the repo anyway.

Losing the private key means installed apps can no longer accept updates. `generate_appcast` reads the key from the Keychain and may show a Keychain prompt: click Always Allow (same for `codesign` and `notarytool` prompts).

### Public repo and GitHub Pages

- The repo must be public: release assets, the appcast and the Pages site must be reachable without auth (`/cf-ship` stops otherwise). Make it public yourself, after checking that no secret is tracked:

  ```sh
  gh repo edit dinhanhthi/EchoDiar --visibility public --accept-visibility-change-consequences
  gh repo view dinhanhthi/EchoDiar --json visibility --jq .visibility   # PUBLIC
  ```

- In the GitHub repo, open Settings > Pages and set Source = GitHub Actions (the website deploy sets this up, with the custom domain `ed.dinhanhthi.com`). Every push of `appcast.xml` to `main` then redeploys the feed. Check:

  ```sh
  gh api repos/dinhanhthi/EchoDiar/pages --jq .build_type   # workflow
  ```

The repo root holds a valid empty `appcast.xml` (no items) so Pages deploys before the first release; installed apps then simply find no update. `publish-appcast.sh` replaces it on every release.

### Protect the release files (manual, recommended once the repo is public)

`/cf-ship` and the scripts run whatever `.coding-friend/skills/cf-ship-custom/SKILL.md`, `scripts/`, `project.yml` and `appcast.xml` say on `main`, with your signing keys. The same holds for the Windows release files `.github/workflows/windows-release.yml`, `windows/scripts/`, `windows/packaging/` and `windows/src/EchoDiar.Setup/`, with the Windows release key ([§3.0](#30-one-time-setup)). Once the repo is public, a merged PR could change them. `/cf-ship` stops when the last commit touching one of them (locally or on `origin/main`) is not authored by your `git config user.email`, but author fields can be forged, so set up these GitHub controls yourself (nothing creates them for you):

- `.github/CODEOWNERS` with you as owner of the release files:

  ```text
  /.coding-friend/             @dinhanhthi
  /scripts/                    @dinhanhthi
  /project.yml                 @dinhanhthi
  /appcast.xml                 @dinhanhthi
  /.github/                    @dinhanhthi
  /windows/scripts/            @dinhanhthi
  /windows/packaging/          @dinhanhthi
  /windows/src/EchoDiar.Setup/ @dinhanhthi
  ```

- A branch protection rule (or ruleset) on `main`: require a pull request with review from Code Owners, block force pushes and deletions, and restrict who can push to `main` to you.

## §1 Per release

### With /cf-ship (recommended)

Run `/cf-ship`. The project guide in `.coding-friend/skills/cf-ship-custom/SKILL.md` runs the §0 preflight checks and `windows/scripts/release.sh --preflight` ([§3.0](#30-one-time-setup)), picks the version, writes the `CHANGELOG.md` section, bumps the version (`scripts/bump-version.sh`), commits `chore(release): vX.Y.Z`, pushes to `main` and runs `scripts/ship.sh`. It never asks: running `/cf-ship` is the approval for the whole flow, including the `windows-release-signing` deployment of the tag's Windows run. Its summary says when a release went out macOS-only ([§3.2](#32-troubleshooting-and-rollback)).

First release: the current `MARKETING_VERSION` (0.1.0) ships as-is, without a bump, and the `## [Unreleased]` items move into `## [0.1.0] - <date>`.

### Manually

1. Bump the version (sets `MARKETING_VERSION` and increments `CURRENT_PROJECT_VERSION` in `project.yml`, and sets the Windows package version `X.Y.Z.0` in `windows/src/EchoDiar.App/Package.appxmanifest`; skip on the first release):

   ```sh
   scripts/bump-version.sh X.Y.Z
   ```

2. Move the `## [Unreleased]` items of `CHANGELOG.md` into a new `## [X.Y.Z] - YYYY-MM-DD` section (the GitHub Release notes come from it; `publish-release.sh` appends the fixed Windows install block `windows/packaging/release-notes.md`, so never copy that block into `CHANGELOG.md`).
3. Commit and push:

   ```sh
   git add -- CHANGELOG.md project.yml windows/src/EchoDiar.App/Package.appxmanifest
   git commit -m "chore(release): vX.Y.Z" -- CHANGELOG.md project.yml windows/src/EchoDiar.App/Package.appxmanifest
   git push origin main
   ```

4. Ship:

   ```sh
   scripts/ship.sh
   ```

   After its checks, `ship.sh` runs four steps:

   1. `scripts/build-and-notarize.sh`: the macOS zip and DMG.
   2. `scripts/publish-release.sh --yes <zip>`: the tag `vX.Y.Z` and the GitHub Release with the macOS assets. The tag push starts `windows-release.yml` (release channel).
   3. `scripts/publish-appcast.sh <zip>`: the appcast on `main`. macOS is fully shipped here.
   4. `windows/scripts/release.sh --attach vX.Y.Z --yes`: approves the tag's Windows run, attaches its five files to the release and commits the feed on `main` ([§3.1](#31-per-release)). It takes about 30-60 min (CI build and approval), so run `ship.sh` where it can keep running.

   When step 4 fails, macOS stays shipped: finish Windows with `windows/scripts/release.sh --attach vX.Y.Z --yes` (or `/cf-ship`), see [§3.2](#32-troubleshooting-and-rollback). To ship macOS without step 4, see [Ship macOS only by hand](#ship-macos-only-by-hand).

Outputs land in `release/export/` (gitignored): `EchoDiar.app`, `EchoDiar-X.Y.Z.zip`, `EchoDiar-X.Y.Z.dmg` and the release notes `EchoDiar-X.Y.Z.md`. The Windows files land in `release/windows/vX.Y.Z/` (gitignored).

### What each script does

- `scripts/bump-version.sh [X.Y.Z]`: edits `MARKETING_VERSION` (when given) and `CURRENT_PROJECT_VERSION` (+1) in `project.yml`, and with `X.Y.Z` also the Windows `<Identity Version="X.Y.Z.0">` in `windows/src/EchoDiar.App/Package.appxmanifest` (it fails unless there is exactly one). Sparkle only offers an update when the build number increases. Does not commit.
- `scripts/build-and-notarize.sh`: checks Xcode 27+, exactly one Developer ID Application identity of team `86H6CNLN4C` (it signs by that identity's hash), a non-empty `SUPublicEDKey`, an https `SUFeedURL` and the notary profile before anything slow; runs `xcodegen generate`; archives the Release build with `ARCHS=arm64` to `release/EchoDiar.xcarchive` (default DerivedData, so `publish-appcast.sh` finds the Sparkle tools); checks the arm64-only binary, signing, hardened runtime, no `get-task-allow`, macOS 15.0 minimum, Sparkle.framework and the compiled app icon; re-signs nested code inside-out with Developer ID (Sparkle's XPC services, Autoupdate and Updater.app, the SPM resource bundles such as the MLX metallib bundle and the WhisperKit, FluidAudio and speech-swift bundles, frameworks and dylibs), then the app with its entitlements, and checks that the app and every nested item carry `TeamIdentifier=86H6CNLN4C`; prints the embedded Sparkle version; notarizes and staples the app, zips it; builds, signs, notarizes and staples the DMG.
- `scripts/publish-release.sh [--yes] [<zip>]`: reads the version from the committed `project.yml` (the zip defaults to `release/export/EchoDiar-<version>.zip`), requires branch `main`, `gh` auth, `origin` = `github.com/dinhanhthi/EchoDiar`, `HEAD` already on `origin/main`, a new tag and a `## [X.Y.Z]` section in `CHANGELOG.md`; writes that section, followed by the Windows install block `windows/packaging/release-notes.md` (read from `HEAD`), to `release/export/EchoDiar-X.Y.Z.md`; creates and pushes tag `vX.Y.Z` (which starts `windows-release.yml`); creates the GitHub Release with the zip and uploads the DMG.
- `scripts/publish-appcast.sh <zip>`: needs a zip named `EchoDiar-X.Y.Z.zip` and verifies it before signing anything: same SHA-256 as `release/export/EchoDiar-X.Y.Z.zip` when that file exists and is not the zip given, and always (after unzipping to a temp dir) team `86H6CNLN4C` (`codesign -dvvv`), `codesign --verify --deep --strict`, "Notarized Developer ID" (`spctl -a -t exec -vv`) and `CFBundleShortVersionString` == `X.Y.Z`. It then runs Sparkle's `generate_appcast --account EchoDiar --maximum-versions 0 --maximum-deltas 0` (keeps every past item, no deltas) over a fresh `release/appcast-archives/` holding the new zip plus the existing `appcast.xml`, with the release asset URL as download prefix and `CHANGELOG.md` on `main` as the full release notes link; checks that the new item's enclosure URL is `https://github.com/dinhanhthi/EchoDiar/releases/download/vX.Y.Z/EchoDiar-X.Y.Z.zip` and carries a `sparkle:edSignature`, prints the Sparkle version of the tools, copies the result to the repo root, commits `chore(release): appcast vX.Y.Z` and pushes; the Pages workflow deploys it in about a minute.
- `scripts/ship.sh`: before building, refuses a branch other than `main`, `HEAD` != `origin/main` (after `git fetch origin main`: only pushed commits ship), `gh` not authenticated, uncommitted changes to tracked files, no `## [X.Y.Z]` section in `CHANGELOG.md`, an existing tag `vX.Y.Z` (local or on origin), or a Windows release channel that `windows/scripts/release.sh --preflight` rejects ([§3.0](#30-one-time-setup)); then runs the three scripts above in order (it aborts if `HEAD` changes during the build), and last `windows/scripts/release.sh --attach vX.Y.Z --yes`. When that step fails, it says that macOS is fully shipped and how to finish Windows, and exits non-zero.
- `windows/scripts/release.sh --attach vX.Y.Z [--yes]`: approves the release channel run of the tag, checks its files, uploads the ones the release lacks and commits the feed ([§3.1](#31-per-release)). Safe to re-run.

Verify a release:

```sh
curl -fsSL https://ed.dinhanhthi.com/appcast.xml | grep shortVersionString
gh release view vX.Y.Z --repo dinhanhthi/EchoDiar --json assets --jq '.assets[].name'   # the 2 macOS and 5 Windows files
curl -fsSL https://ed.dinhanhthi.com/windows/EchoDiar.appinstaller | grep -E 'Version=|Uri='
```

## §2 Troubleshooting and rollback

- Notarization not Accepted: `build-and-notarize.sh` prints the submission id. Read the log, which names the binary that failed:

  ```sh
  xcrun notarytool log '<id>' --keychain-profile '<profile>'
  ```

- Gatekeeper check of the app or DMG:

  ```sh
  spctl -a -vvv release/export/EchoDiar.app
  spctl -a -vvv -t open --context context:primary-signature release/export/EchoDiar-X.Y.Z.dmg
  ```

  The app must report `source=Notarized Developer ID`. A local Release build that was never notarized reports `rejected` with `source=Unnotarized Developer ID`; that is expected. If Gatekeeper still blocks a downloaded copy, check that the release assets are the stapled files from `release/export/`.
- `no 'Developer ID Application' identity`: create the certificate (§0) and check `security find-identity -v -p codesigning`.
- `notarytool credential profile ... is missing`: run `store-credentials` (§0) or set `ECHODIAR_NOTARY_PROFILE`.
- `SUPublicEDKey is empty`: generate the Sparkle key (§0) and paste the public key into `EchoDiar/Info.plist`.
- `generate_appcast not found`: build the project once (`scripts/dev.sh`) so DerivedData holds the Sparkle tools.
- Build fails on x86_64 or the arch check fails: the build was run without `ARCHS=arm64` on the command line.
- `tag vX.Y.Z already exists`: if the release is half-shipped (tag pushed but assets or appcast entry missing), finish it: if the GitHub Release itself is missing, `gh release create vX.Y.Z release/export/EchoDiar-X.Y.Z.zip --repo dinhanhthi/EchoDiar --title "EchoDiar X.Y.Z" --notes-file release/export/EchoDiar-X.Y.Z.md` (the notes `publish-release.sh` wrote before tagging: the `## [X.Y.Z]` section of `CHANGELOG.md` and the Windows block); upload any missing asset with `gh release upload vX.Y.Z release/export/<missing file> --repo dinhanhthi/EchoDiar`; then download the published zip into its own fresh dir (never over `release/export/`) and generate the appcast from it: `rm -rf release/download && mkdir -p release/download && gh release download vX.Y.Z --repo dinhanhthi/EchoDiar --pattern 'EchoDiar-X.Y.Z.zip' --dir release/download` and `scripts/publish-appcast.sh release/download/EchoDiar-X.Y.Z.zip` (`/cf-ship` does this automatically). The script refuses to sign it unless it has the same SHA-256 as `release/export/EchoDiar-X.Y.Z.zip` (when that exists) and the app inside is team `86H6CNLN4C`, notarized and version `X.Y.Z`; if it refuses, check the release assets and roll back instead of signing. Once macOS is complete, finish Windows with `windows/scripts/release.sh --attach vX.Y.Z --yes` ([§3.1](#31-per-release)). Otherwise bump the version, or roll back the bad release below.
- Push rejected (non-fast-forward): local `main` is behind `origin/main` (e.g. a PR was merged on GitHub). Run `git fetch origin && git merge --ff-only origin/main`; if that fails, the branches diverged: rebase or merge by hand, then release again. Never force-push `main`.
- Updates not offered: check the appcast is live (curl above), the build number increased, and `SUPublicEDKey` matches the key under the `EchoDiar` Keychain account.

### Roll back a bad release

When the release carries the Windows app, first revert its feed commit ([§3.2, Roll back a Windows release](#roll-back-a-windows-release)): the feed points at this release's `.msixbundle`.

```sh
gh release delete vX.Y.Z --repo dinhanhthi/EchoDiar
git push origin :refs/tags/vX.Y.Z && git tag -d vX.Y.Z
git revert '<appcast-commit>'   # the "chore(release): appcast vX.Y.Z" commit
git push origin main
```

Reverting the appcast stops offering the update to installed apps. Users who already updated keep that version; ship a fixed, higher version to move them on.

## §3 Windows

Unlike the macOS app, the Windows app ([windows/README.md](../windows/README.md)) is built and signed on GitHub Actions by `.github/workflows/windows-release.yml`. The release's tag `vX.Y.Z`, which `publish-release.sh` pushes (`ship.sh` step 2), starts it. `ship.sh` step 4, `windows/scripts/release.sh --attach vX.Y.Z --yes`, then approves that run, checks its files on the Mac, attaches them to the same GitHub Release and commits the feed `windows/EchoDiar.appinstaller` on `main`. The Pages workflow publishes the feed at `https://ed.dinhanhthi.com/windows/EchoDiar.appinstaller`, and installed apps update through App Installer from there.

The workflow runs four jobs: `prepare` (tag, channel, version, Publisher; it fails fast on a bad tag) → `package` (x64 and arm64: unsigned Release MSIX packages; no environment and no secret, because this job restores and builds third-party code) → `sign` (the only job with an environment and a PFX; it builds nothing: it signs both packages, bundles them, signs the bundle and exports the public certificate) → `assemble` (no environment: it builds `EchoDiar-Setup.exe` around that certificate, renders the feed and writes the checksums). Its result is the artifact `EchoDiar-<tag>`, kept 14 days.

There are three channels:

- **release**: the tag `vX.Y.Z` of the macOS release. Signed in the environment `windows-release-signing` (required reviewer) with the self-signed release certificate (§3.0), Publisher = the repository variable `WINDOWS_PUBLIC_PUBLISHER`, public certificate `EchoDiar.cer`. `prepare` fails when that variable is empty or `CN=EchoDiar Dev`, or when the tag is not the `MARKETING_VERSION` of `project.yml` at the tag's commit. This is the build users get, from the release notes, the website and the feed.
- **dev**: tag `windows-vX.Y.Z-dev`, from `release.sh --dev` on any branch. Signed in `windows-dev-signing` with the self-signed dev certificate (Publisher `CN=EchoDiar Dev`, public certificate `EchoDiarDev.cer`), through the same jobs as the release channel. For testing only: published, if at all, as a prerelease, and its feed is never deployed. App Installer always reads a feed from the feed's own Uri, so a dev feed's Uri is its prerelease's own `EchoDiar.appinstaller` asset, and the dev Setup is built with it: a dev prerelease installs with its Setup alone (no `--feed`), and its installs check that same prerelease feed for updates, which never offers a newer version. Test updates with a local feed (`serve-update.sh`, §3.1).
- **public**: tag `windows-vX.Y.Z`, signed with Trusted Signing. Kept for next year: its signing step is a placeholder that throws (§3.0, Trusted Signing). Nothing starts it: `release.sh` has no mode for it.

Each release `vX.Y.Z` carries, next to the macOS DMG and zip, five Windows files with fixed names:

- `EchoDiar-Setup.exe`: the installer users download. One exe for x64 and Arm64 (.NET Framework 4.8, in every Windows 11), not signed by a certificate authority. It asks for UAC once, to add the release certificate it embeds to `LocalMachine\TrustedPeople`; it installs `Microsoft.VCLibs.140.00.UWPDesktop` when missing, then installs EchoDiar for the user from the feed, so the app updates itself. Run again, it updates or does nothing, without UAC.
- `EchoDiar.cer`: the public release certificate, for the manual PowerShell install.
- `SHA256SUMS-windows.txt`: the SHA-256 of the four other files.
- `EchoDiar_X.Y.Z.0.msixbundle`: the signed x64 and arm64 packages.
- `EchoDiar.appinstaller`: the feed (its own Uri is the address above, its MainBundle Uri the `.msixbundle` of this release).

Never link these files through `releases/latest/download/`: while a release is shipping, Latest is the new release before its Windows files are attached. The website takes the tag from the live feed, and `README.md` links the docs page.

Setup is not signed by a public certificate authority, so Windows warns: SmartScreen ("Windows protected your PC": More info, then Run anyway) and UAC ("Unknown publisher"). The release notes (`windows/packaging/release-notes.md`), the website (`https://ed.dinhanhthi.com/docs.html#windows-warnings`) and Settings > About in the app explain why. Azure Artifact Signing (§3.0, Trusted Signing) is the planned fix.

### §3.0 One-time setup

Never paste passwords, client secrets or private keys into an AI chat. Run the commands below in your own terminal.

- Tools: `gh` authenticated (§0), plus `openssl`, `shasum` and `/usr/bin/python3`, which `release.sh` uses to check the files (macOS has them).
- GitHub Pages as in §0 (Source = GitHub Actions).

#### Release certificate

Do this once, before the first `/cf-ship` that ships Windows: `/cf-ship` and `scripts/ship.sh` refuse to start until `windows/scripts/release.sh --preflight` passes. On the Mac, with this repo cloned at `~/git/EchoDiar` (else change `REPO`):

```sh
SUBJ='/CN=Anh-Thi Dinh'; PUBLISHER='CN=Anh-Thi Dinh'
cd "$(mktemp -d)"
openssl req -x509 -newkey rsa:3072 -sha256 -days 1825 -nodes -subj "$SUBJ" \
  -addext "basicConstraints=critical,CA:FALSE" -addext "keyUsage=critical,digitalSignature" \
  -addext "extendedKeyUsage=codeSigning" -keyout key.pem -out cert.pem
export PFX_PASSWORD="$(openssl rand -base64 24)"
mkdir -p ~/.config/echodiar
openssl pkcs12 -export -inkey key.pem -in cert.pem -out ~/.config/echodiar/windows-release-cert.pfx -passout env:PFX_PASSWORD
chmod 600 ~/.config/echodiar/windows-release-cert.pfx
# Pin the certificate (release.sh refuses any other signer); commit it on main
REPO=~/git/EchoDiar
openssl x509 -in cert.pem -outform DER | shasum -a 256 | cut -d' ' -f1 > "$REPO/windows/packaging/release-cert.sha256"
rm key.pem cert.pem
git -C "$REPO" switch main && git -C "$REPO" pull --ff-only
git -C "$REPO" add -- windows/packaging/release-cert.sha256
git -C "$REPO" commit -m "chore(release): pin windows release certificate" -- windows/packaging/release-cert.sha256
git -C "$REPO" push origin main
security add-generic-password -U -s echodiar-windows-release-cert -a ci -w "$PFX_PASSWORD"
# The protected environment FIRST (a job naming a missing environment creates it unprotected)
ME_ID="$(gh api user --jq .id)"
gh api -X PUT repos/dinhanhthi/EchoDiar/environments/windows-release-signing --input - <<JSON
{"reviewers":[{"type":"User","id":$ME_ID}],"prevent_self_review":false,
 "deployment_branch_policy":{"protected_branches":false,"custom_branch_policies":true}}
JSON
gh api -X POST repos/dinhanhthi/EchoDiar/environments/windows-release-signing/deployment-branch-policies -f name='v*' -f type=tag
base64 -i ~/.config/echodiar/windows-release-cert.pfx | gh secret set WINDOWS_RELEASE_CERT_PFX --env windows-release-signing --repo dinhanhthi/EchoDiar
printf '%s' "$PFX_PASSWORD" | gh secret set WINDOWS_RELEASE_CERT_PASSWORD --env windows-release-signing --repo dinhanhthi/EchoDiar
unset PFX_PASSWORD
gh variable set WINDOWS_PUBLIC_PUBLISHER --repo dinhanhthi/EchoDiar --body "$PUBLISHER"
# Protect v* tags like windows-v* (admin bypass)
gh api -X POST repos/dinhanhthi/EchoDiar/rulesets --input - <<'JSON'
{"name":"release tags","target":"tag","enforcement":"active",
 "conditions":{"ref_name":{"include":["refs/tags/v*"],"exclude":[]}},
 "rules":[{"type":"creation"},{"type":"update"},{"type":"deletion"}],
 "bypass_actors":[{"actor_id":5,"actor_type":"RepositoryRole","bypass_mode":"always"}]}
JSON
# Back up ~/.config/echodiar/windows-release-cert.pfx and its password offline. Then: windows/scripts/release.sh --preflight
```

It does, in this order:

1. Creates a self-signed code signing certificate with the subject `CN=Anh-Thi Dinh`, valid 1825 days (5 years), as the PFX `~/.config/echodiar/windows-release-cert.pfx`. Its password goes into the Keychain (service `echodiar-windows-release-cert`, account `ci`; read it back with `security find-generic-password -s echodiar-windows-release-cert -a ci -w`).
2. Pins the certificate: `windows/packaging/release-cert.sha256` holds the SHA-256 of its DER, committed on `main`. `release.sh --attach` refuses a certificate, a bundle or an inner package signed by any other key.
3. Creates the environment `windows-release-signing` before any job names it (a job naming a missing environment creates it, unprotected): you as the required reviewer, `prevent_self_review` off (so `--attach` can approve the run with your `gh` login), deployments from tags `v*` only. Then its secrets `WINDOWS_RELEASE_CERT_PFX` and `WINDOWS_RELEASE_CERT_PASSWORD`, and the variable `WINDOWS_PUBLIC_PUBLISHER`.
4. Adds the tag ruleset "release tags" on `refs/tags/v*`: only the repository admin role (you) can create, move or delete release tags. `--preflight` only warns without it.

Then check that `windows/scripts/release.sh --preflight` ends with "Preflight OK".

Back up the PFX and its password offline (for example in a password manager and on an offline drive). Losing the PFX, or its expiry after 5 years, means a new certificate: every user must run a new `EchoDiar-Setup.exe` once to trust it, since App Installer refuses an update signed by a certificate the PC does not trust. With the same subject, the app and its updates stay the same package family.

The Publisher (`WINDOWS_PUBLIC_PUBLISHER`, the certificate's subject) is part of the package family name: keep it until the switch to Azure Artifact Signing. A different Publisher is a different app, which installed copies never update to. The Artifact Signing certificate will have its own subject, so at that switch users reinstall once (uninstall, then run the new Setup); their library in `%LOCALAPPDATA%\EchoDiar` stays.

The release Publisher also differs from `CN=EchoDiar Dev`: the first release install is a new app next to any dev install, and a dev install never updates to it. Uninstall dev builds (the library in `%LOCALAPPDATA%\EchoDiar` stays):

```powershell
Get-AppxPackage dinh.thi.EchoDiar | Where-Object Publisher -eq 'CN=EchoDiar Dev' | Remove-AppxPackage
```

#### Dev certificate

The secrets `WINDOWS_DEV_CERT_PFX` (base64 PFX) and `WINDOWS_DEV_CERT_PASSWORD` of the environment `windows-dev-signing` hold the dev certificate, subject exactly `CN=EchoDiar Dev`. They also sign the CI builds of `windows.yml`. Create or rotate them as in [windows/README.md, Dev signing certificate](../windows/README.md#dev-signing-certificate-one-time), and keep its pin `windows/packaging/dev-cert.sha256` up to date: `release.sh --dev` refuses any other signer.

#### Protect the signing secrets

Repository secrets reach every workflow run of the repository, so both certificates live in GitHub Environments instead (Settings > Environments and Settings > Rules):

1. The tag rulesets "windows release tags" (`refs/tags/windows-v*`, set up 2026-10-24) and "release tags" (`refs/tags/v*`, from the release certificate commands above), both active: they block creating, updating and deleting these tags for everyone but the repository admin role (bypass always), so only you can start a release run; `publish-release.sh` and `release.sh` keep working for you.
2. The environment `windows-dev-signing`, "Deployment branches and tags" = selected: the tags `windows-v*` and the branches `main`, `plan/2026-10-21-windows-port`, `plan/2026-10-25-windows-macos-parity` and `plan/2026-10-28-windows-public-release`. It holds `WINDOWS_DEV_CERT_PFX` and `WINDOWS_DEV_CERT_PASSWORD` as environment secrets; there are no repository secrets of that name. The jobs that read the dev certificate name it: `package` in `windows.yml` (except on pull requests, which are not allowed by the policy and build unsigned) and `sign` in `windows-release.yml` (dev channel).
3. The environment `windows-release-signing`: required reviewer = you, `prevent_self_review` off, deployments from the tags `v*` only. It holds `WINDOWS_RELEASE_CERT_PFX` and `WINDOWS_RELEASE_CERT_PASSWORD`. Only `sign` in `windows-release.yml` (release channel) names it, and that job runs only the checkout, the artifact download, `signtool` and `makeappx`: the release key never reaches a job that restores or builds third-party code. Every release run waits there for an approval, which `--attach` gives to that run only (§3.1).

Consequences: `windows.yml` runs on pushes to `main` and the plan branches above only, and a `workflow_dispatch` run on any other branch fails at the environment check of `package`. To build another branch, add it to `on.push.branches` and to the environment (Settings > Environments > `windows-dev-signing` > Deployment branches and tags, or `gh api -X POST repos/dinhanhthi/EchoDiar/environments/windows-dev-signing/deployment-branch-policies -f name='<branch>' -f type=branch`). Each signing job shows up as a deployment of its environment.

Rotate the dev certificate as in [windows/README.md, Dev signing certificate](../windows/README.md#dev-signing-certificate-one-time): it sets the two environment secrets (`gh secret set ... --env windows-dev-signing`, value on stdin). Check with `gh secret list --env windows-dev-signing`.

#### Trusted Signing (public channel, next year)

Not used yet. Microsoft's publicly trusted signing service (Trusted Signing, now Azure Artifact Signing) accepts individuals only in the US and Canada for now, so the release channel signs with the self-signed certificate above. These steps stay here for when it becomes available. A publicly trusted signature on Setup and the packages removes the certificate import (and its UAC prompt), lets Smart App Control run them, and closes the accepted risks of §3.2.

1. In Azure, create a Trusted Signing account, complete the identity validation and create a Public Trust certificate profile.
2. Create an app registration (service principal) with a client secret, and give it the Trusted Signing Certificate Profile Signer role on the account.
3. Store its values as repository secrets (`gh secret set` prompts for each value, so it never lands in shell history), and the certificate subject as a variable:

   ```sh
   for name in AZURE_TENANT_ID AZURE_CLIENT_ID AZURE_CLIENT_SECRET \
               TRUSTED_SIGNING_ENDPOINT TRUSTED_SIGNING_ACCOUNT TRUSTED_SIGNING_CERT_PROFILE; do
     gh secret set "$name" --repo dinhanhthi/EchoDiar
   done
   gh variable set WINDOWS_PUBLIC_PUBLISHER --repo dinhanhthi/EchoDiar --body '<certificate subject, e.g. CN=..., O=..., C=...>'
   ```

   `TRUSTED_SIGNING_ENDPOINT` is the account's region endpoint (`https://<region>.codesigning.azure.net/`). `WINDOWS_PUBLIC_PUBLISHER` must be exactly the subject of the Trusted Signing certificate: the workflow writes it into the manifest `Identity Publisher`, the bundle and the `.appinstaller`, and refuses `CN=EchoDiar Dev`. The release channel reads the same variable: changing it changes the Publisher of every later release, so users reinstall once (§3.0, Release certificate).

4. **Before the first public release, implement the signing step.** The Trusted Signing step of `windows-release.yml` is a placeholder that throws. Without the secrets above the run fails at once (the `prepare` job names the missing ones); with them it stops at that step. Replace it with real Trusted Signing (signtool with the Trusted Signing dlib, or Microsoft's signing action) for both packages, the bundle and `EchoDiar-Setup.exe`, and switch the release channel, which `/cf-ship` ships, to it.

Also still to do then: an environment `windows-public-signing` restricted to the `windows-v*` tags with a required reviewer, holding the Trusted Signing secrets, and named by the job that signs with Trusted Signing (`windows-release.yml` has a comment where). Create the environment first: a job naming a missing environment creates it, unprotected. Prefer OpenID Connect (a federated credential on the app registration, `azure/login` with `id-token: write`) over `AZURE_CLIENT_SECRET`, so no long-lived secret is stored at all.

### §3.1 Per release

Windows ships with every release, through `/cf-ship` or `scripts/ship.sh` (§1): never push a `v*` tag or dispatch the release channel by hand. The version is the release's: `scripts/bump-version.sh` writes the package version `X.Y.Z.0` (each part 0-65535) into the manifest, CI patches it from the tag anyway, and `prepare` refuses a tag that is not `MARKETING_VERSION` at the tag's commit. Settings > About shows `X.Y.Z`, as on macOS. The release `vX.Y.Z` is the Latest release for both apps (`publish-release.sh` creates a normal release, which GitHub marks Latest; dev prereleases never are):

```sh
gh release view --repo dinhanhthi/EchoDiar --json tagName --jq .tagName   # vX.Y.Z
```

Releases made before the Windows app (`v1.1.0` and earlier) have no `windows/src/EchoDiar.Setup/` at their tag: `--attach` prints "has no Windows app (macOS-only release)" and does nothing.

#### What --attach does

`windows/scripts/release.sh --attach vX.Y.Z [--yes]` (`ship.sh` step 4; `--yes` answers its one prompt):

1. **Guards**, before anything else: branch `main`, `HEAD` = `origin/main` after a fetch, no uncommitted changes to tracked files (it commits the feed), `gh` authenticated, `origin` = this repo, the tag on origin, the GitHub Release `vX.Y.Z` published, the tag's commit on `origin/main` (only `main`'s code gets the release key), `WINDOWS_PUBLIC_PUBLISHER` set and the pin `windows/packaging/release-cert.sha256` at `HEAD`.
2. **Run**: finds the newest `windows-release.yml` run of the tag's commit (that workflow file, that tag, a `push` or `workflow_dispatch` event). With no run after 5 minutes, it dispatches the release channel on the tag (`gh workflow run windows-release.yml --ref vX.Y.Z -f version=X.Y.Z -f channel=release`).
3. **Approval**: once the run waits at `sign` (after both `package` legs, about 20 min in), it approves the `windows-release-signing` deployment of this run only, as you. When it cannot, it prints the run's URL and waits up to 30 min for you to approve it in the browser.
4. **Wait**: up to 2 hours per attempt. A failed run is rerun once, its failed jobs only (the whole run when its intermediate artifacts have expired), and approved again. An expired artifact `EchoDiar-vX.Y.Z` also gets a whole rerun.
5. **Download**: the artifact into `release/windows/vX.Y.Z/` (gitignored). A folder there that already verifies against its `SHA256SUMS-windows.txt` is reused, never downloaded again: a rebuild is signed anew, and its bytes no longer match assets already uploaded.
6. **Checks**, reading the files as data (isolated Python, never run): exactly the five files and no private key; the checksums; the bundle (signed, Name, Version, Publisher, x64 and arm64, both inner packages signed); the feed (Version, Publisher, both Uris, update settings); the certificate's subject is the Publisher; the certificate and the signers of the bundle and of both inner packages have the pinned SHA-256; Setup is a Windows executable that embeds the certificate.
7. **Upload**: only the assets the release lacks, never with `--clobber`. An asset already there must have the same SHA-256 (its GitHub `digest`), or it stops before uploading anything.
8. **Feed**: commits `windows/EchoDiar.appinstaller` on `main` (`chore(release): windows appinstaller vX.Y.Z`) and pushes it, unless `origin/main`'s feed already has this version or a higher one: the feed never rolls back. The Pages workflow deploys it within minutes, and the website then offers this release's Setup.

It is idempotent: when it fails, fix the cause and run the same command again, and it carries on from where the release stands. `/cf-ship` runs it once per `/cf-ship` run for the latest release tag only (§3.2).

`windows/scripts/release.sh --preflight` is read-only: it checks the environment `windows-release-signing` (exists, lists you as required reviewer, `prevent_self_review` off, deployment policy exactly the tags `v*`), its two secrets (names only), `WINDOWS_PUBLIC_PUBLISHER` (set, not `CN=EchoDiar Dev`), `windows-release.yml` and `windows/packaging/release-cert.sha256` at `HEAD`, and warns when no tag ruleset covers `refs/tags/v*`. It stops at the first failure with a pointer to §3.0. `ship.sh` and `/cf-ship` run it before building.

#### Dev builds and prereleases

```sh
windows/scripts/release.sh --dev --version 0.0.N            # build and check only
windows/scripts/release.sh --dev --version 0.0.N --publish  # ... then create the prerelease (asks first, unless --yes)
```

N must be above the run number of the `windows.yml` CI builds (they are `0.0.<run>.0`): App Installer only moves to a higher version. It runs on any branch whose `HEAD` is pushed (uncommitted changes only warn: CI builds the pushed tag's commit), pushes the tag `windows-v0.0.N-dev`, waits for the run, downloads the artifact into `release/windows/<tag>/` and checks it against the dev pin `windows/packaging/dev-cert.sha256`. With `--publish` it creates a prerelease with the five files (`--latest=false --prerelease`). A tag is never reused: without `--publish` it prints the `gh release create` command and how to discard the tag.

A dev Setup embeds `EchoDiarDev.cer` and installs from its prerelease's own feed (`https://github.com/dinhanhthi/EchoDiar/releases/download/windows-v0.0.N-dev/EchoDiar.appinstaller`) by default, so it needs no `--feed`. `--feed` points it at another feed (a local one, below), as https, or as http on a private or loopback IPv4 address.

#### The feed

`--attach` commits the rendered feed on `main`. The Pages workflow deploys it (its `paths` include `windows/EchoDiar.appinstaller`), which also shows the Windows download on the website. Never commit a dev `.appinstaller` there: it has the dev Publisher, its prerelease's Uri and a prerelease bundle, and the Pages workflow refuses it. The commit also runs `windows.yml` (it is under `windows/`); that is harmless. To redeploy without a commit: `gh workflow run pages.yml --repo dinhanhthi/EchoDiar --ref main`.

Verify:

```sh
curl -fsSI https://ed.dinhanhthi.com/windows/EchoDiar.appinstaller   # 200; note the Content-Type
curl -fsSL https://ed.dinhanhthi.com/windows/EchoDiar.appinstaller | grep -E 'Version=|Uri='
```

#### Test an update locally

`windows/scripts/serve-update.sh` serves a local feed to the VMPal VM, without a release. Put exactly one signed `.msix` or `.msixbundle` (a CI artifact or a dev release) in `~/Downloads/EchoDiar-update/`, then run it on the Mac. It renders `EchoDiar.appinstaller` there from the package's own manifest and serves it on port 8765 until Ctrl+C. It listens only on the Mac's address on the VM bridge (`192.168.128.x`), or on the IPv4 address of `--host` (a host name is refused), and serves only the package, `EchoDiar.appinstaller` and `EchoDiarDev.cer` (when present): every other path is a 404, with no folder listing. It answers HTTP Range requests (206, one range per request), which App Installer and Delivery Optimization use to download the package. In the VM, with the dev certificate trusted (`vm-install.ps1 -TrustCert`, or a dev Setup):

```powershell
Add-AppxPackage -AppInstallerFile http://<host>:8765/EchoDiar.appinstaller
# or: .\EchoDiar-Setup.exe --feed http://<host>:8765/EchoDiar.appinstaller
```

To offer the next version, stop the script, replace the package with a higher version and run it again. The app shows its Update button once it finds it. `--render-only` writes the file without serving, `--host` and `--folder` override the defaults.

### §3.2 Troubleshooting and rollback

- **`--preflight` fails** (`ship.sh` and `/cf-ship` stop before building): its `FAIL` line names the missing piece. Set it up as in §3.0.
- **`prepare` fails**: on the release channel, `WINDOWS_PUBLIC_PUBLISHER` is unset or the dev subject, or the tag is not `MARKETING_VERSION` at its commit (only possible for a tag pushed by hand). On the public channel, its error names the missing Trusted Signing secrets or variable; with them set, the run stops at the placeholder signing step.
- **Approval pending**: nothing can be approved until both `package` legs finish (about 20 min); `--attach` then approves on its own. **Approval not possible** (`cannot approve the windows-release-signing deployment: approve it at <url>`): you are not a required reviewer, `prevent_self_review` is on, or the API call failed. Approve the run in the browser (the run's page > Review deployments > `windows-release-signing` > Approve and deploy) within the 30 minutes `--attach` waits, or approve it later and run `--attach` again: the run keeps waiting. Then fix the environment (`--preflight` names what is wrong).
- **The run failed**: `--attach` reruns its failed jobs once, approves the new attempt, and stops if it fails again, with `gh run view <id> --repo dinhanhthi/EchoDiar --log-failed`. A transient failure (runner, network, package restore): run `--attach` again, which reruns once more. A failure in the code or the workflow: the tag pins both, so every rerun of `vX.Y.Z` fails the same way. Fix it on `main` and ship a patch version (below).
- **No run for the tag**: `--attach` dispatches the release channel after 5 minutes. If even that run does not show up, check the Actions tab: dispatch needs `windows-release.yml` on `main`.
- **Artifact expired**: workflow artifacts are kept 14 days. `--attach` then reruns the whole run (and approves it). The rebuild is signed anew, so its bytes differ from any Windows asset already uploaded (next item). Keep `release/windows/vX.Y.Z/` until the release is complete: `--attach` reuses it instead of rebuilding.
- **Digest mismatch** (`assets of the release differ from SHA256SUMS-windows.txt (or have no digest): <names>`): the release already holds Windows assets from another build (an attempt before a whole rerun, or a manual upload), and nothing was uploaded. If the release already has all five Windows assets and `main`'s feed has `X.Y.Z.0`, Windows is complete: there is nothing to do. Otherwise delete the release's Windows assets (never the macOS ones) and run `--attach` again, so that all five come from one build:

  ```sh
  for f in EchoDiar-Setup.exe EchoDiar.cer SHA256SUMS-windows.txt EchoDiar_X.Y.Z.0.msixbundle EchoDiar.appinstaller; do
    gh release delete-asset vX.Y.Z "$f" --repo dinhanhthi/EchoDiar --yes
  done
  ```

- **Defender flags Setup** (a false positive is likely for an unsigned exe that elevates and imports a certificate): submit the release's `EchoDiar-Setup.exe` at the Microsoft WDSI file submission portal (Microsoft Security Intelligence, "Submit a file"), as a software developer reporting an incorrect detection. Do it once after the first Windows release anyway, and again whenever a version is flagged.
- **Smart App Control** blocks unsigned exes outright, with no "Run anyway", and may also block the installed app's unsigned native DLLs. Nothing fixes that before a publicly trusted signature (§3.0, Trusted Signing), and QA cannot test it (once off, Smart App Control cannot be turned back on in the VM). Users install with the PowerShell steps on the website (`https://ed.dinhanhthi.com/docs.html#windows-warnings`): `EchoDiar.cer` into `LocalMachine\TrustedPeople` from an administrator PowerShell, then `Add-AppxPackage -AppInstallerFile https://ed.dinhanhthi.com/windows/EchoDiar.appinstaller` as the user.
- **VCLibs**: Setup installs `Microsoft.VCLibs.140.00.UWPDesktop` (14.0.33728.0 or later) from Microsoft when it is missing; when the install still fails for it, Setup shows `0x80073CF3`. Install it by hand, then run Setup again: `Add-AppxPackage https://aka.ms/Microsoft.VCLibs.x64.14.00.Desktop.appx` (`arm64` instead of `x64` on Arm PCs). See also the notes in [windows/README.md](../windows/README.md#testing-in-a-windows-vm).
- **Updates not offered**: check the feed is live with a higher Version (curl above), the copy was installed with Setup or from the `.appinstaller` (a sideloaded `.msix` never updates; Settings > About says so) and has the same Publisher.

#### A release that went out macOS-only

When step 4 fails for good, `vX.Y.Z` stays macOS-only: macOS is shipped, the release has no Windows files and the feed stays at the previous version. The website keeps offering the previous complete release (its download link comes from the live feed), but the release notes describe a Setup that is not there: drop that block as in [Ship macOS only by hand](#ship-macos-only-by-hand).

A Windows build that is broken for a tagged version is fixed by shipping a patch version: the tag pins the workflow and the source, so `vX.Y.Z` can never build differently. `/cf-ship` makes one `--attach` attempt per run, for the latest release tag only. When it fails, its summary says "vX.Y.Z shipped macOS-only: <reason>" and it goes on to ship the next version; older versions are never retried. While the latest release lacks `EchoDiar-Setup.exe`, `/cf-ship` counts commits to the Windows release path (`.github/workflows/windows-release.yml`, `windows/scripts/`, `windows/packaging/`, `windows/src/EchoDiar.Setup/`) as user-facing, so that fix can ship on its own.

#### Accepted risks

Two weaknesses of the self-signed release are known and were accepted on 2026-10-28. Signing Setup with Azure Artifact Signing (§3.0, Trusted Signing) closes both.

- **The elevated certificate import trusts the exe on disk.** Someone who can replace the unsigned `EchoDiar-Setup.exe` in the user's Downloads (the same user) runs their own code elevated once the user accepts UAC. A thumbprint check in Setup cannot help, because the replacement simply skips it, and UAC shows "Unknown publisher" either way. This is inherent to any unsigned installer.
- **Setup is outside the signer pin.** The `assemble` job, which has no secrets, builds it around the pinned certificate, and `release.sh` checks only that it is a Windows executable containing that certificate. Its integrity rests on the same CI build trust as the packages: `package` also builds them without secrets before `sign` signs them, so a compromised build dependency would be signed either way. Signing Setup with the same self-signed certificate would add nothing.

#### Ship macOS only by hand

When the Windows release channel is broken or not set up, `ship.sh` refuses at `--preflight`. Ship macOS alone by running its first three steps yourself, after the release commit is pushed (§1, Manually, steps 1-3), and skip step 4:

```sh
scripts/build-and-notarize.sh
scripts/publish-release.sh --yes release/export/EchoDiar-X.Y.Z.zip
scripts/publish-appcast.sh release/export/EchoDiar-X.Y.Z.zip
```

Then drop the Windows block from the release notes (everything from its `## Windows` heading on), since it describes a Setup that is not there:

```sh
notes="$(mktemp)"
gh release view vX.Y.Z --repo dinhanhthi/EchoDiar --json body -q .body | awk '/^## Windows/ { exit } { print }' > "$notes"
gh release edit vX.Y.Z --repo dinhanhthi/EchoDiar --notes-file "$notes"
```

The tag push still starts a Windows run. On a channel that is not set up, it fails in `prepare`; otherwise it waits at `sign` for an approval: cancel it (`gh run list --repo dinhanhthi/EchoDiar --workflow windows-release.yml --branch vX.Y.Z`, then `gh run cancel <id> --repo dinhanhthi/EchoDiar`). While `vX.Y.Z` is the latest release, the next `/cf-ship` still makes its one `--attach` attempt for it.

#### Roll back a Windows release

Keep macOS and take Windows back: revert the feed commit first, then delete the Windows assets:

```sh
git revert '<feed-commit>'   # the "chore(release): windows appinstaller vX.Y.Z" commit
git push origin main
for f in EchoDiar-Setup.exe EchoDiar.cer SHA256SUMS-windows.txt EchoDiar_X.Y.Z.0.msixbundle EchoDiar.appinstaller; do
  gh release delete-asset vX.Y.Z "$f" --repo dinhanhthi/EchoDiar --yes
done
```

Then drop the Windows block from the release notes as in [Ship macOS only by hand](#ship-macos-only-by-hand). Revert first: the previous feed points at the previous release's `.msixbundle`, so keep that release. Reverting the first feed removes it, and the website hides the Windows download again. App Installer does not downgrade: users who already updated keep that version; ship a fixed, higher version to move them on. While `vX.Y.Z` is the latest release, the next `/cf-ship` makes its one `--attach` attempt for it and would attach that build again: ship the fix with the manual steps of §1 (`scripts/ship.sh` never goes back to an older tag), or roll back the whole release.

To roll back the whole release, revert the feed commit as above, then roll back macOS as in [§2, Roll back a bad release](#roll-back-a-bad-release): it deletes the release with all its assets and the tag `vX.Y.Z` (the `v*` tag ruleset lets only the admin role delete it).
