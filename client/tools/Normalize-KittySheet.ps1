<#
.SYNOPSIS
  Builds the kitty sprite sheets in the cow sheet layout (128x256, 4 columns x 8 rows of 32x32 frames).

.DESCRIPTION
  Source: "Just a Kitty" by bimlab (https://bimlab.itch.io/just-a-kitty), assets/kitty/kitty_1.png,
  supplied by the owner. That sheet is 128x128, four rows of four 32x32 cells:
    row 0 front view (walking toward the camera)   row 1 side view facing left (walk)
    row 2 side view facing right (walk)            row 3 back view (walking away)
  It has no idle, sit or meow frames, so the standing walk frames stand in for them. See assets/kitty/SOURCE.md.

  Every variant is a palette swap of that one sheet. Each opaque source colour gets a role:
    O outline   E eye   P nose/cheek pink   F fur
  Outline, eye and pink are exact colours. Fur is every other low-saturation colour (the sheet anti-aliases
  its fur into ~50 tones); it is recoloured along a dark -> mid -> light ramp by its luminance, so the
  shading survives the swap. Any other colour throws, so a changed source fails loudly.

  Layout (matches client/assets/sprites/cow/manifest.json):
    row 0 idle (3)   row 1 graze (2)   row 2 idle2 (4)   row 3 moo (4)
    row 4 walk (4)   row 5 lie/sleep (frame 0 used; the front view)   rows 6-7 empty
  Art faces right (head on the right), feet on the bottom pixel row of each frame (bottom-center anchor).

  Writes one sheet per palette to -OutDir (the working copy, client/assets/sprites/cow) and, when
  -OriginalsDir is given, the same files to assets/kitty (the "originals, do not edit" folder).
  Runs in Windows PowerShell 5.1 and PowerShell 7 (System.Drawing; no ?. or ?? operators).

.EXAMPLE
  cd client; .\tools\Normalize-KittySheet.ps1
  cd client; .\tools\Normalize-KittySheet.ps1 -PreviewPath $env:TEMP\kitties.png -PreviewScale 4
#>
param(
    [string]$SourcePath = (Join-Path $PSScriptRoot "..\..\assets\kitty\kitty_1.png"),
    [string]$OutDir = (Join-Path $PSScriptRoot "..\assets\sprites\cow"),
    [string]$OriginalsDir = (Join-Path $PSScriptRoot "..\..\assets\kitty"),
    [string]$SiteDir = (Join-Path $PSScriptRoot "..\..\site\assets"),
    # Optional: write one scaled PNG with every sheet side by side, to eyeball the result.
    [string]$PreviewPath = "",
    [int]$PreviewScale = 3
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$FrameW = 32
$FrameH = 32
$Cols = 4
$Rows = 8

$SourceFrameW = 32
$SourceFrameH = 32

# Largest width a kitty may take in a frame (about a pug); decides the integer scale.
$MaxKittyW = 24

# ---------------------------------------------------------------------------------------------------
# Colour roles: exact source colour (RRGGBB) -> role. Everything else must be fur (see Get-Role).
# ---------------------------------------------------------------------------------------------------
$ColourRoles = @{
    "463239" = "O"
    "4B8271" = "E"
    "FFC2D7" = "P"
    "FABBD0" = "P"
}
# Fur tones are greyish pink; anything more saturated than this is not fur.
$FurMaxChroma = 0x30

# ---------------------------------------------------------------------------------------------------
# Palettes: variant name -> colour per role. Fur uses three stops: D (darkest source fur), M (mid), L (lightest).
# ---------------------------------------------------------------------------------------------------
$Palettes = [ordered]@{
    kitty_orange = @{ O = "#3B2016"; E = "#4B8271"; P = "#FFB3C6"; D = "#A8501E"; M = "#E8893A"; L = "#FFE2BF" }
    kitty_black  = @{ O = "#0C0A0E"; E = "#C8D84A"; P = "#E68AA6"; D = "#1A171D"; M = "#2F2B33"; L = "#4E4955" }
    kitty_white  = @{ O = "#5E5A63"; E = "#5D9AD6"; P = "#FFB3C6"; D = "#B9B6BF"; M = "#E9E7EC"; L = "#FFFFFF" }
    kitty_grey   = @{ O = "#2A2C35"; E = "#D9B23A"; P = "#F2A7BC"; D = "#5D6676"; M = "#8B95A6"; L = "#C4CBD6" }
}
$MidStop = 0.6   # where on the source fur luminance range the M stop sits

# ---------------------------------------------------------------------------------------------------
# Frame map: one entry per manifest row, up to 4 source cells as @(sourceRow, sourceCol).
# Missing columns repeat the last cell. Columns 0 and 2 of source row 2 are the standing poses.
# ---------------------------------------------------------------------------------------------------
$FrameMap = @(
    # row 0 idle
    @( @(2, 0), @(2, 2), @(2, 0) ),
    # row 1 graze (no head-down frame in the source)
    @( @(2, 0), @(2, 2) ),
    # row 2 idle2
    @( @(2, 0), @(2, 0), @(2, 2), @(2, 2) ),
    # row 3 moo (no meow frame in the source)
    @( @(2, 0), @(2, 2), @(2, 2), @(2, 0) ),
    # row 4 walk
    @( @(2, 0), @(2, 1), @(2, 2), @(2, 3) ),
    # row 5 lie / sleep: front view; frame 0 is the one the app shows
    @( @(0, 0), @(0, 1), @(0, 2), @(0, 3) ),
    # row 6 walk-away: left empty (unused by the app; the back view's tail hangs below its feet)
    @(),
    @()
)

# ---------------------------------------------------------------------------------------------------
function Get-Hex($c) { return ("{0:X2}{1:X2}{2:X2}" -f $c.R, $c.G, $c.B) }

function Get-Luma($c) { return 0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B }

function Get-Role($c) {
    $hex = Get-Hex $c
    if ($ColourRoles.ContainsKey($hex)) { return $ColourRoles[$hex] }
    $chroma = [Math]::Max($c.R, [Math]::Max($c.G, $c.B)) - [Math]::Min($c.R, [Math]::Min($c.G, $c.B))
    if ($chroma -le $FurMaxChroma) { return "F" }
    throw "source colour #$hex has no role; add it to `$ColourRoles"
}

function Mix($a, $b, [double]$t) {
    return [System.Drawing.Color]::FromArgb(255,
        [int][Math]::Round($a.R + ($b.R - $a.R) * $t),
        [int][Math]::Round($a.G + ($b.G - $a.G) * $t),
        [int][Math]::Round($a.B + ($b.B - $a.B) * $t))
}

function Get-Colour($src, [hashtable]$pal) {
    $role = Get-Role $src
    if ($role -ne "F") { return [System.Drawing.ColorTranslator]::FromHtml($pal[$role]) }
    $t = ((Get-Luma $src) - $script:FurLumaMin) / ($script:FurLumaMax - $script:FurLumaMin)
    $d = [System.Drawing.ColorTranslator]::FromHtml($pal.D)
    $m = [System.Drawing.ColorTranslator]::FromHtml($pal.M)
    $l = [System.Drawing.ColorTranslator]::FromHtml($pal.L)
    if ($t -le $MidStop) { return Mix $d $m ($t / $MidStop) }
    return Mix $m $l (($t - $MidStop) / (1 - $MidStop))
}

# Opaque-pixel bounding box of a set of source cells, in cell coordinates.
function Get-UnionBox($cells) {
    $box = @{ X0 = $SourceFrameW; Y0 = $SourceFrameH; X1 = -1; Y1 = -1 }
    foreach ($cell in $cells) {
        for ($y = 0; $y -lt $SourceFrameH; $y++) {
            for ($x = 0; $x -lt $SourceFrameW; $x++) {
                if ($src.GetPixel($cell[1] * $SourceFrameW + $x, $cell[0] * $SourceFrameH + $y).A -eq 0) { continue }
                if ($x -lt $box.X0) { $box.X0 = $x }
                if ($y -lt $box.Y0) { $box.Y0 = $y }
                if ($x -gt $box.X1) { $box.X1 = $x }
                if ($y -gt $box.Y1) { $box.Y1 = $y }
            }
        }
    }
    if ($box.X1 -lt 0) { throw "source cells are empty" }
    return $box
}

function Draw-Cell($bmp, $cell, [hashtable]$pal, [int]$frameX, [int]$frameY) {
    $off = $Offsets[[int]$cell[0]]
    for ($y = 0; $y -lt $SourceFrameH; $y++) {
        for ($x = 0; $x -lt $SourceFrameW; $x++) {
            $c = $src.GetPixel($cell[1] * $SourceFrameW + $x, $cell[0] * $SourceFrameH + $y)
            if ($c.A -eq 0) { continue }
            $colour = Get-Colour $c $pal
            $colour = [System.Drawing.Color]::FromArgb($c.A, $colour.R, $colour.G, $colour.B)
            for ($dy = 0; $dy -lt $Scale; $dy++) {
                for ($dx = 0; $dx -lt $Scale; $dx++) {
                    $tx = $off.X + $x * $Scale + $dx; $ty = $off.Y + $y * $Scale + $dy
                    if ($tx -lt 0 -or $ty -lt 0 -or $tx -ge $FrameW -or $ty -ge $FrameH) { throw "pixel ($tx,$ty) outside the ${FrameW}x${FrameH} frame" }
                    $bmp.SetPixel($frameX + $tx, $frameY + $ty, $colour)
                }
            }
        }
    }
}

function New-Sheet([hashtable]$pal) {
    $bmp = New-Object System.Drawing.Bitmap ($FrameW * $Cols), ($FrameH * $Rows), ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    for ($row = 0; $row -lt $Rows; $row++) {
        $cells = $FrameMap[$row]
        if ($cells.Count -eq 0) { continue }
        for ($col = 0; $col -lt $Cols; $col++) {
            $cell = $cells[[Math]::Min($col, $cells.Count - 1)]
            Draw-Cell $bmp $cell $pal ($col * $FrameW) ($row * $FrameH)
        }
    }
    return $bmp
}

function Save-Png($bmp, [string]$path) {
    $dir = Split-Path -Parent $path
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host ("wrote {0} ({1}x{2})" -f $path, $bmp.Width, $bmp.Height)
}

# ---------------------------------------------------------------------------------------------------
$src = New-Object System.Drawing.Bitmap (Resolve-Path $SourcePath).Path
if (($src.Width % $SourceFrameW) -ne 0 -or ($src.Height % $SourceFrameH) -ne 0) {
    throw ("source is {0}x{1}, not a multiple of {2}x{3} cells" -f $src.Width, $src.Height, $SourceFrameW, $SourceFrameH)
}

# Classify every source colour up front (throws on an unknown one) and find the fur luminance range.
$FurLumaMin = 255.0; $FurLumaMax = 0.0
for ($y = 0; $y -lt $src.Height; $y++) {
    for ($x = 0; $x -lt $src.Width; $x++) {
        $c = $src.GetPixel($x, $y)
        if ($c.A -eq 0) { continue }
        if ((Get-Role $c) -ne "F") { continue }
        $l = Get-Luma $c
        if ($l -lt $FurLumaMin) { $FurLumaMin = $l }
        if ($l -gt $FurLumaMax) { $FurLumaMax = $l }
    }
}

# Every source row used gets one fixed offset, from the union box of all its mapped cells: the row's own motion
# is kept, its lowest pixel (the feet) lands on the bottom frame row, and it is centred horizontally.
$cellsByRow = @{}
foreach ($cells in $FrameMap) { foreach ($cell in $cells) {
    $r = [int]$cell[0]
    if (-not $cellsByRow.ContainsKey($r)) { $cellsByRow[$r] = @() }
    $cellsByRow[$r] += , @($r, [int]$cell[1])
} }
$boxes = @{}
foreach ($r in $cellsByRow.Keys) { $boxes[$r] = Get-UnionBox $cellsByRow[$r] }

$Scale = 1
while ($true) {
    $next = $Scale + 1
    $fits = $true
    foreach ($box in $boxes.Values) {
        $w = ($box.X1 - $box.X0 + 1) * $next; $h = ($box.Y1 - $box.Y0 + 1) * $next
        if ($w -gt $MaxKittyW -or $w -gt $FrameW -or $h -gt $FrameH) { $fits = $false }
    }
    if (-not $fits) { break }
    $Scale = $next
}
$Offsets = @{}
foreach ($r in $boxes.Keys) {
    $box = $boxes[$r]
    $w = ($box.X1 - $box.X0 + 1) * $Scale
    $Offsets[$r] = @{
        X = [int][Math]::Floor(($FrameW - $w) / 2) - $box.X0 * $Scale
        Y = ($FrameH - 1) - ($box.Y1 * $Scale + $Scale - 1)
    }
    Write-Host ("source row {0}: box x {1}..{2} y {3}..{4}, scale {5}, offset ({6},{7})" -f $r, $box.X0, $box.X1, $box.Y0, $box.Y1, $Scale, $Offsets[$r].X, $Offsets[$r].Y)
}

$sheets = @()
foreach ($name in $Palettes.Keys) {
    $file = "kittys_spritesheet_" + $name.Substring(6) + ".png"
    $bmp = New-Sheet $Palettes[$name]
    Save-Png $bmp (Join-Path $OutDir $file)
    if ($OriginalsDir) { Save-Png $bmp (Join-Path $OriginalsDir $file) }
    if ($SiteDir) { Save-Png $bmp (Join-Path $SiteDir ($name + ".png")) }
    $sheets += $bmp
}
$src.Dispose()

if ($PreviewPath) {
    $gap = 8
    $w = ($sheets.Count * ($FrameW * $Cols + $gap)) * $PreviewScale
    $h = ($FrameH * $Rows) * $PreviewScale
    $preview = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($preview)
    $g.Clear([System.Drawing.Color]::FromArgb(255, 120, 170, 110))
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
    for ($i = 0; $i -lt $sheets.Count; $i++) {
        $x = $i * ($FrameW * $Cols + $gap) * $PreviewScale
        $g.DrawImage($sheets[$i], $x, 0, $FrameW * $Cols * $PreviewScale, $FrameH * $Rows * $PreviewScale)
    }
    $g.Dispose()
    Save-Png $preview $PreviewPath
    $preview.Dispose()
}
foreach ($bmp in $sheets) { $bmp.Dispose() }
