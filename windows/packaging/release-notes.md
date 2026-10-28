## Windows

Download **EchoDiar-Setup.exe** from the Assets below (Windows 11, x64 and Arm64). The Windows files arrive within about an hour after the macOS ones.

1. Open it. When Windows says "Windows protected your PC", click **More info**, then **Run anyway**.
2. When Windows asks if you want to allow an app from an "Unknown publisher" to make changes, click **Yes**. This happens only once, to trust the EchoDiar certificate.

Setup trusts the EchoDiar certificate, installs EchoDiar from ed.dinhanhthi.com and keeps it updated.

**Why the warnings?** EchoDiar is not yet signed by a public certificate authority. It is open source, and GitHub Actions builds these files from the source of this release.

**Check the download** (optional): in PowerShell, run `Get-FileHash .\EchoDiar-Setup.exe -Algorithm SHA256` and compare the hash with the EchoDiar-Setup.exe line of `SHA256SUMS-windows.txt`.

**Smart App Control:** if it blocks Setup (it has no "Run anyway" button), install EchoDiar with the PowerShell steps at https://ed.dinhanhthi.com/docs.html#windows-warnings.

**Uninstalling:** after you uninstall EchoDiar, remove its certificate too, as described on the same page.
