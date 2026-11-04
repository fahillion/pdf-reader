# Builds PdfReader.exe using the C# compiler and libraries that ship with Windows.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$fx = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$gac = "$env:WINDIR\Microsoft.NET\assembly\GAC_MSIL"

# Draws the app icon once (a coral tile with a white page) and stores it as a PNG-based .ico.
$ico = "$here\app.ico"
if (-not (Test-Path $ico)) {
    Add-Type -AssemblyName System.Drawing
    $bmp = New-Object Drawing.Bitmap 256, 256
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    function RoundRect($x, $y, $w, $h, $r) {
        $p = New-Object Drawing.Drawing2D.GraphicsPath
        $p.AddArc($x, $y, $r, $r, 180, 90); $p.AddArc($x + $w - $r, $y, $r, $r, 270, 90)
        $p.AddArc($x + $w - $r, $y + $h - $r, $r, $r, 0, 90); $p.AddArc($x, $y + $h - $r, $r, $r, 90, 90)
        $p.CloseFigure(); $p
    }
    $coral = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(242, 118, 95))
    $white = New-Object Drawing.SolidBrush ([Drawing.Color]::White)
    $g.FillPath($coral, (RoundRect 8 8 240 240 64))
    $g.FillPath($white, (RoundRect 72 48 112 160 20))
    foreach ($y in 88, 120, 152) { $g.FillRectangle($coral, 92, $y, 72, 12) }
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    $png = $ms.ToArray()
    $out = New-Object IO.MemoryStream
    $w = New-Object IO.BinaryWriter $out
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]1)
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$png.Length); $w.Write([uint32]22)
    $w.Write($png); $w.Flush()
    [IO.File]::WriteAllBytes($ico, $out.ToArray())
    $g.Dispose(); $bmp.Dispose()
}

$refs = @(
    "$fx\System.dll",
    "$fx\System.Core.dll",
    "$fx\System.Xml.dll",
    "$fx\System.Xaml.dll",
    "$fx\System.Drawing.dll",
    "$fx\System.Windows.Forms.dll",
    "$fx\System.Security.dll",
    "$fx\System.Net.Http.dll",
    "$fx\System.Web.Extensions.dll",
    "$fx\System.Runtime.WindowsRuntime.dll",
    "$fx\WPF\PresentationCore.dll",
    "$fx\WPF\PresentationFramework.dll",
    "$fx\WPF\WindowsBase.dll"
)
foreach ($facade in 'System.Runtime', 'System.Threading.Tasks') {
    $found = Get-ChildItem "$gac\$facade" -Recurse -Filter "$facade.dll" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) { $refs += $found.FullName }
}
$refs += (Get-ChildItem "$env:WINDIR\System32\WinMetadata\*.winmd").FullName

$rsp = Join-Path $env:TEMP 'pdfreader-build.rsp'
$lines = @('/nologo', '/nostdlib+', '/target:winexe', '/platform:x64', '/optimize+',
    "/win32manifest:`"$here\app.manifest`"", "/win32icon:`"$ico`"", "/out:`"$here\PdfReader.exe`"",
    "/resource:`"$here\Theme.xaml`",Theme.xaml", "/resource:`"$here\Main.xaml`",Main.xaml",
    "/r:`"$fx\mscorlib.dll`"")
$lines += $refs | ForEach-Object { "/r:`"$_`"" }
$lines += "`"$here\PdfReader.cs`"", "`"$here\ChatGptAuth.cs`"", "`"$here\ClaudeCode.cs`""
Set-Content -Path $rsp -Value $lines -Encoding ASCII

& "$fx\csc.exe" "@$rsp"
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Output "Built $here\PdfReader.exe"
