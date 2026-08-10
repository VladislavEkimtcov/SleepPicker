<#
.SYNOPSIS
    Regenerates assets\SleepPicker.ico from assets\SleepPicker.png.

.DESCRIPTION
    The artwork is a flat-colour crescent drawn on a solid white background. This script
    turns that white into transparency, scales the result down to the sizes the shell asks
    for, and packs them into a multi-size .ico. The output is committed so a normal build
    needs nothing but MSBuild.

    Everything here uses System.Drawing, which ships with the in-box .NET Framework: the
    target machines (Windows IoT Enterprise LTSC) have no image editor and no package
    manager.

    Entries are written as classic 32-bit BGRA DIBs rather than PNG-compressed entries,
    because System.Drawing.Icon does not reliably decode PNG entries.

    Run with Windows PowerShell 5.1:
        powershell.exe -ExecutionPolicy Bypass -File tools\MakeIcon.ps1
#>
[CmdletBinding()]
param(
    [string] $SourcePath,
    [string] $OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# $PSScriptRoot is not bound yet when parameter defaults are evaluated, so the
# repo-relative defaults are resolved here instead.
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $SourcePath) { $SourcePath = Join-Path $repoRoot 'assets\SleepPicker.png' }
if (-not $OutputPath) { $OutputPath = Join-Path $repoRoot 'assets\SleepPicker.ico' }

Add-Type -AssemblyName System.Drawing

# Up to 64 only. A 256px entry would have to be stored uncompressed here (256 KB on its
# own) because PNG-compressed entries are the part System.Drawing.Icon handles badly, and
# a tray utility never renders larger than the jumbo shell icon anyway.
$sizes = @(16, 20, 24, 32, 48, 64)

function Read-Rgba {
    <# The source PNG as one straight-alpha [double[]] in R,G,B,A order, row by row. #>
    param([System.Drawing.Bitmap] $Bitmap)

    $w = $Bitmap.Width
    $h = $Bitmap.Height

    $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
    $data = $Bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                             [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $raw = New-Object byte[] ($data.Stride * $h)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $raw, 0, $raw.Length)
        $stride = $data.Stride
    }
    finally {
        $Bitmap.UnlockBits($data)
    }

    $rgba = New-Object double[] ($w * $h * 4)
    for ($y = 0; $y -lt $h; $y++) {
        for ($x = 0; $x -lt $w; $x++) {
            $src = ($y * $stride) + ($x * 4)   # BGRA in memory
            $dst = (($y * $w) + $x) * 4
            $rgba[$dst]     = [double] $raw[$src + 2]
            $rgba[$dst + 1] = [double] $raw[$src + 1]
            $rgba[$dst + 2] = [double] $raw[$src]
            $rgba[$dst + 3] = [double] $raw[$src + 3]
        }
    }
    return $rgba
}

function Get-InkColors {
    <#
        The flat colours the artwork is drawn in -- here the gold fill and the dark rim.

        They are found rather than hard-coded so that redrawing the PNG in different
        colours needs no edit to this script: colours far enough from white to be ink are
        counted, the most common ones win, and near-duplicates (the odd stray shade left by
        the drawing tool) collapse into the first ink that claimed them.
    #>
    param([double[]] $Rgba)

    $counts = @{}
    for ($i = 0; $i -lt $Rgba.Length; $i += 4) {
        $dr = 255.0 - $Rgba[$i]
        $dg = 255.0 - $Rgba[$i + 1]
        $db = 255.0 - $Rgba[$i + 2]
        if ((($dr * $dr) + ($dg * $dg) + ($db * $db)) -lt (110.0 * 110.0)) { continue }

        $key = '{0},{1},{2}' -f [int] $Rgba[$i], [int] $Rgba[$i + 1], [int] $Rgba[$i + 2]
        if ($counts.ContainsKey($key)) { $counts[$key]++ } else { $counts[$key] = 1 }
    }

    $inks = @()
    foreach ($entry in ($counts.GetEnumerator() | Sort-Object -Property Value -Descending)) {
        $parts = $entry.Key -split ','
        $candidate = @([double] $parts[0], [double] $parts[1], [double] $parts[2])

        $isDuplicate = $false
        foreach ($ink in $inks) {
            $dr = $ink[0] - $candidate[0]
            $dg = $ink[1] - $candidate[1]
            $db = $ink[2] - $candidate[2]
            if ((($dr * $dr) + ($dg * $dg) + ($db * $db)) -lt (60.0 * 60.0)) {
                $isDuplicate = $true
                break
            }
        }
        if (-not $isDuplicate) { $inks += , $candidate }
        if ($inks.Count -ge 4) { break }
    }

    if ($inks.Count -eq 0) {
        throw "No ink found in $SourcePath -- is the image blank?"
    }
    return $inks
}

