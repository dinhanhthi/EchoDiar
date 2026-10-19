# Releasing EchoDiar

Releases are built, signed, notarized and published from your own Mac. There is no CI release workflow: the only GitHub workflow, `.github/workflows/pages.yml`, publishes the website and `appcast.xml` to GitHub Pages so installed apps can find updates at `https://ed.dinhanhthi.com/appcast.xml` (`SUFeedURL`).

Each release ships two notarized assets on the GitHub Release:

- `EchoDiar-X.Y.Z.dmg`: drag-to-install download for new users.
- `EchoDiar-X.Y.Z.zip`: the Sparkle update archive referenced by the appcast.

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

`/cf-ship` and the scripts run whatever `.coding-friend/skills/cf-ship-custom/SKILL.md`, `scripts/`, `project.yml` and `appcast.xml` say on `main`, with your signing keys. Once the repo is public, a merged PR could change them. `/cf-ship` stops when the last commit touching one of them (locally or on `origin/main`) is not authored by your `git config user.email`, but author fields can be forged, so set up these GitHub controls yourself (nothing creates them for you):

- `.github/CODEOWNERS` with you as owner of the release files:

  ```text
  /.coding-friend/ @dinhanhthi
  /scripts/        @dinhanhthi
  /project.yml     @dinhanhthi
  /appcast.xml     @dinhanhthi
  /.github/        @dinhanhthi
  ```

- A branch protection rule (or ruleset) on `main`: require a pull request with review from Code Owners, block force pushes and deletions, and restrict who can push to `main` to you.

## §1 Per release

### With /cf-ship (recommended)

Run `/cf-ship`. The project guide in `.coding-friend/skills/cf-ship-custom/SKILL.md` runs the §0 preflight checks, picks the version, writes the `CHANGELOG.md` section, bumps `project.yml`, commits `chore(release): vX.Y.Z`, pushes to `main` and runs `scripts/ship.sh`. It never asks: running `/cf-ship` is the approval for the whole flow.

First release: the current `MARKETING_VERSION` (0.1.0) ships as-is, without a bump, and the `## [Unreleased]` items move into `## [0.1.0] - <date>`.

### Manually

1. Bump the version (sets `MARKETING_VERSION`, increments `CURRENT_PROJECT_VERSION` in `project.yml`; skip on the first release):

   ```sh
   scripts/bump-version.sh X.Y.Z
   ```

2. Move the `## [Unreleased]` items of `CHANGELOG.md` into a new `## [X.Y.Z] - YYYY-MM-DD` section (the GitHub Release notes come from it).
3. Commit and push:

   ```sh
   git add -- CHANGELOG.md project.yml
   git commit -m "chore(release): vX.Y.Z" -- CHANGELOG.md project.yml
   git push origin main
   ```

4. Ship:

   ```sh
   scripts/ship.sh
   ```

Outputs land in `release/export/` (gitignored): `EchoDiar.app`, `EchoDiar-X.Y.Z.zip`, `EchoDiar-X.Y.Z.dmg` and the release notes `EchoDiar-X.Y.Z.md`.

### What each script does

