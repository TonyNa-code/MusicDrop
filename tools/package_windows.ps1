[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^v[0-9A-Za-z.-]+$')]
    [string]$Version,

    [string]$OutputDirectory = 'artifacts/release-windows',

    [string]$FfmpegArchive = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$distRoot = if ([IO.Path]::IsPathFullyQualified($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
}
[IO.Directory]::CreateDirectory($distRoot) | Out-Null

$workRoot = Join-Path ([IO.Path]::GetTempPath()) ('MusicDropWindowsRelease-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($workRoot) | Out-Null
$createdOutputs = [Collections.Generic.List[string]]::new()

$archiveName = 'ffmpeg-n8.1.2-34-g9b6c8969e0-win64-lgpl-shared-8.1.zip'
$archiveRoot = 'ffmpeg-n8.1.2-34-g9b6c8969e0-win64-lgpl-shared-8.1'
$archiveUrl = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-08-11-13-11/' + $archiveName
$archiveSha256 = '026f3ba22f0acf4fe58bf4da28a7eb64ffb107b270119684b91e4cace3b577aa'
$ffmpegRevision = 'n8.1.2-34-g9b6c8969e0-20260811'
$ffmpegFiles = [ordered]@{
    'bin/ffmpeg.exe' = '86a84607db881c93ac23ec8216b454e05ca8ae035ee8209fc2a9b10a845c2c84'
    'bin/ffprobe.exe' = '8e174683e435b089d7a9942afec5019e30ae6c550fcabfca3f917beb0768f7a6'
    'bin/avcodec-62.dll' = 'cc91ca4fc909f3d5a512e5b0d50a3d161305e005ca7febe969b5737acaef2475'
    'bin/avdevice-62.dll' = '2a229adf099eb360aad5bdda24a7f3d1a9d151db0e28365b6f428277360c320f'
    'bin/avfilter-11.dll' = 'e0d301cf78679caf8337a0babde8879924227a892e2e08abe04e9ec88bb9c351'
    'bin/avformat-62.dll' = '2fbd044d2a910035032d83dfd81d0f7fe442b73bea56341ccc171c941c62eb91'
    'bin/avutil-60.dll' = 'fd951227b0d1b574ed964d44ccca59422be1a821b67820600a4ac0a1b558e95a'
    'bin/swresample-6.dll' = '81d46648a06852f7123bc05501ec8c12bc396ad6f35b9ef2130ff9e3cadf80e5'
    'bin/swscale-9.dll' = '6f1214e30b4ebcef4468ff05954413c36ec83e4c8a0ed3dc7c6a04d42c26b0bd'
    'LICENSE.txt' = 'da7eabb7bafdf7d3ae5e9f223aa5bdc1eece45ac569dc21b3b037520b4464768'
}

function Assert-Sha256([string]$Path, [string]$Expected) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing release input: $Path" }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Expected) { throw "SHA-256 mismatch: $Path" }
}

function Copy-ReleaseDocuments([string]$Destination) {
    foreach ($name in @('README.md', 'README.zh-CN.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md',
            'TRADEMARKS.md', 'PRIVACY.md', 'SECURITY.md')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot $name) -Destination $Destination
    }

    $dotnetRoot = Split-Path -Parent (Get-Command dotnet -ErrorAction Stop).Source
    Copy-Item -LiteralPath (Join-Path $dotnetRoot 'LICENSE.txt') `
        -Destination (Join-Path $Destination 'DOTNET-LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $dotnetRoot 'ThirdPartyNotices.txt') `
        -Destination (Join-Path $Destination 'DOTNET-THIRD-PARTY-NOTICES.txt')
}

function Write-RecursiveSums([string]$Directory) {
    $lines = foreach ($file in Get-ChildItem -LiteralPath $Directory -Recurse -File |
            Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object FullName) {
        $relative = $file.FullName.Substring($Directory.TrimEnd('\').Length + 1).Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $relative"
    }
    $lines | Set-Content -LiteralPath (Join-Path $Directory 'SHA256SUMS.txt') -Encoding ascii
}

function Write-ZipHash([string]$ZipPath) {
    $hashPath = $ZipPath + '.sha256.txt'
    $hash = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($ZipPath))" | Set-Content -LiteralPath $hashPath -Encoding ascii
    $createdOutputs.Add($hashPath)
}

try {
    $archive = if ([string]::IsNullOrWhiteSpace($FfmpegArchive)) {
        $download = Join-Path $workRoot $archiveName
        Invoke-WebRequest -Uri $archiveUrl -OutFile $download
        $download
    } else {
        [IO.Path]::GetFullPath($FfmpegArchive)
    }
    $archiveInfo = Get-Item -LiteralPath $archive
    if ($archiveInfo.Length -le 0 -or $archiveInfo.Length -gt 128MB) {
        throw 'FFmpeg archive size is outside the allowed range.'
    }
    Assert-Sha256 $archive $archiveSha256

    $expanded = Join-Path $workRoot 'ffmpeg-expanded'
    Expand-Archive -LiteralPath $archive -DestinationPath $expanded
    $ffmpegRoot = Join-Path $expanded $archiveRoot
    foreach ($entry in $ffmpegFiles.GetEnumerator()) {
        Assert-Sha256 (Join-Path $ffmpegRoot $entry.Key.Replace('/', '\')) $entry.Value
    }

    $ffmpegExe = Join-Path $ffmpegRoot 'bin\ffmpeg.exe'
    $versionOutput = (& $ffmpegExe -hide_banner -version 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0 -or -not $versionOutput.Contains($ffmpegRevision) -or
        -not $versionOutput.Contains('--enable-version3') -or
        -not $versionOutput.Contains('--enable-shared') -or
        -not $versionOutput.Contains('--enable-libmp3lame') -or
        -not $versionOutput.Contains('--enable-libvorbis') -or
        $versionOutput.Contains('--enable-gpl')) {
        throw 'FFmpeg version or LGPL capability verification failed.'
    }
    $encoders = (& $ffmpegExe -hide_banner -encoders 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0 -or -not $encoders.Contains('libmp3lame') -or
        -not $encoders.Contains('libvorbis') -or -not $encoders.Contains(' flac ')) {
        throw 'FFmpeg is missing a required audio encoder.'
    }

    $publishGui = Join-Path $workRoot 'publish-gui'
    $publishCli = Join-Path $workRoot 'publish-cli'
    & dotnet publish (Join-Path $repoRoot 'MusicDrop3\MFlacDrop.csproj') `
        -c Release -r win-x64 --self-contained true --no-restore -o $publishGui
    if ($LASTEXITCODE -ne 0) { throw 'GUI publish failed.' }
    & dotnet publish (Join-Path $repoRoot 'MusicDrop3.Cli\MusicDrop3.Cli.csproj') `
        -c Release -r win-x64 --self-contained true --no-restore -o $publishCli
    if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed.' }

    $slimRoot = Join-Path $workRoot 'package-slim'
    $fullRoot = Join-Path $workRoot 'package-full'
    [IO.Directory]::CreateDirectory($slimRoot) | Out-Null
    [IO.Directory]::CreateDirectory($fullRoot) | Out-Null
    Copy-Item -LiteralPath (Join-Path $publishGui 'MusicDrop3.exe') -Destination $slimRoot
    Copy-Item -LiteralPath (Join-Path $publishCli 'MusicDrop3.Cli.exe') -Destination $slimRoot
    Copy-ReleaseDocuments $slimRoot
    Get-ChildItem -LiteralPath $slimRoot -Force | Copy-Item -Destination $fullRoot -Recurse

    $ffmpegTarget = Join-Path $fullRoot 'ffmpeg'
    foreach ($entry in $ffmpegFiles.GetEnumerator()) {
        $destination = Join-Path $ffmpegTarget $entry.Key.Replace('/', '\')
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        Copy-Item -LiteralPath (Join-Path $ffmpegRoot $entry.Key.Replace('/', '\')) -Destination $destination
    }
    @(
        'MusicDrop bundled FFmpeg provenance',
        '',
        "Build: $ffmpegRevision",
        "Archive: $archiveName",
        "Archive SHA-256: $archiveSha256",
        'Build project: https://github.com/BtbN/FFmpeg-Builds',
        'FFmpeg project: https://ffmpeg.org/',
        'Source revision: https://github.com/FFmpeg/FFmpeg/commit/9b6c8969e0'
    ) | Set-Content -LiteralPath (Join-Path $ffmpegTarget 'FFMPEG-SOURCE.txt') -Encoding utf8

    Write-RecursiveSums $slimRoot
    Write-RecursiveSums $fullRoot

    $slimZip = Join-Path $distRoot "MusicDrop-$Version-Slim-Windows-x64.zip"
    $fullZip = Join-Path $distRoot "MusicDrop-$Version-Full-Windows-x64.zip"
    foreach ($path in @($slimZip, $fullZip, $slimZip + '.sha256.txt', $fullZip + '.sha256.txt')) {
        if (Test-Path -LiteralPath $path) { throw "Refusing to overwrite release output: $path" }
    }
    Compress-Archive -Path (Join-Path $slimRoot '*') -DestinationPath $slimZip -CompressionLevel Optimal
    $createdOutputs.Add($slimZip)
    Compress-Archive -Path (Join-Path $fullRoot '*') -DestinationPath $fullZip -CompressionLevel Optimal
    $createdOutputs.Add($fullZip)
    Write-ZipHash $slimZip
    Write-ZipHash $fullZip

    Get-Item -LiteralPath $slimZip, $fullZip | Select-Object FullName, Length
}
catch {
    foreach ($path in $createdOutputs) {
        try { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force } } catch { }
    }
    throw
}
finally {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    $resolvedWork = [IO.Path]::GetFullPath($workRoot)
    if ($resolvedWork.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedWork)) {
        Remove-Item -LiteralPath $resolvedWork -Recurse -Force
    }
}