function Remove-White {
    <#
        White out, alpha in.

        Every pixel of flat artwork on white is some ink laid over white at some coverage,
        so a pixel sits on the line from white to its ink and how far along that line it
        sits is its alpha. For each pixel the ink whose line passes closest is taken as the
        one it was drawn with, the projection onto that line gives the alpha, and dividing
        the white back out recovers the ink's own colour -- which is what stops the keyed
        edges from carrying a white fringe onto a dark taskbar.

        Pixels at or past their ink keep both their colour and full opacity, so shading
        inside the glyph (the rim blending into the fill) survives untouched.
    #>
    param([double[]] $Rgba, [object[]] $Inks)

    $lines = @()
    foreach ($ink in $Inks) {
        # Each element is parenthesised: PowerShell's comma binds tighter than arithmetic.
        $v = @(($ink[0] - 255.0), ($ink[1] - 255.0), ($ink[2] - 255.0))
        $lines += , @{ Ink = $ink; V = $v; LenSq = ($v[0] * $v[0]) + ($v[1] * $v[1]) + ($v[2] * $v[2]) }
    }

    $out = New-Object double[] $Rgba.Length
    for ($i = 0; $i -lt $Rgba.Length; $i += 4) {
        $u = @(($Rgba[$i] - 255.0), ($Rgba[$i + 1] - 255.0), ($Rgba[$i + 2] - 255.0))

        $bestAlpha = 0.0
        $bestResidual = [double]::MaxValue
        foreach ($line in $lines) {
            $v = $line.V
            $alpha = ((($u[0] * $v[0]) + ($u[1] * $v[1]) + ($u[2] * $v[2])) / $line.LenSq)
            $rx = $u[0] - ($alpha * $v[0])
            $ry = $u[1] - ($alpha * $v[1])
            $rz = $u[2] - ($alpha * $v[2])
            $residual = ($rx * $rx) + ($ry * $ry) + ($rz * $rz)
            if ($residual -lt $bestResidual) {
                $bestResidual = $residual
                $bestAlpha = $alpha
            }
        }

        if ($bestAlpha -ge 1.0) {
            $out[$i]     = $Rgba[$i]
            $out[$i + 1] = $Rgba[$i + 1]
            $out[$i + 2] = $Rgba[$i + 2]
            $out[$i + 3] = 255.0
        }
        elseif ($bestAlpha -le 0.004) {
            $out[$i] = 0.0; $out[$i + 1] = 0.0; $out[$i + 2] = 0.0; $out[$i + 3] = 0.0
        }
        else {
            for ($c = 0; $c -lt 3; $c++) {
                $straight = (($Rgba[$i + $c] - (255.0 * (1.0 - $bestAlpha))) / $bestAlpha)
                $out[$i + $c] = [Math]::Max(0.0, [Math]::Min(255.0, $straight))
            }
            $out[$i + 3] = $bestAlpha * 255.0
        }
    }
    return $out
}

function Resize-Rgba {
    <#
        Box filter, averaging in premultiplied alpha so that transparent pixels contribute
        no colour of their own to the pixels they are folded into. Every target size is a
        reduction of the source, which is the case a box filter handles best.
    #>
    param([double[]] $Rgba, [int] $SourceSize, [int] $TargetSize)

    if ($SourceSize -eq $TargetSize) { return $Rgba }

    $scale = [double] $SourceSize / $TargetSize
    $out = New-Object double[] ($TargetSize * $TargetSize * 4)

    for ($ty = 0; $ty -lt $TargetSize; $ty++) {
        $y0 = $ty * $scale
        $y1 = ($ty + 1) * $scale
        for ($tx = 0; $tx -lt $TargetSize; $tx++) {
            $x0 = $tx * $scale
            $x1 = ($tx + 1) * $scale

            $sumR = 0.0; $sumG = 0.0; $sumB = 0.0; $sumA = 0.0; $sumW = 0.0
            for ($sy = [int][Math]::Floor($y0); $sy -lt [Math]::Ceiling($y1); $sy++) {
                $hCover = [Math]::Min($y1, $sy + 1.0) - [Math]::Max($y0, [double] $sy)
                if ($hCover -le 0) { continue }
                for ($sx = [int][Math]::Floor($x0); $sx -lt [Math]::Ceiling($x1); $sx++) {
                    $wCover = [Math]::Min($x1, $sx + 1.0) - [Math]::Max($x0, [double] $sx)
                    if ($wCover -le 0) { continue }

                    $weight = $hCover * $wCover
                    $src = (($sy * $SourceSize) + $sx) * 4
                    $a = $Rgba[$src + 3] / 255.0
                    $sumR += $Rgba[$src] * $a * $weight
                    $sumG += $Rgba[$src + 1] * $a * $weight
                    $sumB += $Rgba[$src + 2] * $a * $weight
                    $sumA += $Rgba[$src + 3] * $weight
                    $sumW += $weight
                }
            }

            $dst = (($ty * $TargetSize) + $tx) * 4
            $alpha = $sumA / $sumW
            if ($alpha -le 0.0) {
                $out[$dst] = 0.0; $out[$dst + 1] = 0.0; $out[$dst + 2] = 0.0; $out[$dst + 3] = 0.0
            }
            else {
                # Back out of premultiplied: the colour sums were weighted by alpha, so
                # dividing by the alpha sum (rather than the coverage sum) restores them.
                $unpremultiply = 255.0 / $sumA
                $out[$dst]     = [Math]::Min(255.0, $sumR * $unpremultiply)
                $out[$dst + 1] = [Math]::Min(255.0, $sumG * $unpremultiply)
                $out[$dst + 2] = [Math]::Min(255.0, $sumB * $unpremultiply)
                $out[$dst + 3] = $alpha
            }
        }
    }
    return $out
}

