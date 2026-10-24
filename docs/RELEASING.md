# Releasing EchoDiar

Releases are built, signed, notarized and published from your own Mac. There is no CI release workflow: the only GitHub workflow, `.github/workflows/pages.yml`, publishes the website and `appcast.xml` to GitHub Pages so installed apps can find updates at `https://ed.dinhanhthi.com/appcast.xml` (`SUFeedURL`).

Each release ships two notarized assets on the GitHub Release:

- `EchoDiar-X.Y.Z.dmg`: drag-to-install download for new users.
- `EchoDiar-X.Y.Z.zip`: the Sparkle update archive referenced by the appcast.

The Windows app is released differently, from GitHub Actions: see [§3 Windows](#3-windows).

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

## §3 Windows

Unlike the macOS app, the Windows app ([windows/README.md](../windows/README.md)) is built and signed on GitHub Actions by `.github/workflows/windows-release.yml`, which a `windows-v*` tag starts. From the Mac, `windows/scripts/release.sh` pushes the tag, watches the run, downloads and checks its files and, with `--publish`, creates the GitHub Release. Installed apps update through App Installer from `https://ed.dinhanhthi.com/windows/EchoDiar.appinstaller`, which the Pages workflow publishes from `windows/EchoDiar.appinstaller` on `main`.

There are two channels:

- **dev**: tag `windows-vX.Y.Z-dev`, signed with the self-signed dev certificate (Publisher `CN=EchoDiar Dev`), published as a prerelease with `EchoDiarDev.cer`. For testing only: a machine must trust the certificate first, and its feed is never deployed. A copy installed from a dev prerelease's `EchoDiar.appinstaller` takes that file's own Uri, the public feed address above, as its update source: it gets no update until a public feed with the same Publisher exists there, which never happens (the public Publisher differs, §3.0). Only a local feed (`serve-update.sh`, §3.1, or a `file://` `.appinstaller`) updates dev installs.
- **public**: tag `windows-vX.Y.Z` from `main`, signed with Trusted Signing. This is the one the website offers. It needs Trusted Signing (§3.0) and your own decision to publish: nothing starts it for you.

Each release ships on the GitHub Release of its tag:

- `EchoDiar_X.Y.Z.0.msixbundle`: the x64 and arm64 packages.
- `EchoDiar.appinstaller`: the App Installer feed (its own Uri is the address above, its MainBundle Uri the `.msixbundle` asset of the tag).
- `EchoDiarDev.cer` (dev channel only): the public dev certificate.

### §3.0 One-time setup

Never paste passwords, client secrets or private keys into an AI chat. Run the commands below in your own terminal.

- Tools: `gh` authenticated (§0), plus the macOS `unzip` and `/usr/bin/python3` that `release.sh` uses to check the files.
- GitHub Pages as in §0 (Source = GitHub Actions).

#### Dev certificate

The secrets `WINDOWS_DEV_CERT_PFX` (base64 PFX) and `WINDOWS_DEV_CERT_PASSWORD` of the environment `windows-dev-signing` hold the dev certificate, subject exactly `CN=EchoDiar Dev`. They also sign the CI builds of `windows.yml`. Create or rotate them as in [windows/README.md, Dev signing certificate](../windows/README.md#dev-signing-certificate-one-time).

#### Protect the signing secrets

Repository secrets reach every workflow run of the repository, so the dev certificate lives in a GitHub Environment instead (set up 2026-10-24, Settings > Environments and Settings > Rules):

1. The tag ruleset "windows release tags" (target tags, `refs/tags/windows-v*`, active): it blocks creating, updating and deleting these tags for everyone but the repository admin role (bypass always), so only you can start a release run; `release.sh` keeps working for you.
2. The environment `windows-dev-signing`, "Deployment branches and tags" = selected: the tags `windows-v*` and the branches `main` and `plan/2026-10-21-windows-port`. It holds `WINDOWS_DEV_CERT_PFX` and `WINDOWS_DEV_CERT_PASSWORD` as environment secrets; there are no repository secrets of that name. The jobs that read the dev certificate name it: `package` in `windows.yml` (except on pull requests, which are not allowed by the policy and build unsigned), `package` and `bundle` in `windows-release.yml` (dev channel only).

Consequences: a CI build of another branch (for example a new `plan/*` branch, or `workflow_dispatch` on one) fails at the environment check of `package` until you add the branch to the environment (Settings > Environments > `windows-dev-signing` > Deployment branches and tags, or `gh api -X POST repos/dinhanhthi/EchoDiar/environments/windows-dev-signing/deployment-branch-policies -f name='<branch>' -f type=branch`). Each signing job shows up as a deployment of the environment.

Rotate the certificate as in [windows/README.md, Dev signing certificate](../windows/README.md#dev-signing-certificate-one-time): it sets the two environment secrets (`gh secret set ... --env windows-dev-signing`, value on stdin). Check with `gh secret list --env windows-dev-signing`.

Still to do, for the public channel: an environment `windows-public-signing` restricted to the `windows-v*` tags with a required reviewer, holding the Trusted Signing secrets, and named by the job that signs with Trusted Signing (`windows-release.yml` has a comment where). Create the environment first: a job naming a missing environment creates it, unprotected. Prefer OpenID Connect (a federated credential on the app registration, `azure/login` with `id-token: write`) over `AZURE_CLIENT_SECRET`, so no long-lived secret is stored at all.

#### Trusted Signing (public channel)

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

   `TRUSTED_SIGNING_ENDPOINT` is the account's region endpoint (`https://<region>.codesigning.azure.net/`). `WINDOWS_PUBLIC_PUBLISHER` must be exactly the subject of the Trusted Signing certificate: the workflow writes it into the manifest `Identity Publisher`, the bundle and the `.appinstaller`, and refuses `CN=EchoDiar Dev`.

4. **Before the first public release, implement the signing step.** The Trusted Signing step of `windows-release.yml` is a placeholder that throws. Without the secrets above the run fails at once (the `prepare` job names the missing ones); with them it stops at that step. Replace it with real Trusted Signing (signtool with the Trusted Signing dlib, or Microsoft's signing action) for both packages and the bundle.

The public Publisher differs from `CN=EchoDiar Dev`, so the package family name changes: the first public install is a new app next to any dev install, and a dev install never updates to it. Uninstall dev builds (the library in `%LOCALAPPDATA%\EchoDiar` stays):

```powershell
Get-AppxPackage dinh.thi.EchoDiar | Where-Object Publisher -eq 'CN=EchoDiar Dev' | Remove-AppxPackage
```

Keep the public Publisher from then on: changing it again would strand every installed copy.

### §3.1 Per release

Versions: the tag `windows-vX.Y.Z` (or `windows-vX.Y.Z-dev`) gives the package version `X.Y.Z.0` (each part 0-65535). The public channel defaults to `MARKETING_VERSION` of the committed `project.yml`, the macOS version; pass `--version X.Y.Z` otherwise. The dev channel needs `--version 0.0.N`, with N above the run number of the `windows.yml` CI builds (they are `0.0.<run>.0`): App Installer only moves to a higher version.

Dev release (any branch, `HEAD` pushed):

```sh
windows/scripts/release.sh --dev --version 0.0.N            # build and check only
windows/scripts/release.sh --dev --version 0.0.N --publish  # ... then create the prerelease (asks first)
```

Public release (from `main`, `HEAD` pushed):

```sh
windows/scripts/release.sh --publish                        # or --version X.Y.Z
```

What `release.sh` does:

- Refuses uncommitted changes to tracked files, a detached `HEAD`, the public channel off `main`, `HEAD` not on `origin/<branch>`, `gh` not authenticated, and an existing tag, GitHub Release or `release/windows/<tag>/`.
- Shows the channel, version, tag and commit, asks, then pushes the annotated tag and waits for the `windows-release.yml` run of that tag.
- Downloads the artifact `EchoDiar-<tag>` into `release/windows/<tag>/` (gitignored) and checks it: no private key, the `.msixbundle` is signed, its Name, Version, Publisher and both architectures, and the `.appinstaller` (Version, Publisher, both Uris, `OnLaunch HoursBetweenUpdateChecks="0"`, `AutomaticBackgroundTask`).
- With `--publish`, asks again and runs `gh release create <tag> --verify-tag --latest=false` (plus `--prerelease` on the dev channel). Without it, it prints that command so you can create the release by hand later: the tag stays.

`--latest=false` on both channels: the website's macOS Download links `releases/latest`, which must keep pointing at the macOS release with the DMG. Check after a Windows release:

```sh
gh release view --repo dinhanhthi/EchoDiar --json tagName --jq .tagName   # vX.Y.Z, not windows-v...
```

#### Publish the feed (public channel only)

Commit the rendered feed on `main`. The Pages workflow deploys it (its `paths` include `windows/EchoDiar.appinstaller`), which also shows the Windows download on the website:

```sh
cp release/windows/windows-vX.Y.Z/EchoDiar.appinstaller windows/EchoDiar.appinstaller
git add -- windows/EchoDiar.appinstaller
git commit -m "chore(release): windows appinstaller vX.Y.Z" -- windows/EchoDiar.appinstaller
git push origin main
```

Never commit a dev `.appinstaller` there: it has the same Uri but the dev Publisher and a prerelease bundle. The commit also runs `windows.yml` (it is under `windows/`); that is harmless. To redeploy without a commit: `gh workflow run pages.yml --repo dinhanhthi/EchoDiar --ref main`.

Verify:

```sh
curl -fsSI https://ed.dinhanhthi.com/windows/EchoDiar.appinstaller   # 200; note the Content-Type
curl -fsSL https://ed.dinhanhthi.com/windows/EchoDiar.appinstaller | grep -E 'Version=|Uri='
```

#### Test an update locally

`windows/scripts/serve-update.sh` serves a local feed to the VMPal VM, without a release. Put exactly one signed `.msix` or `.msixbundle` (a CI artifact or a dev release) in `~/Downloads/EchoDiar-update/`, then run it on the Mac. It renders `EchoDiar.appinstaller` there from the package's own manifest and serves it on port 8765 until Ctrl+C. It listens only on the Mac's address on the VM bridge (`192.168.128.x`), or on the IPv4 address of `--host` (a host name is refused), and serves only the package, `EchoDiar.appinstaller` and `EchoDiarDev.cer` (when present): every other path is a 404, with no folder listing. It answers HTTP Range requests (206, one range per request), which App Installer and Delivery Optimization use to download the package. In the VM, with the dev certificate trusted (`vm-install.ps1 -TrustCert`):

```powershell
Add-AppxPackage -AppInstallerFile http://<host>:8765/EchoDiar.appinstaller
```

To offer the next version, stop the script, replace the package with a higher version and run it again. The app shows its Update button once it finds it. `--render-only` writes the file without serving, `--host` and `--folder` override the defaults.

### §3.2 Troubleshooting and rollback

- The public run fails at "Check Trusted Signing secrets": its error names the missing secrets or variable (§3.0). It fails at the signing step: the placeholder is not implemented yet.
- The install fails for `Microsoft.VCLibs.140.00.UWPDesktop`: the `.appinstaller` lists no `Dependencies`, so App Installer relies on the framework being on the machine (it usually is when the Microsoft Store is). See the notes in [windows/README.md](../windows/README.md#testing-in-a-windows-vm).
- Updates not offered: check the feed is live with a higher Version (curl above), the copy was installed from the `.appinstaller` (a sideloaded `.msix` never updates; Settings > About says so) and has the same Publisher.

#### Roll back a bad release

Re-publish the previous feed, then delete the bad release:

```sh
git revert <feed-commit>   # the "chore(release): windows appinstaller vX.Y.Z" commit
git push origin main
gh release delete windows-vX.Y.Z --repo dinhanhthi/EchoDiar
git push origin :refs/tags/windows-vX.Y.Z && git tag -d windows-vX.Y.Z
```

Revert first: the previous feed points at the previous release's `.msixbundle`, so keep that release. Reverting the first public feed removes it, and the website hides the Windows download again. App Installer does not downgrade: users who already updated keep that version; ship a fixed, higher version to move them on.
