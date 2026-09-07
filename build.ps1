<#
.SYNOPSIS
    Builds, packages and installs the Hockey Fight screensaver.

.DESCRIPTION
    The Windows counterpart to the macOS Makefile. Publishes the project, renames
    the resulting executable to "Hockey Fight.scr", and can install it for the
    current user without administrator rights.

.EXAMPLE
    .\build.ps1                     # build and install for the current user
    .\build.ps1 -Task build         # build only, leave it in dist\
    .\build.ps1 -SelfContained      # bundle the .NET runtime into the .scr
    .\build.ps1 -Task release       # build self-contained into release\
    .\build.ps1 -Task uninstall     # remove it and clear the registry entry
    .\build.ps1 -Task clean
#>
[CmdletBinding()]
param(
    [ValidateSet('install', 'build', 'release', 'uninstall', 'clean')]
    [string]$Task = 'install',

    [switch]$SelfContained,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$root       = $PSScriptRoot
$project    = Join-Path $root 'HockeyFight.csproj'
$distDir    = Join-Path $root 'dist'
$releaseDir = Join-Path $root 'release'
$saverName  = 'Hockey Fight.scr'
$installDir = Join-Path $env:LOCALAPPDATA 'Hockey Fight'

function Invoke-Publish {
    param([string]$OutDir, [bool]$Standalone)

    if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

    Write-Host "Publishing ($(if ($Standalone) { 'self-contained' } else { 'framework-dependent' }))..."

    $publishArgs = @(
        'publish', $project,
        '-c', $Configuration,
        '-r', 'win-x64',
        '-o', $OutDir,
        '-p:PublishSingleFile=true',
        "-p:SelfContained=$($Standalone.ToString().ToLowerInvariant())",
        '-p:DebugType=none',
        '--nologo', '-v', 'quiet'
    )
    if ($Standalone) { $publishArgs += '-p:EnableCompressionInSingleFile=true' }

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

    # A .scr is just a PE executable with a different extension; Windows discovers
    # screensavers by extension, and shows the FileDescription as the display name.
    $exe = Join-Path $OutDir 'HockeyFight.exe'
    if (-not (Test-Path $exe)) { throw "Expected $exe to exist after publish" }

    $scr = Join-Path $OutDir $saverName
    Move-Item $exe $scr -Force

    $size = [math]::Round((Get-Item $scr).Length / 1MB, 1)
    Write-Host "  -> $scr  ($size MB)"
    return $scr
}

function Invoke-Install {
    $scr = Invoke-Publish -OutDir $distDir -Standalone $SelfContained.IsPresent

    # Stop a running instance so the file is not locked.
    Get-Process -Name 'HockeyFight', 'Hockey Fight' -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue

    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
    $installed = Join-Path $installDir $saverName
    Copy-Item $scr $installed -Force

    # Pointing SCRNSAVE.EXE at an arbitrary path avoids needing to write into
    # System32, so no elevation is required.
    Set-ItemProperty -Path 'HKCU:\Control Panel\Desktop' -Name 'SCRNSAVE.EXE'    -Value $installed
    Set-ItemProperty -Path 'HKCU:\Control Panel\Desktop' -Name 'ScreenSaveActive' -Value '1'

    $timeout = (Get-ItemProperty -Path 'HKCU:\Control Panel\Desktop' -Name 'ScreenSaveTimeOut' -ErrorAction SilentlyContinue).ScreenSaveTimeOut
    if (-not $timeout) {
        Set-ItemProperty -Path 'HKCU:\Control Panel\Desktop' -Name 'ScreenSaveTimeOut' -Value '300'
    }

    Write-Host ''
    Write-Host 'Screensaver built and installed successfully.' -ForegroundColor Green
    Write-Host "Installed to: $installed"
    Write-Host 'Open the Screen Saver Settings to preview it, or run:'
    Write-Host "  & '$installed' /s"
}

function Invoke-Uninstall {
    Get-Process -Name 'HockeyFight', 'Hockey Fight' -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue

    $current = (Get-ItemProperty -Path 'HKCU:\Control Panel\Desktop' -Name 'SCRNSAVE.EXE' -ErrorAction SilentlyContinue).'SCRNSAVE.EXE'
    if ($current -and $current.StartsWith($installDir, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-ItemProperty -Path 'HKCU:\Control Panel\Desktop' -Name 'SCRNSAVE.EXE' -ErrorAction SilentlyContinue
        Set-ItemProperty   -Path 'HKCU:\Control Panel\Desktop' -Name 'ScreenSaveActive' -Value '0'
        Write-Host 'Cleared the screensaver registry entry.'
    }

    if (Test-Path $installDir) {
        Remove-Item $installDir -Recurse -Force
        Write-Host "Removed $installDir"
    }

    Write-Host 'Uninstalled.' -ForegroundColor Green
}

switch ($Task) {
    'build' {
        $scr = Invoke-Publish -OutDir $distDir -Standalone $SelfContained.IsPresent
        Write-Host ''
        Write-Host "Built: $scr" -ForegroundColor Green
    }
    'install'   { Invoke-Install }
    'release'   {
        $scr = Invoke-Publish -OutDir $releaseDir -Standalone $true
        Write-Host ''
        Write-Host "Release built: $scr" -ForegroundColor Green
    }
    'uninstall' { Invoke-Uninstall }
    'clean' {
        foreach ($d in @((Join-Path $root 'bin'), (Join-Path $root 'obj'), $distDir)) {
            if (Test-Path $d) { Remove-Item $d -Recurse -Force }
        }
        Write-Host 'Clean complete.' -ForegroundColor Green
    }
}
