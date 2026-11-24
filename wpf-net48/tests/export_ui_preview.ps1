param([Parameter(Mandatory)][string]$CaptureDirectory, [Parameter(Mandatory)][string]$OutputFile)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$panels = @(
    @('light-home.png', '启动'), @('light-curseforge.png', 'CurseForge 官方下载'),
    @('dark-skin-editor.png', '皮肤编辑'), @('startup-loading.png', '加载画面')
)
$tileWidth = 525
$tileHeight = 315
$padding = 14
$caption = 26
$width = 2 * $tileWidth + 3 * $padding
$height = 2 * ($tileHeight + $caption) + 3 * $padding
$canvas = [Drawing.Bitmap]::new($width, $height)
$gfx = [Drawing.Graphics]::FromImage($canvas)
$font = [Drawing.Font]::new('Microsoft YaHei UI', 13, [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
try {
    $gfx.Clear([Drawing.Color]::FromArgb(233, 238, 246))
    $gfx.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    for ($i = 0; $i -lt $panels.Count; $i++) {
        $img = [Drawing.Image]::FromFile((Join-Path $CaptureDirectory $panels[$i][0]))
        try {
            $x = $padding + ($i % 2) * ($tileWidth + $padding)
            $y = $padding + [Math]::Floor($i / 2) * ($tileHeight + $caption + $padding)
            $gfx.DrawString($panels[$i][1], $font, [Drawing.Brushes]::MidnightBlue, [single]$x, [single]$y)
            $scale = [Math]::Min($tileWidth / $img.Width, $tileHeight / $img.Height)
            $w = [int]($img.Width * $scale)
            $h = [int]($img.Height * $scale)
            $box = [Drawing.Rectangle]::new([int]($x + ($tileWidth - $w) / 2), [int]($y + $caption + ($tileHeight - $h) / 2), $w, $h)
            $gfx.DrawImage($img, $box)
        } finally { $img.Dispose() }
    }
    $canvas.Save($OutputFile, [Drawing.Imaging.ImageFormat]::Jpeg)
} finally { $font.Dispose(); $gfx.Dispose(); $canvas.Dispose() }
