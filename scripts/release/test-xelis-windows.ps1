[CmdletBinding()]
param([ValidateSet('v142', 'v143')][string] $PlatformToolset = 'v143')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if(-not $msbuild) { throw 'Windows Xelis vectors require the Visual Studio C++ workload' }
$project = Join-Path $PSScriptRoot 'fixtures\xelis-windows-vectors.vcxproj'
& $msbuild $project /nologo /verbosity:minimal /p:Configuration=Release /p:Platform=x64 "/p:PlatformToolset=$PlatformToolset"
if($LASTEXITCODE -ne 0) { throw 'Windows Xelis source-vector build failed' }
$binary = Join-Path $PSScriptRoot '..\..\build\xelis-windows\xelis-windows-vectors.exe'
& $binary
if($LASTEXITCODE -ne 0) { throw 'Windows Xelis source vectors failed' }
