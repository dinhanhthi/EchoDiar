## Windows

Download `EchoDiar.cer` and `EchoDiar.appinstaller` from the Assets below (Windows 11, x64 and Arm64). The Windows files arrive within about an hour after the macOS ones.

1. Open `EchoDiar.cer` (click **Open** if Windows asks whether to open the file). Ignore "This CA Root certificate is not trusted..." and never put the certificate in Trusted Root: click **Install Certificate...**, choose **Local Machine**, click **Next** and **Yes** when Windows asks for permission, then **Place all certificates in the following store** > **Browse...** > **Trusted People** > **OK**, **Next** and **Finish**.
2. Open `EchoDiar.appinstaller` and click **Install**. App Installer installs EchoDiar from the feed at ed.dinhanhthi.com and keeps it updated.

Step by step, with what each window says: https://ed.dinhanhthi.com/docs.html#install-windows

**Why the extra step?** EchoDiar is not yet signed by a public certificate authority, so Windows trusts EchoDiar's own certificate only after you import it. It is open source, and GitHub Actions builds these files from the source of this release.

**Check the download** (optional, before step 1): in PowerShell, run `Get-FileHash .\EchoDiar.cer -Algorithm SHA256` and compare the hash with the EchoDiar.cer line of `SHA256SUMS-windows.txt`.

**Uninstalling:** after you uninstall EchoDiar, remove its certificate too, as described at https://ed.dinhanhthi.com/docs.html#windows-warnings.
