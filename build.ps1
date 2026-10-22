[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [ValidateSet("x64", "x86", "ARM64")]
    [string]$Platform = "x64",

    [string]$OutputDir = ".artifacts\dist\NiceWP-0.0.18-win-x64",

    [switch]$SkipRestore,

    [switch]$RunTests,

    [switch]$CreateZip,

    [switch]$CreateInstaller
)

$ErrorActionPreference = "Stop"
$RepoRoot = $PSScriptRoot
$SolutionPath = Join-Path $RepoRoot "NiceWallpaper.sln"
$TargetFramework = "net10.0-windows10.0.19041.0"
$WindowsAppSdkRuntimeVersion = "2.5.1"

Write-Host "=== NiceWP Build & Package Script ===" -ForegroundColor Cyan
Write-Host "Configuration : $Configuration"
Write-Host "Platform      : $Platform"
Write-Host "Output Dir    : $OutputDir"
Write-Host "-------------------------------------"

# Stop running NiceWP processes to avoid file locks and stale background hosts
$RunningProcesses = Get-Process -Name "NiceWP.App", "NiceWP.Runtime.Host" -ErrorAction SilentlyContinue
if ($RunningProcesses) {
    Write-Host "[0/4] Stopping running NiceWP processes..." -ForegroundColor Yellow
    $RunningProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 300
}

# 1. Restore NuGet packages
if (-not $SkipRestore) {
    Write-Host "[1/4] Restoring NuGet packages..." -ForegroundColor Yellow
    & dotnet restore $SolutionPath
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed with exit code $LASTEXITCODE."
    }
} else {
    Write-Host "[1/4] Skipping NuGet restore." -ForegroundColor DarkGray
}

# 2. Ensure Windows App Runtime 2.5.1 MSIX dependencies (DDLM / Main / Singleton) are registered
$MsixArchFolder = switch ($Platform) {
    "x64"   { "win10-x64" }
    "x86"   { "win10-x86" }
    "ARM64" { "win10-arm64" }
}
$NuGetMsixDir = Join-Path $env:USERPROFILE ".nuget\packages\microsoft.windowsappsdk.runtime\$WindowsAppSdkRuntimeVersion\tools\MSIX\$MsixArchFolder"
if (Test-Path $NuGetMsixDir) {
    $ddlmInstalled = Get-AppxPackage -Name "Microsoft.WinAppRuntime.DDLM.$WindowsAppSdkRuntimeVersion*" -ErrorAction SilentlyContinue
    if (-not $ddlmInstalled) {
        Write-Host "[2/4] Installing Windows App Runtime $WindowsAppSdkRuntimeVersion MSIX dependencies ($MsixArchFolder)..." -ForegroundColor Yellow
        $msixFiles = @(
            "Microsoft.WindowsAppRuntime.2.msix",
            "Microsoft.WindowsAppRuntime.DDLM.2.msix",
            "Microsoft.WindowsAppRuntime.Main.2.msix",
            "Microsoft.WindowsAppRuntime.Singleton.2.msix"
        )
        foreach ($msix in $msixFiles) {
            $msixPath = Join-Path $NuGetMsixDir $msix
            if (Test-Path $msixPath) {
                Add-AppxPackage -Path $msixPath -ErrorAction SilentlyContinue
            }
        }
    } else {
        Write-Host "[2/4] Windows App Runtime $WindowsAppSdkRuntimeVersion MSIX dependencies are already installed." -ForegroundColor Green
    }
} else {
    Write-Host "[2/4] Windows App Runtime MSIX cache not found at $NuGetMsixDir; skipping registration check." -ForegroundColor DarkGray
}

# 3. Build Solution
Write-Host "[3/4] Building solution ($Configuration | $Platform)..." -ForegroundColor Yellow
& dotnet build $SolutionPath -c $Configuration -p:Platform=$Platform --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE."
}

if ($RunTests) {
    Write-Host "[Test] Running unit tests..." -ForegroundColor Yellow
    & dotnet test $SolutionPath -c $Configuration -p:Platform=$Platform --no-build
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet test failed with exit code $LASTEXITCODE."
    }
}

