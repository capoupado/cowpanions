<#
.SYNOPSIS
  Builds the pug sprite sheets in the cow sheet layout (128x256, 4 columns x 8 rows of 32x32 frames).

.DESCRIPTION
  The pug art is original pixel art drawn by this script from the pixel maps below (no online source
  with a redistributable licence was available when the pugs were added; see assets/pug/SOURCE.md).
  Every frame is composed from a few layers (torso, legs, head, tail) placed at small offsets, so the
  frame map at the top of the script is the only thing to touch to change a pose or an animation.

  Layout (matches client/assets/sprites/cow/manifest.json):
    row 0 idle (3)   row 1 graze (2)   row 2 idle2 (4)   row 3 moo/bark (4)
    row 4 walk (4)   row 5 lie/sleep (frame 0 used)      rows 6-7 empty
  Art faces right (head on the right), feet on the bottom pixel row of each frame (bottom-center anchor).

  Writes one sheet per palette to -OutDir (the working copy, client/assets/sprites/cow) and, when
  -OriginalsDir is given, the same files to assets/pug (the "originals, do not edit" folder).
  Runs in Windows PowerShell 5.1 and PowerShell 7 (System.Drawing; no ?. or ?? operators).

.EXAMPLE
  cd client; .\tools\Normalize-PugSheet.ps1
  cd client; .\tools\Normalize-PugSheet.ps1 -PreviewPath $env:TEMP\pugs.png -PreviewScale 4
