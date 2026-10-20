# Builds dist\Eden-Installer.exe (a single exe, only needs the .NET Framework 4.8 that comes with Windows).
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

dotnet publish EdenInstaller.csproj -c Release -o dist --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

# Only the exe matters, the .config of net48 is not needed.
Get-ChildItem dist | Where-Object { $_.Name -ne 'Eden-Installer.exe' } | Remove-Item -Recurse -Force
Get-Item dist\Eden-Installer.exe | Select-Object Name, Length, LastWriteTime