# 4. Stage distribution output (NiceWP.App + NiceWP.Runtime.Host)
Write-Host "[4/4] Packaging distribution output..." -ForegroundColor Yellow

$AppBinDir = Join-Path $RepoRoot "src\NiceWP.App\bin\$Platform\$Configuration\$TargetFramework"
$HostBinDir = Join-Path $RepoRoot "src\NiceWP.Runtime.Host\bin\$Platform\$Configuration\$TargetFramework"

if (-not (Test-Path $AppBinDir)) {
    throw "App build output directory not found: $AppBinDir"
}
if (-not (Test-Path $HostBinDir)) {
    throw "Runtime.Host build output directory not found: $HostBinDir"
}

$ResolvedOutputDir = if ([System.IO.Path]::IsPathRooted($OutputDir)) {
    $OutputDir
} else {
    Join-Path $RepoRoot $OutputDir
}

if (Test-Path $ResolvedOutputDir) {
    Remove-Item -Path $ResolvedOutputDir -Recurse -Force
}
New-Item -ItemType Directory -Path $ResolvedOutputDir -Force | Out-Null

# Copy Runtime.Host output first, then App output (both share the same WinUI/SDK dependencies)
Copy-Item -Path (Join-Path $HostBinDir "*") -Destination $ResolvedOutputDir -Recurse -Force
Copy-Item -Path (Join-Path $AppBinDir "*") -Destination $ResolvedOutputDir -Recurse -Force

$AppExePath = Join-Path $ResolvedOutputDir "NiceWP.App.exe"
$HostExePath = Join-Path $ResolvedOutputDir "NiceWP.Runtime.Host.exe"

if (-not (Test-Path $AppExePath) -or -not (Test-Path $HostExePath)) {
    throw "Packaging verification failed: NiceWP.App.exe or NiceWP.Runtime.Host.exe is missing in $ResolvedOutputDir"
}

if ($CreateZip) {
    $ZipPath = "$ResolvedOutputDir.zip"
    if (Test-Path $ZipPath) {
        Remove-Item -Path $ZipPath -Force
    }
    Compress-Archive -Path (Join-Path $ResolvedOutputDir "*") -DestinationPath $ZipPath -Force
    Write-Host "Zip Archive   : $ZipPath" -ForegroundColor Green
}

if ($CreateInstaller) {
    Write-Host "[Installer] Compiling Inno Setup installer..." -ForegroundColor Yellow
    $IsccPath = $null
    $KnownPaths = @(
        "C:\Program Files (x86)\Inno Setup 6\iscc.exe",
        "C:\Program Files\Inno Setup 6\iscc.exe"
    )
    foreach ($candidate in $KnownPaths) {
        if (Test-Path $candidate) {
            $IsccPath = $candidate
            break
        }
    }
    if (-not $IsccPath) {
        $cmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
        if ($cmd) {
            $IsccPath = $cmd.Source
        }
    }
    if (-not $IsccPath) {
        throw "Inno Setup compiler (iscc.exe) not found. Please install Inno Setup 6 or add it to PATH."
    }

    $IssScript = Join-Path $RepoRoot "packaging\NiceWP.iss"
    if (-not (Test-Path $IssScript)) {
        throw "Installer script not found: $IssScript"
    }

    $InstallerOutputDir = Join-Path $RepoRoot ".artifacts\installer"
    if (-not (Test-Path $InstallerOutputDir)) {
        New-Item -ItemType Directory -Path $InstallerOutputDir -Force | Out-Null
    }

    & $IsccPath $IssScript
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
    }

    $InstallerExe = Join-Path $InstallerOutputDir "NiceWP-0.0.18-win-x64-Setup.exe"
    Write-Host "Installer Exe : $InstallerExe" -ForegroundColor Green
}

Write-Host "-------------------------------------"
Write-Host "Build and packaging completed successfully!" -ForegroundColor Green
Write-Host "App Executable  : $AppExePath" -ForegroundColor Cyan
Write-Host "Host Executable : $HostExePath" -ForegroundColor Cyan