#>
param(
    [string]$OutDir = (Join-Path $PSScriptRoot "..\assets\sprites\cow"),
    [string]$OriginalsDir = (Join-Path $PSScriptRoot "..\..\assets\pug"),
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

# ---------------------------------------------------------------------------------------------------
# Palettes: variant name -> colour per map letter. Letters not listed here are transparent.
#   O outline   B body   S body shade   L belly/light   M mask (face, ears)   E eye   W eye shine
#   N nose   T tongue
# ---------------------------------------------------------------------------------------------------
$Palettes = [ordered]@{
    pug_fawn = @{
        O = "#2A211D"; B = "#D7B07A"; S = "#B38A55"; L = "#EFD9AD"; M = "#2A211D"; E = "#0B0806"; W = "#FFFFFF"; N = "#0B0806"; T = "#E0778E"
    }
    pug_black = @{
        O = "#0F0B0A"; B = "#3E3532"; S = "#2A2321"; L = "#4F4542"; M = "#1D1715"; E = "#000000"; W = "#FFFFFF"; N = "#000000"; T = "#D96F86"
    }
    pug_apricot = @{
        O = "#2A211D"; B = "#E7AE7B"; S = "#C27F4F"; L = "#F7DCBE"; M = "#2A211D"; E = "#0B0806"; W = "#FFFFFF"; N = "#0B0806"; T = "#E0778E"
    }
    pug_silver = @{
        O = "#1F1D22"; B = "#B6B4BC"; S = "#86848D"; L = "#DBD9E0"; M = "#2A2630"; E = "#000000"; W = "#FFFFFF"; N = "#000000"; T = "#E0778E"
    }
}

# ---------------------------------------------------------------------------------------------------
# Layers. Each is a list of equal-width strings; '.' is transparent. Origins below are frame pixels.
# ---------------------------------------------------------------------------------------------------
$Torso = @(
    "...OOOOOOOOOO....",
    "..OBBBBBBBBBBBO..",
    ".OBBBBBBBBBBBBBO.",
    "OBBBBBBBBBBBBBBBO",
    "OBBBBBBBBBBBBBBBO",
    "OBBLLLLLLLLBBBBBO",
    "OBBLLLLLLLLBBBBBO",
    ".OBBLLLLLLBBBBBO.",
    "..OBBBBBBBBBBBO.."
)
$TorsoX = 4; $TorsoY = 18    # rows 18..26

# Head, 3/4 view looking right: big floppy ear on the left, dark mask with one eye and the nose on the right.
$Head = @(
    "....OOOOOO..",
    "..OOBBBBBBO.",
    ".OMMBBBBBBBO",
    "OMMMBBBBBBBO",
    "OMMMBBBBMMMO",
    "OMMBBBBMMMMO",
    ".OBBBBMWEMMO",
    ".OBBBMMMMMNO",
    ".OBBBMMMMMMO",
    "..OBBMMMMMO.",
    "...OOOOOOO.."
)
$HeadOpen = @(
    "....OOOOOO..",
    "..OOBBBBBBO.",
    ".OMMBBBBBBBO",
    "OMMMBBBBBBBO",
    "OMMMBBBBMMMO",
    "OMMBBBBMMMMO",
    ".OBBBBMWEMMO",
    ".OBBBMMMMMNO",
    ".OBBBMMMOOO.",
    "..OBBMMOTTO.",
    "...OOOOOOO.."
)
$HeadX = 15; $HeadY = 14     # rows 14..24, overlaps the torso's right end

$Tail = @(
    ".OOO.",
    "OSSSO",
    "OBOSO",
    ".OBO.",
    "..O.."
)
$TailX = 4; $TailY = 14      # curl sits on the back at the left

# One leg: three wide, five tall, dark paw on the bottom row. Far legs use the shade colour.
$LegNear = @("OBO", "OBO", "OBO", "OBO", "OOO")
$LegFar  = @("OSO", "OSO", "OSO", "OSO", "OOO")
$LegY = 27                   # rows 27..31, feet on the last frame row
# Leg x positions when standing: far back, near back, far front, near front.
$LegStand = @{ FarBack = 6; NearBack = 9; FarFront = 14; NearFront = 17 }

# Lying pose (row 5): torso dropped onto the ground, head resting lower, paws stretched forward.
$Paws = @("OBBBO", "OOOOO")

# ---------------------------------------------------------------------------------------------------
# Frame map: one entry per manifest row, up to 4 poses. Missing columns repeat the last pose.
#   Legs: "stand" | "lie" | a hashtable of per-leg dx (walk)      Body: dy of torso+tail+legs
#   Head: "normal" | "open"   HeadDx/HeadDy: head offset   TailDx: wag offset
# ---------------------------------------------------------------------------------------------------
function Pose([string]$legs = "stand", [int]$bodyDy = 0, [string]$head = "normal", [int]$headDx = 0, [int]$headDy = 0, [int]$tailDx = 0, $walk = $null) {
    return @{ Legs = $legs; BodyDy = $bodyDy; Head = $head; HeadDx = $headDx; HeadDy = $headDy; TailDx = $tailDx; Walk = $walk }
}
$FrameMap = @(
    # row 0 idle: breathe (head bob) and a tail wag
    @( (Pose), (Pose -headDy 1), (Pose -tailDx 1) ),
    # row 1 graze: nose to the ground (sniffing)
    @( (Pose -headDx 1 -headDy 4), (Pose -headDx 2 -headDy 5) ),
    # row 2 idle2: look up and wag
    @( (Pose -headDy -1), (Pose -headDy -1 -tailDx 1), (Pose -headDy -2), (Pose -headDy -1 -tailDx 1) ),
    # row 3 moo = bark: mouth open on frames 2-3, head thrown back a little
    @( (Pose), (Pose -head "open"), (Pose -head "open" -headDy -1 -tailDx 1), (Pose) ),
    # row 4 walk: legs alternate, body bobs one pixel
    @(
        (Pose -walk @{ FarBack = -1; NearBack = 1; FarFront = 1; NearFront = -1 }),
        (Pose -bodyDy -1 -walk @{ FarBack = 0; NearBack = 0; FarFront = 0; NearFront = 0 }),
        (Pose -walk @{ FarBack = 1; NearBack = -1; FarFront = -1; NearFront = 1 }),
        (Pose -bodyDy -1 -walk @{ FarBack = 0; NearBack = 0; FarFront = 0; NearFront = 0 })
    ),
    # row 5 lie / sleep: frame 0 is the one the app shows; the rest repeat it
    @( (Pose -legs "lie") ),
    @(),
    @()
)

# ---------------------------------------------------------------------------------------------------
function Draw-Layer($bmp, [string[]]$layer, [int]$x0, [int]$y0, [hashtable]$pal, [int]$frameX, [int]$frameY) {
    for ($r = 0; $r -lt $layer.Count; $r++) {
        $line = $layer[$r]
        for ($c = 0; $c -lt $line.Length; $c++) {
            $ch = [string]$line[$c]
            if ($ch -eq ".") { continue }
            if (-not $pal.ContainsKey($ch)) { throw "palette has no colour for '$ch'" }
            $x = $x0 + $c; $y = $y0 + $r
            if ($x -lt 0 -or $y -lt 0 -or $x -ge $FrameW -or $y -ge $FrameH) { throw "pixel ($x,$y) outside the ${FrameW}x${FrameH} frame" }
            $bmp.SetPixel($frameX + $x, $frameY + $y, [System.Drawing.ColorTranslator]::FromHtml($pal[$ch]))
        }
    }
}

function Draw-Pose($bmp, [hashtable]$pose, [hashtable]$pal, [int]$frameX, [int]$frameY) {
    $dy = $pose.BodyDy
    if ($pose.Legs -eq "lie") {
        # Everything drops so the belly is on the ground; no legs, paws in front of the chest.
        $lieDy = 5
        Draw-Layer $bmp $Tail  ($TailX) ($TailY + $lieDy) $pal $frameX $frameY
        Draw-Layer $bmp $Torso ($TorsoX) ($TorsoY + $lieDy) $pal $frameX $frameY
        Draw-Layer $bmp $Paws  ($TorsoX + 12) ($FrameH - 2) $pal $frameX $frameY
        Draw-Layer $bmp $Paws  ($TorsoX + 2) ($FrameH - 2) $pal $frameX $frameY
        Draw-Layer $bmp $Head  ($HeadX + 1) ($HeadY + $lieDy + 2) $pal $frameX $frameY
        return
    }
    $legDx = @{ FarBack = 0; NearBack = 0; FarFront = 0; NearFront = 0 }
    if ($pose.Walk -ne $null) { $legDx = $pose.Walk }
    # Far legs first, then tail and torso, then near legs and the head on top.
    # Feet always stay on the ground line: when the body bobs up (dy < 0) the legs are drawn once lifted with
    # the torso and once at rest, so they stretch instead of floating.
    $legYs = @($LegY)
    if ($dy -lt 0) { $legYs = @(($LegY + $dy), $LegY) }
    foreach ($ly in $legYs) {
        Draw-Layer $bmp $LegFar ($LegStand.FarBack + $legDx.FarBack)   $ly $pal $frameX $frameY
        Draw-Layer $bmp $LegFar ($LegStand.FarFront + $legDx.FarFront) $ly $pal $frameX $frameY
    }
    Draw-Layer $bmp $Tail  ($TailX + $pose.TailDx) ($TailY + $dy) $pal $frameX $frameY
    Draw-Layer $bmp $Torso $TorsoX ($TorsoY + $dy) $pal $frameX $frameY
    foreach ($ly in $legYs) {
        Draw-Layer $bmp $LegNear ($LegStand.NearBack + $legDx.NearBack)   $ly $pal $frameX $frameY
        Draw-Layer $bmp $LegNear ($LegStand.NearFront + $legDx.NearFront) $ly $pal $frameX $frameY
    }
    $headLayer = $Head
    if ($pose.Head -eq "open") { $headLayer = $HeadOpen }
    Draw-Layer $bmp $headLayer ($HeadX + $pose.HeadDx) ($HeadY + $pose.HeadDy + $dy) $pal $frameX $frameY
}

function New-Sheet([hashtable]$pal) {
    $bmp = New-Object System.Drawing.Bitmap ($FrameW * $Cols), ($FrameH * $Rows), ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    for ($row = 0; $row -lt $Rows; $row++) {
        $poses = $FrameMap[$row]
        if ($poses.Count -eq 0) { continue }
        for ($col = 0; $col -lt $Cols; $col++) {
            $pose = $poses[[Math]::Min($col, $poses.Count - 1)]
            Draw-Pose $bmp $pose $pal ($col * $FrameW) ($row * $FrameH)
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

$sheets = @()
foreach ($name in $Palettes.Keys) {
    $file = "pugs_spritesheet_" + $name.Substring(4) + ".png"
    $bmp = New-Sheet $Palettes[$name]
    Save-Png $bmp (Join-Path $OutDir $file)
    if ($OriginalsDir) { Save-Png $bmp (Join-Path $OriginalsDir $file) }
    if ($SiteDir) { Save-Png $bmp (Join-Path $SiteDir ($name + ".png")) }
    $sheets += $bmp
}

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
