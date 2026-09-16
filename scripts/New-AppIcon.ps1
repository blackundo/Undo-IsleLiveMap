param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\src\TheIsleOverlay.App\Assets\IsleLiveMap.ico')
)

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class NativeIconHandle {
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr handle);
}
'@

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $resolvedOutput
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null

$bitmap = New-Object System.Drawing.Bitmap 256,256
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::FromArgb(20,10,36))

$purple = [System.Drawing.Color]::FromArgb(183,92,255)
$lavender = [System.Drawing.Color]::FromArgb(246,236,255)
$magenta = [System.Drawing.Color]::FromArgb(239,92,196)
$linePen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(110,183,92,255)),6
$innerPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(165,183,92,255)),3
$graphics.DrawEllipse($linePen,28,28,200,200)
$graphics.DrawEllipse($innerPen,63,63,130,130)

$arrow = New-Object System.Drawing.Drawing2D.GraphicsPath
$arrow.AddPolygon([System.Drawing.Point[]]@(
    [System.Drawing.Point]::new(220,128),
    [System.Drawing.Point]::new(95,172),
    [System.Drawing.Point]::new(119,128),
    [System.Drawing.Point]::new(95,84)
))
$arrowBrush = New-Object System.Drawing.SolidBrush $lavender
$arrowPen = New-Object System.Drawing.Pen $purple,5
$graphics.FillPath($arrowBrush,$arrow)
$graphics.DrawPath($arrowPen,$arrow)

$centerBrush = New-Object System.Drawing.SolidBrush $magenta
$graphics.FillEllipse($centerBrush,116,116,24,24)

$iconHandle = $bitmap.GetHicon()
$icon = [System.Drawing.Icon]::FromHandle($iconHandle)
$stream = [System.IO.File]::Create($resolvedOutput)
$icon.Save($stream)
$stream.Dispose()
$icon.Dispose()
[NativeIconHandle]::DestroyIcon($iconHandle) | Out-Null
$centerBrush.Dispose()
$arrowPen.Dispose()
$arrowBrush.Dispose()
$arrow.Dispose()
$innerPen.Dispose()
$linePen.Dispose()
$graphics.Dispose()
$bitmap.Dispose()

Write-Output $resolvedOutput
