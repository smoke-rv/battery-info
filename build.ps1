# Builds BatteryInfo.exe with the C# compiler that ships with Windows (nothing to install).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out  = Join-Path $root 'BatteryInfo.exe'
$ico  = Join-Path $root 'assets\app.ico'
$src  = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object FullName

$iconArg = @()
if (Test-Path $ico) { $iconArg = @("/win32icon:$ico") }

& $csc /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ "/out:$out" @iconArg `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll $src
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "Done: $out"
