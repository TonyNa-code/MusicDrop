param(
    [Parameter(Mandatory = $true)][string]$Ffmpeg,
    [Parameter(Mandatory = $true)][string]$SourcePng,
    [Parameter(Mandatory = $true)][string]$OutputIco
)

$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($SourcePng)
$output = [IO.Path]::GetFullPath($OutputIco)
if (-not [IO.File]::Exists($source)) { throw "Source PNG not found: $source" }
if (-not [IO.File]::Exists([IO.Path]::GetFullPath($Ffmpeg))) { throw "FFmpeg not found: $Ffmpeg" }
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output)) | Out-Null
$temporary = Join-Path ([IO.Path]::GetDirectoryName($output)) ('.icon-build-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporary) | Out-Null

try {
    $sizes = @(16, 24, 32, 48, 64, 128, 256)
    $images = @()
    foreach ($size in $sizes) {
        $png = Join-Path $temporary ("musicdrop-$size.png")
        & $Ffmpeg -hide_banner -loglevel error -y -i $source -vf "scale=${size}:${size}:flags=lanczos" -frames:v 1 $png
        if ($LASTEXITCODE -ne 0 -or -not [IO.File]::Exists($png)) { throw "FFmpeg failed to create ${size}px icon." }
        $images += [pscustomobject]@{ Size = $size; Bytes = [IO.File]::ReadAllBytes($png) }
    }

    $stream = [IO.File]::Open($output, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $writer = [IO.BinaryWriter]::new($stream)
        try {
            $writer.Write([uint16]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]$images.Count)
            $offset = 6 + 16 * $images.Count
            foreach ($image in $images) {
                $dimension = if ($image.Size -eq 256) { [byte]0 } else { [byte]$image.Size }
                $writer.Write($dimension)
                $writer.Write($dimension)
                $writer.Write([byte]0)
                $writer.Write([byte]0)
                $writer.Write([uint16]1)
                $writer.Write([uint16]32)
                $writer.Write([uint32]$image.Bytes.Length)
                $writer.Write([uint32]$offset)
                $offset += $image.Bytes.Length
            }
            foreach ($image in $images) { $writer.Write($image.Bytes) }
        }
        finally { $writer.Dispose() }
    }
    finally { $stream.Dispose() }
}
finally {
    if ([IO.Directory]::Exists($temporary)) { [IO.Directory]::Delete($temporary, $true) }
}

$hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "ICO: $output"
Write-Host "SHA-256: $hash"
