<#
.SYNOPSIS
    Regenerates assets\SleepPicker.ico.

.DESCRIPTION
    The target machines (Windows IoT Enterprise LTSC) have no image editor and no package
    manager, so the icon is generated from code with System.Drawing, which ships with the
    in-box .NET Framework. The result is committed so a normal build needs nothing.

    Entries are written as classic 32-bit BGRA DIBs rather than PNG-compressed entries,
    because System.Drawing.Icon does not reliably decode PNG entries.

    Run with Windows PowerShell 5.1:
        powershell.exe -ExecutionPolicy Bypass -File tools\MakeIcon.ps1
#>
[CmdletBinding()]
param(
    [string] $OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $OutputPath) {
    # $PSScriptRoot is not bound yet when parameter defaults are evaluated, so the
    # repo-relative default is resolved here instead.
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $OutputPath = Join-Path $repoRoot 'assets\SleepPicker.ico'
}

Add-Type -AssemblyName System.Drawing

# Up to 64 only. A 256px entry would have to be stored uncompressed here (256 KB on its
# own) because PNG-compressed entries are the part System.Drawing.Icon handles badly, and
# a tray utility never renders larger than the jumbo shell icon anyway.
$sizes = @(16, 20, 24, 32, 48, 64)

function New-Glyph {
    <#
        A crescent moon: a filled disc with a second disc punched out of it, using an
        alternate fill mode so the overlap becomes a hole.

        Filled gold rather than the usual monochrome white: the notification area may be
        dark or light depending on the user's theme, and a white glyph vanishes on a light
        taskbar. Gold with a dark rim stays legible on both.
    #>
    param([int] $Size)

    $bitmap = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.Clear([System.Drawing.Color]::Transparent)

        $s = [double] $Size

        # Outer disc, then the bite taken out of it, as a single even-odd-free path built
        # by drawing the moon and then erasing with a transparent-copy composite.
        $moon = New-Object System.Drawing.Drawing2D.GraphicsPath
        $moon.AddEllipse([single](0.08 * $s), [single](0.08 * $s), [single](0.84 * $s), [single](0.84 * $s))

        $bite = New-Object System.Drawing.Drawing2D.GraphicsPath
        $bite.AddEllipse([single](0.32 * $s), [single](-0.06 * $s), [single](0.82 * $s), [single](0.82 * $s))

        $crescent = New-Object System.Drawing.Drawing2D.GraphicsPath
        $crescent.FillMode = [System.Drawing.Drawing2D.FillMode]::Alternate
        $crescent.AddPath($moon, $false)
        $crescent.AddPath($bite, $false)

        $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 247, 201, 72))
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(220, 66, 52, 16)), ([single]([Math]::Max(1.0, $s / 24.0)))
        try {
            $g.FillPath($brush, $crescent)
            $g.DrawPath($pen, $crescent)
        }
        finally {
            $brush.Dispose()
            $pen.Dispose()
        }

        $crescent.Dispose()
        $bite.Dispose()
        $moon.Dispose()
    }
    finally {
        $g.Dispose()
    }
    return $bitmap
}

function Get-DibBytes {
    <# One ICO entry: BITMAPINFOHEADER + bottom-up BGRA pixels + a 1bpp AND mask. #>
    param([System.Drawing.Bitmap] $Bitmap)

    $w = $Bitmap.Width
    $h = $Bitmap.Height

    $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
    $data = $Bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                             [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $pixels = New-Object byte[] ($data.Stride * $h)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    }
    finally {
        $Bitmap.UnlockBits($data)
    }

    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $stream
    try {
        # BITMAPINFOHEADER. Height is doubled: the DIB covers image plus mask.
        $writer.Write([uint32] 40)          # biSize
        $writer.Write([int32] $w)           # biWidth
        $writer.Write([int32] ($h * 2))     # biHeight
        $writer.Write([uint16] 1)           # biPlanes
        $writer.Write([uint16] 32)          # biBitCount
        $writer.Write([uint32] 0)           # biCompression = BI_RGB
        $writer.Write([uint32] ($w * $h * 4))
        $writer.Write([int32] 0)            # biXPelsPerMeter
        $writer.Write([int32] 0)            # biYPelsPerMeter
        $writer.Write([uint32] 0)           # biClrUsed
        $writer.Write([uint32] 0)           # biClrImportant

        # Colour data, bottom row first.
        for ($y = $h - 1; $y -ge 0; $y--) {
            $writer.Write($pixels, $y * $data.Stride, $w * 4)
        }

        # AND mask: all zeros (fully opaque). The 32-bit alpha channel does the real work,
        # but the mask must still be present and 4-byte aligned per row.
        $maskStride = [int](([Math]::Floor(($w + 31) / 32)) * 4)
        $maskRow = New-Object byte[] $maskStride
        for ($y = 0; $y -lt $h; $y++) {
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

$entries = @()
foreach ($size in $sizes) {
    $bitmap = New-Glyph -Size $size
    try {
        $entries += [pscustomobject]@{ Size = $size; Bytes = (Get-DibBytes -Bitmap $bitmap) }
    }
    finally {
        $bitmap.Dispose()
    }
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

Write-Host ("Wrote {0} ({1} sizes, {2} bytes)." -f $OutputPath, $entries.Count, (Get-Item $OutputPath).Length)
