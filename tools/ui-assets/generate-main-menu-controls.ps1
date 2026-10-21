param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $PSScriptRoot '..\..\assets\ui\main-menu'
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

function New-RoundedPath([float]$X, [float]$Y, [float]$Width, [float]$Height, [float]$Radius) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $Radius * 2
    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return ,$path
}

function Write-Panel([string]$Name, [string]$Top, [string]$Bottom, [bool]$Overlay) {
    $bitmap = [System.Drawing.Bitmap]::new(96, 96, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $path = New-RoundedPath 2 2 91 91 14
    $inner = New-RoundedPath 7 7 81 81 10
    $gold = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#E6BC73'), 2)
    $fine = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(130, 255, 229, 177), 1)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        if (-not $Overlay) {
            $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
                [System.Drawing.Rectangle]::new(0, 0, 96, 96),
                [System.Drawing.ColorTranslator]::FromHtml($Top),
                [System.Drawing.ColorTranslator]::FromHtml($Bottom), 90.0)
            try { $graphics.FillPath($brush, $path) } finally { $brush.Dispose() }
        }
        $graphics.DrawPath($gold, $path)
        $graphics.DrawPath($fine, $inner)
        $graphics.DrawLine($fine, 18, 12, 77, 12)
        $graphics.DrawLine($fine, 18, 83, 77, 83)
        $bitmap.Save((Join-Path $OutputDirectory $Name), [System.Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $gold.Dispose(); $fine.Dispose(); $path.Dispose(); $inner.Dispose()
        $graphics.Dispose(); $bitmap.Dispose()
    }
}

Write-Panel 'button-primary.png' '#B43B2D' '#642019' $false
Write-Panel 'button-secondary.png' '#194F42' '#0A2622' $false
Write-Panel 'button-focus.png' '' '' $true

# Six independently addressable 64x64 cells: solo / online / skin / secrets / settings / exit.
$icons = [System.Drawing.Bitmap]::new(384, 64, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$draw = [System.Drawing.Graphics]::FromImage($icons)
$draw.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$pen = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#F4D590'), 3)
$pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$fill = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#F4D590'))
try {
    $draw.Clear([System.Drawing.Color]::Transparent)
    for ($cell = 0; $cell -lt 6; $cell++) {
        $saved = $draw.Save()
        try {
            $draw.TranslateTransform(($cell * 64), 0)
            switch ($cell) {
                0 {
                    $p = New-RoundedPath 16 10 32 44 5
                    try { $draw.DrawPath($pen, $p) } finally { $p.Dispose() }
                    foreach ($dot in @(22,32,42)) { $draw.FillEllipse($fill, 27, $dot - 4, 9, 9) }
                }
                1 {
                    $draw.DrawLine($pen, 18, 18, 46, 18); $draw.DrawLine($pen, 18, 46, 46, 46)
                    $draw.DrawLine($pen, 18, 18, 18, 46); $draw.DrawLine($pen, 46, 18, 46, 46)
                    foreach ($position in @(@(11,11),@(39,11),@(11,39),@(39,39))) {
                        $draw.FillEllipse($fill, [int]$position[0], [int]$position[1], 14, 14)
                    }
                }
                2 {
                    $draw.DrawRectangle($pen, 11, 18, 26, 34)
                    $draw.DrawRectangle($pen, 25, 10, 26, 34)
                    $draw.DrawLine($pen, 32, 22, 44, 22); $draw.DrawLine($pen, 32, 30, 44, 30)
                }
                3 {
                    $draw.DrawRectangle($pen, 16, 15, 32, 35)
                    $draw.DrawEllipse($pen, 11, 10, 12, 9); $draw.DrawEllipse($pen, 41, 45, 12, 9)
                    foreach ($line in @(25,33,41)) { $draw.DrawLine($pen, 23, $line, 41, $line) }
                }
                4 {
                    $draw.DrawEllipse($pen, 17, 17, 30, 30); $draw.DrawEllipse($pen, 25, 25, 14, 14)
                    for ($spoke = 0; $spoke -lt 8; $spoke++) {
                        $a = $spoke * [Math]::PI / 4
                        $draw.DrawLine($pen, [single](32+16*[Math]::Cos($a)), [single](32+16*[Math]::Sin($a)),
                            [single](32+23*[Math]::Cos($a)), [single](32+23*[Math]::Sin($a)))
                    }
                }
                5 {
                    $draw.DrawLine($pen, 25, 12, 13, 12); $draw.DrawLine($pen, 13, 12, 13, 52)
                    $draw.DrawLine($pen, 13, 52, 25, 52); $draw.DrawLine($pen, 26, 32, 52, 32)
                    $draw.DrawLine($pen, 43, 23, 52, 32); $draw.DrawLine($pen, 43, 41, 52, 32)
                }
            }
        } finally { $draw.Restore($saved) }
    }
    $icons.Save((Join-Path $OutputDirectory 'menu-icons.png'), [System.Drawing.Imaging.ImageFormat]::Png)
} finally { $fill.Dispose(); $pen.Dispose(); $draw.Dispose(); $icons.Dispose() }
