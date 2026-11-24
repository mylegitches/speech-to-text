<#
.SYNOPSIS
  Builds SpeechToText and installs it for the current user. No admin rights needed.

.DESCRIPTION
  publish -> %LOCALAPPDATA%\Programs\SpeechToText\SpeechToText.exe -> Start menu shortcut -> launch.
  Run it again to update. "Start with Windows" is moved to the installed copy if it was on.

.EXAMPLE
  .\install.ps1              # build, install and start
  .\install.ps1 -Uninstall   # remove the app; keeps your settings and saved keys
#>
param(
    [switch]$Uninstall,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$name     = 'SpeechToText'
$dest     = Join-Path $env:LOCALAPPDATA "Programs\$name"
$exe      = Join-Path $dest "$name.exe"
$shortcut = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$name.lnk"
$runKey   = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

function Stop-App {
    $running = Get-Process -Name $name -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host "Closing the running $name..."
        $running | Stop-Process -Force
        $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    }
}

if ($Uninstall) {
    Stop-App
    Remove-ItemProperty -Path $runKey -Name $name -ErrorAction SilentlyContinue
    Remove-Item $shortcut -ErrorAction SilentlyContinue
    Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "$name is uninstalled."
    Write-Host "Your settings are still in $env:APPDATA\$name and your API keys in Windows Credential Manager (entries starting with '${name}:')."
    return
}

# 1. Build
$publish = Join-Path $PSScriptRoot 'publish'
Write-Host 'Building (about 30 s)...'
dotnet publish (Join-Path $PSScriptRoot "src\$name") -c Release -o $publish -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed. See the errors above.' }

# 2. Install
Stop-App
New-Item -ItemType Directory -Force -Path $dest | Out-Null
# Windows can hold the old exe for a moment after the process exits, so retry briefly.
for ($i = 1; ; $i++) {
    try { Copy-Item (Join-Path $publish "$name.exe") $exe -Force; break }
    catch [System.IO.IOException] {
        if ($i -ge 20) { throw "Couldn't replace $exe because it is still in use. Close $name and run this again." }
        Start-Sleep -Milliseconds 250
    }
}

# 3. Start menu shortcut
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $dest
$link.Description = 'Dictation and voice notes from a hotkey'
$link.Save()

# 4. Keep "Start with Windows" pointing at the installed copy
if (Get-ItemProperty -Path $runKey -Name $name -ErrorAction SilentlyContinue) {
    Set-ItemProperty -Path $runKey -Name $name -Value "`"$exe`""
}

$mb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "Installed $name ($mb MB) to $dest"
Write-Host 'Start menu: SpeechToText'

if (-not $NoLaunch) { Start-Process $exe }
