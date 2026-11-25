param([Parameter(Mandatory)][string]$CaptureDirectory, [Parameter(Mandatory)][string]$OutputFile,
    [switch]$QwenDownloadPreview, [switch]$QwenStudioPreview, [switch]$QwenWebPreview)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$panels = @(
    @('light-home.png', '启动'), @('curseforge-selected-file.png', '模组 · CurseForge 官方'),
    @('qwen-search-result.png', '千问 AI 搜索 · 离线示例'), @('dark-qwen-search.png', '千问 AI 搜索 · 深色界面')
)
if ($QwenDownloadPreview) {
    $panels[0] = @('qwen-download-action.png', '千问下载指令 · 离线示例')
}
if ($QwenStudioPreview) {
    $panels = @(
        @('qwen-chat-result.png', '连续对话 · 测试示例'), @('qwen-media.png', '图片与视频创作 · 测试示例'),
        @('qwen-download-action.png', '模组下载指令'), @('dark-qwen-search.png', '千问 AI 助手 · 深色界面')
    )
}
if ($QwenWebPreview) {
    $panels = @(
        @('qwen-web.png', '千问官网 · 内嵌浏览器测试示例'), @('qwen-chat-result.png', '聊天与下载指令'),
        @('qwen-media.png', '百炼创作 · 测试示例'), @('qwen-download-action.png', '模组搜索和安装')
    )
    if (Test-Path -LiteralPath (Join-Path $CaptureDirectory 'qwen-web-live.png')) {
        $panels[0] = @('qwen-web-live.png', '千问官网 · 启动器内未登录界面')
    }
}
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
