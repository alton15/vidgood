# Builds a single self-contained VideoPlayer.exe into .\dist
# Usage:  powershell -ExecutionPolicy Bypass -File publish.ps1
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$dist = Join-Path $PSScriptRoot "dist"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

dotnet publish VideoPlayer.csproj -c Release -o $dist -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$exe = Join-Path $dist "VideoPlayer.exe"
"{0}  ({1:N1} MB)" -f $exe, ((Get-Item $exe).Length / 1MB)
Get-ChildItem $dist -Exclude VideoPlayer.exe | ForEach-Object { "  extra file: $($_.Name)" }