function Get-DibBytes {
    <# One ICO entry: BITMAPINFOHEADER + bottom-up BGRA pixels + a 1bpp AND mask. #>
    param([double[]] $Rgba, [int] $Size)

    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $stream
    try {
        # BITMAPINFOHEADER. Height is doubled: the DIB covers image plus mask.
        $writer.Write([uint32] 40)              # biSize
        $writer.Write([int32] $Size)            # biWidth
        $writer.Write([int32] ($Size * 2))      # biHeight
        $writer.Write([uint16] 1)               # biPlanes
        $writer.Write([uint16] 32)              # biBitCount
        $writer.Write([uint32] 0)               # biCompression = BI_RGB
        $writer.Write([uint32] ($Size * $Size * 4))
        $writer.Write([int32] 0)                # biXPelsPerMeter
        $writer.Write([int32] 0)                # biYPelsPerMeter
        $writer.Write([uint32] 0)               # biClrUsed
        $writer.Write([uint32] 0)               # biClrImportant

        # Colour data, bottom row first.
        for ($y = $Size - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $Size; $x++) {
                $src = (($y * $Size) + $x) * 4
                $writer.Write([byte] [Math]::Round($Rgba[$src + 2]))   # B
                $writer.Write([byte] [Math]::Round($Rgba[$src + 1]))   # G
                $writer.Write([byte] [Math]::Round($Rgba[$src]))       # R
                $writer.Write([byte] [Math]::Round($Rgba[$src + 3]))   # A
            }
        }

        # AND mask: all zeros (fully opaque). The 32-bit alpha channel does the real work,
        # but the mask must still be present and 4-byte aligned per row.
        $maskStride = [int](([Math]::Floor(($Size + 31) / 32)) * 4)
        $maskRow = New-Object byte[] $maskStride
        for ($y = 0; $y -lt $Size; $y++) {
            $writer.Write($maskRow, 0, $maskStride)
        }

        $writer.Flush()
        return $stream.ToArray()
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

if (-not (Test-Path -LiteralPath $SourcePath)) {
    throw "Source artwork not found: $SourcePath"
}

$source = [System.Drawing.Bitmap]::FromFile($SourcePath)
try {
    if ($source.Width -ne $source.Height) {
        throw "Source artwork must be square; $SourcePath is $($source.Width)x$($source.Height)."
    }
    $sourceSize = $source.Width
    $largest = ($sizes | Measure-Object -Maximum).Maximum
    if ($sourceSize -lt $largest) {
        throw "Source artwork is ${sourceSize}px; at least ${largest}px is needed for the largest icon entry."
    }
    $sourceRgba = Read-Rgba -Bitmap $source
}
finally {
    $source.Dispose()
}

$inks = Get-InkColors -Rgba $sourceRgba
$keyed = Remove-White -Rgba $sourceRgba -Inks $inks

$entries = @()
foreach ($size in $sizes) {
    $scaled = Resize-Rgba -Rgba $keyed -SourceSize $sourceSize -TargetSize $size
    $entries += [pscustomobject]@{ Size = $size; Bytes = (Get-DibBytes -Rgba $scaled -Size $size) }
}

$outDir = Split-Path -Parent $OutputPath
if (-not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}

$file = [System.IO.File]::Create($OutputPath)
$writer = New-Object System.IO.BinaryWriter $file
try {
    # ICONDIR
    $writer.Write([uint16] 0)                  # reserved
    $writer.Write([uint16] 1)                  # type: icon
    $writer.Write([uint16] $entries.Count)

    # ICONDIRENTRY table. Offsets follow the 6-byte header and 16 bytes per entry.
    $offset = 6 + (16 * $entries.Count)
    foreach ($entry in $entries) {
        # A dimension of 256 is encoded as 0.
        $dim = if ($entry.Size -ge 256) { 0 } else { $entry.Size }
        $writer.Write([byte] $dim)             # width
        $writer.Write([byte] $dim)             # height
        $writer.Write([byte] 0)                # palette entries
        $writer.Write([byte] 0)                # reserved
        $writer.Write([uint16] 1)              # colour planes
        $writer.Write([uint16] 32)             # bits per pixel
        $writer.Write([uint32] $entry.Bytes.Length)
        $writer.Write([uint32] $offset)
        $offset += $entry.Bytes.Length
    }

    foreach ($entry in $entries) {
        $writer.Write($entry.Bytes, 0, $entry.Bytes.Length)
    }
}
finally {
    $writer.Dispose()
    $file.Dispose()
}

Write-Host ("Wrote {0} ({1} sizes, {2} inks, {3} bytes)." -f `
    $OutputPath, $entries.Count, $inks.Count, (Get-Item $OutputPath).Length)
