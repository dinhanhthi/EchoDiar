# Installs a CI build of EchoDiar in the Windows QA VM (Windows PowerShell 5.1).
#   Elevated:  vm-install.ps1 -Folder <artifact folder> -TrustCert
#   As user:   vm-install.ps1 -Folder <artifact folder> -Install -Launch
param(
    [Parameter(Mandatory)][string]$Folder,
    [switch]$TrustCert,
    [switch]$Install,
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$PackageName = 'dinh.thi.EchoDiar'

function Assert-Folder {
    if (-not (Test-Path -LiteralPath $Folder -PathType Container)) {
        throw "Folder '$Folder' is not reachable from this session (an elevated session may not see the Z: drive). Copy the folder to C:\EchoDiarQA\builds\<run> first and pass that path."
    }
}

function Test-Elevated {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-Arch {
    # An emulated shell reports its own arch; PROCESSOR_ARCHITEW6432 then holds the machine's.
    $machine = $env:PROCESSOR_ARCHITEW6432
    if (-not $machine) { $machine = $env:PROCESSOR_ARCHITECTURE }
    switch ($machine) {
        'ARM64' { return 'arm64' }
        'AMD64' { return 'x64' }
        default { throw "Unsupported processor architecture '$machine' (expected ARM64 or AMD64)." }
    }
}

function Invoke-TrustCert {
    if (-not (Test-Elevated)) {
        throw '-TrustCert needs an elevated (administrator) session.'
    }
    Assert-Folder
    $cer = @(Get-ChildItem -LiteralPath $Folder -Recurse -File -Filter 'EchoDiarDev.cer')
    if ($cer.Count -eq 0) {
        throw "EchoDiarDev.cer not found under '$Folder'."
    }
    $imported = Import-Certificate -FilePath $cer[0].FullName -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople'
    Write-Host "Trusted certificate $($imported.Subject) ($($imported.Thumbprint)) from $($cer[0].FullName) in LocalMachine\TrustedPeople."
}

function Invoke-Install {
    Assert-Folder
    $arch = Get-Arch
    $archPattern = "(?i)(^|[_.\-])$arch([_.\-]|$)"
    $candidates = @(Get-ChildItem -LiteralPath $Folder -Recurse -File -Filter '*.msix' |
        Where-Object { $_.FullName -notmatch '(?i)\\Dependencies\\' -and $_.BaseName -match $archPattern })
    if ($candidates.Count -eq 0) {
        throw "No $arch .msix found under '$Folder'."
    }
    if ($candidates.Count -gt 1) {
        throw "Several $arch .msix files under '$Folder': $(($candidates | ForEach-Object { $_.Name }) -join ', ')."
    }
    $msix = $candidates[0]

    $work = Join-Path $env:TEMP 'EchoDiarInstall'
    if (Test-Path -LiteralPath $work) {
        Remove-Item -LiteralPath $work -Recurse -Force
    }
    New-Item -ItemType Directory -Path $work | Out-Null
    $localMsix = Join-Path $work $msix.Name
    Copy-Item -LiteralPath $msix.FullName -Destination $localMsix

    $dependencies = @()
    $depFolder = Join-Path $msix.DirectoryName "Dependencies\$arch"
    if (Test-Path -LiteralPath $depFolder -PathType Container) {
        $localDeps = Join-Path $work 'Dependencies'
        New-Item -ItemType Directory -Path $localDeps | Out-Null
        foreach ($dep in @(Get-ChildItem -LiteralPath $depFolder -File | Where-Object { $_.Extension -match '(?i)^\.(msix|appx)$' })) {
            $target = Join-Path $localDeps $dep.Name
            Copy-Item -LiteralPath $dep.FullName -Destination $target
            $dependencies += $target
        }
    }

    Get-Process EchoDiar* -ErrorAction SilentlyContinue | Stop-Process -Force

    $addArgs = @{
        Path                      = $localMsix
        ForceUpdateFromAnyVersion = $true
        ForceApplicationShutdown  = $true
    }
    if ($dependencies.Count -gt 0) {
        $addArgs['DependencyPath'] = $dependencies
    }
    Add-AppxPackage @addArgs

    $pkg = Get-AppxPackage -Name $PackageName | Select-Object -First 1
    if ($null -eq $pkg) {
        throw "Add-AppxPackage finished but $PackageName is not installed."
    }
    Write-Host "Installed $($pkg.PackageFullName) from $($msix.FullName) with $($dependencies.Count) dependency package(s)."
}

function Invoke-Launch {
    $pkg = Get-AppxPackage -Name $PackageName | Select-Object -First 1
    if ($null -eq $pkg) {
        throw "$PackageName is not installed; run with -Install first."
    }
    $target = "shell:AppsFolder\$($pkg.PackageFamilyName)!App"
    Start-Process $target
    Write-Host "Launched $target."
}

try {
    if (-not ($TrustCert -or $Install -or $Launch)) {
        throw 'Nothing to do: pass -TrustCert, -Install and/or -Launch.'
    }
    if ($TrustCert) { Invoke-TrustCert }
    if ($Install) { Invoke-Install }
    if ($Launch) { Invoke-Launch }
    exit 0
}
catch {
    [Console]::Error.WriteLine("vm-install: $($_.Exception.Message)")
    exit 1
}