- `scripts/bump-version.sh [X.Y.Z]`: edits `MARKETING_VERSION` (when given) and `CURRENT_PROJECT_VERSION` (+1) in `project.yml`. Sparkle only offers an update when the build number increases. Does not commit.
- `scripts/build-and-notarize.sh`: checks Xcode 27+, exactly one Developer ID Application identity of team `86H6CNLN4C` (it signs by that identity's hash), a non-empty `SUPublicEDKey`, an https `SUFeedURL` and the notary profile before anything slow; runs `xcodegen generate`; archives the Release build with `ARCHS=arm64` to `release/EchoDiar.xcarchive` (default DerivedData, so `publish-appcast.sh` finds the Sparkle tools); checks the arm64-only binary, signing, hardened runtime, no `get-task-allow`, macOS 15.0 minimum, Sparkle.framework and the compiled app icon; re-signs nested code inside-out with Developer ID (Sparkle's XPC services, Autoupdate and Updater.app, the SPM resource bundles such as the MLX metallib bundle and the WhisperKit, FluidAudio and speech-swift bundles, frameworks and dylibs), then the app with its entitlements, and checks that the app and every nested item carry `TeamIdentifier=86H6CNLN4C`; prints the embedded Sparkle version; notarizes and staples the app, zips it; builds, signs, notarizes and staples the DMG.
- `scripts/publish-release.sh [--yes] [<zip>]`: reads the version from the committed `project.yml` (the zip defaults to `release/export/EchoDiar-<version>.zip`), requires branch `main`, `gh` auth, `origin` = `github.com/dinhanhthi/EchoDiar`, `HEAD` already on `origin/main`, a new tag and a `## [X.Y.Z]` section in `CHANGELOG.md`; writes that section to `release/export/EchoDiar-X.Y.Z.md`; creates and pushes tag `vX.Y.Z`; creates the GitHub Release with the zip and uploads the DMG.
- `scripts/publish-appcast.sh <zip>`: needs a zip named `EchoDiar-X.Y.Z.zip` and verifies it before signing anything: same SHA-256 as `release/export/EchoDiar-X.Y.Z.zip` when that file exists and is not the zip given, and always (after unzipping to a temp dir) team `86H6CNLN4C` (`codesign -dvvv`), `codesign --verify --deep --strict`, "Notarized Developer ID" (`spctl -a -t exec -vv`) and `CFBundleShortVersionString` == `X.Y.Z`. It then runs Sparkle's `generate_appcast --account EchoDiar --maximum-versions 0 --maximum-deltas 0` (keeps every past item, no deltas) over a fresh `release/appcast-archives/` holding the new zip plus the existing `appcast.xml`, with the release asset URL as download prefix and `CHANGELOG.md` on `main` as the full release notes link; checks that the new item's enclosure URL is `https://github.com/dinhanhthi/EchoDiar/releases/download/vX.Y.Z/EchoDiar-X.Y.Z.zip` and carries a `sparkle:edSignature`, prints the Sparkle version of the tools, copies the result to the repo root, commits `chore(release): appcast vX.Y.Z` and pushes; the Pages workflow deploys it in about a minute.
- `scripts/ship.sh`: before building, refuses a branch other than `main`, `HEAD` != `origin/main` (after `git fetch origin main`: only pushed commits ship), `gh` not authenticated, uncommitted changes to tracked files, no `## [X.Y.Z]` section in `CHANGELOG.md`, or an existing tag `vX.Y.Z` (local or on origin), then runs the three scripts above in order and aborts if `HEAD` changes during the build.

Verify a release:

```sh
curl -fsSL https://ed.dinhanhthi.com/appcast.xml | grep shortVersionString
```

## §2 Troubleshooting and rollback

- Notarization not Accepted: `build-and-notarize.sh` prints the submission id. Read the log, which names the binary that failed:

  ```sh
  xcrun notarytool log <id> --keychain-profile <profile>
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
- `tag vX.Y.Z already exists`: if the release is half-shipped (tag pushed but assets or appcast entry missing), finish it: if the GitHub Release itself is missing, `gh release create vX.Y.Z release/export/EchoDiar-X.Y.Z.zip --repo dinhanhthi/EchoDiar --title "EchoDiar X.Y.Z" --notes-file <the ## [X.Y.Z] section of CHANGELOG.md saved to a file>`; upload any missing asset with `gh release upload vX.Y.Z release/export/<missing file> --repo dinhanhthi/EchoDiar`; then download the published zip into its own fresh dir (never over `release/export/`) and generate the appcast from it: `rm -rf release/download && mkdir -p release/download && gh release download vX.Y.Z --repo dinhanhthi/EchoDiar --pattern 'EchoDiar-X.Y.Z.zip' --dir release/download` and `scripts/publish-appcast.sh release/download/EchoDiar-X.Y.Z.zip` (`/cf-ship` does this automatically). The script refuses to sign it unless it has the same SHA-256 as `release/export/EchoDiar-X.Y.Z.zip` (when that exists) and the app inside is team `86H6CNLN4C`, notarized and version `X.Y.Z`; if it refuses, check the release assets and roll back instead of signing. Otherwise bump the version, or roll back the bad release below.
- Push rejected (non-fast-forward): local `main` is behind `origin/main` (e.g. a PR was merged on GitHub). Run `git fetch origin && git merge --ff-only origin/main`; if that fails, the branches diverged: rebase or merge by hand, then release again. Never force-push `main`.
- Updates not offered: check the appcast is live (curl above), the build number increased, and `SUPublicEDKey` matches the key under the `EchoDiar` Keychain account.

### Roll back a bad release

```sh
gh release delete vX.Y.Z --repo dinhanhthi/EchoDiar
git push origin :refs/tags/vX.Y.Z && git tag -d vX.Y.Z
git revert <appcast-commit>   # the "chore(release): appcast vX.Y.Z" commit
git push origin main
```

Reverting the appcast stops offering the update to installed apps. Users who already updated keep that version; ship a fixed, higher version to move them on.
