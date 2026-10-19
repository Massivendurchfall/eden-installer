# Baut dist\Eden-Installer.exe (eine einzelne exe, braucht nur das .NET Framework 4.8 aus Windows).
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

dotnet publish EdenInstaller.csproj -c Release -o dist --nologo
if ($LASTEXITCODE -ne 0) { throw "Build fehlgeschlagen" }

# Nur die exe zählt, die .config von net48 braucht es nicht.
Get-ChildItem dist | Where-Object { $_.Name -ne 'Eden-Installer.exe' } | Remove-Item -Recurse -Force
Get-Item dist\Eden-Installer.exe | Select-Object Name, Length, LastWriteTime
