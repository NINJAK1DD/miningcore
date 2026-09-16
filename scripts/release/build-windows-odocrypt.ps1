[CmdletBinding()]
param(
    [string] $SourceRoot,
    [switch] $VerifyOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$nativeRoot = if([string]::IsNullOrWhiteSpace($SourceRoot)) {
    Join-Path $repositoryRoot 'src\Native'
}
else {
    [System.IO.Path]::GetFullPath($SourceRoot)
}
$projectPath = Join-Path $nativeRoot 'libodocrypt\libodocrypt.vcxproj'
$manifestPath = Join-Path $nativeRoot 'libodocrypt\upstream.sha256'
$outputPath = Join-Path $nativeRoot 'libodocrypt\bin\x64\Release\libodocrypt.dll'

function Get-CanonicalTextSha256 {
    param([Parameter(Mandatory)][string] $Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $canonical = New-Object System.IO.MemoryStream

    try {
        for($index = 0; $index -lt $bytes.Length; $index++) {
            if($bytes[$index] -eq 13 -and
                ($index + 1 -eq $bytes.Length -or $bytes[$index + 1] -eq 10)) {
                if($index + 1 -lt $bytes.Length) {
                    $canonical.WriteByte(10)
                    $index++
                }
            }
            else {
                $canonical.WriteByte($bytes[$index])
            }
        }

        $canonical.Position = 0
        $sha256 = [System.Security.Cryptography.SHA256]::Create()

        try {
            return ([System.BitConverter]::ToString(
                $sha256.ComputeHash($canonical))).Replace('-', '').ToLowerInvariant()
        }
        finally {
            $sha256.Dispose()
        }
    }
    finally {
        $canonical.Dispose()
    }
}

function Get-FileSha256 {
    param([Parameter(Mandatory)][string] $Path)

    $stream = [System.IO.File]::OpenRead($Path)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()

    try {
        return ([System.BitConverter]::ToString(
            $sha256.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
        $stream.Dispose()
    }
}

function Test-PinnedSources {
    if(-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Pinned Odocrypt manifest is missing: $manifestPath"
    }

    $nativeRootPrefix = $nativeRoot.TrimEnd('\') + '\'
    $verified = 0

    foreach($entry in [System.IO.File]::ReadAllLines($manifestPath)) {
        $line = $entry.TrimEnd("`r")

        if($line -notmatch '^([a-f0-9]{64})  ([A-Za-z0-9._/-]+)$') {
            throw 'Pinned Odocrypt manifest contains a malformed entry'
        }

        $expected = $Matches[1]
        $relativePath = $Matches[2]

        if([System.IO.Path]::IsPathRooted($relativePath) -or
            $relativePath.Split('/') -contains '..') {
            throw 'Pinned Odocrypt manifest contains an unsafe path'
        }

        $sourcePath = [System.IO.Path]::GetFullPath(
            (Join-Path $nativeRoot $relativePath.Replace('/', '\')))

        if(-not $sourcePath.StartsWith(
            $nativeRootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'Pinned Odocrypt manifest path escapes the native source root'
        }

        if(-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "Pinned Odocrypt source is missing: $relativePath"
        }

        $attributes = [System.IO.File]::GetAttributes($sourcePath)
        if(($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Pinned Odocrypt source must not be a reparse point: $relativePath"
        }

        $actual = Get-FileSha256 -Path $sourcePath
        if($actual -ne $expected -and
            (Get-CanonicalTextSha256 -Path $sourcePath) -ne $expected) {
            throw "Pinned Odocrypt source identity mismatch: $relativePath"
        }

        $verified++
    }

    if($verified -eq 0) {
        throw 'Pinned Odocrypt manifest contains no files'
    }

    Write-Host "Pinned Odocrypt source identity verified for $verified file(s)"
}

function Find-MSBuild {
    if($env:MSBUILD_EXE_PATH -and
        $env:MSBUILD_EXE_PATH.EndsWith('.exe', [System.StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $env:MSBUILD_EXE_PATH -PathType Leaf)) {
        return (Resolve-Path -LiteralPath $env:MSBUILD_EXE_PATH).Path
    }

    $command = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if($command) {
        return $command.Source
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} `
        'Microsoft Visual Studio\Installer\vswhere.exe'
    if(Test-Path -LiteralPath $vswhere -PathType Leaf) {
        $candidate = & $vswhere -latest -products '*' `
            -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
            -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1

        if($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw ('Visual Studio Build Tools with the Desktop development with C++ workload ' +
        'and the v143 toolset are required to build the Windows Odocrypt runtime')
}

Test-PinnedSources
if($VerifyOnly) {
    return
}

$msbuild = Find-MSBuild
$arguments = @(
    $projectPath,
    '/m',
    '/nologo',
    '/verbosity:minimal',
    '/p:Configuration=Release',
    '/p:Platform=x64'
)

if($env:MININGCORE_WINDOWS_PLATFORM_TOOLSET) {
    $arguments += "/p:PlatformToolset=$($env:MININGCORE_WINDOWS_PLATFORM_TOOLSET)"
}

Write-Host "Building reviewed Windows Odocrypt runtime with $msbuild"
& $msbuild @arguments
if($LASTEXITCODE -ne 0) {
    throw ("Windows Odocrypt build failed with exit status $LASTEXITCODE. " +
        'Install the v143 C++ toolset, or set MININGCORE_WINDOWS_PLATFORM_TOOLSET ' +
        'to an installed compatible toolset for local compatibility testing.')
}

if(-not (Test-Path -LiteralPath $outputPath -PathType Leaf)) {
    throw "Windows Odocrypt build did not produce its expected output: $outputPath"
}

$outputAttributes = [System.IO.File]::GetAttributes($outputPath)
if(($outputAttributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'Windows Odocrypt build output must not be a reparse point'
}

Write-Host "Built reviewed Windows Odocrypt runtime: $outputPath"
