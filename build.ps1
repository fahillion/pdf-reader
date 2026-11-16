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

# PDFium reads the text in PDFs (selecting, copying, Find). It's a pinned build from
# github.com/bblanchon/pdfium-binaries, checked by hash; only its license files are kept in git.
$pdfiumTag = 'chromium/8125'
$pdfiumHash = '9a964261760cff52570aabf038ab346d3a5034dd8b30d0d5fe8861235d356b8b'
$pdfiumDir = "$here\third_party\pdfium"
$pdfiumDll = "$pdfiumDir\pdfium.dll"
if (-not (Test-Path $pdfiumDll)) {
    $tgz = Join-Path $env:TEMP 'pdfium-win-x64.tgz'
    Invoke-WebRequest "https://github.com/bblanchon/pdfium-binaries/releases/download/$pdfiumTag/pdfium-win-x64.tgz" -OutFile $tgz -UseBasicParsing
    if ((Get-FileHash $tgz -Algorithm SHA256).Hash -ne $pdfiumHash) { Remove-Item $tgz; throw "PDFium download doesn't match the expected hash" }
    $unpack = Join-Path $env:TEMP 'pdfium-unpack'
    if (Test-Path $unpack) { Remove-Item $unpack -Recurse -Force }
    New-Item -ItemType Directory $unpack | Out-Null
    & "$env:WINDIR\System32\tar.exe" -xzf $tgz -C $unpack bin/pdfium.dll
    if ($LASTEXITCODE -ne 0) { throw "Couldn't unpack PDFium" }
    Copy-Item "$unpack\bin\pdfium.dll" $pdfiumDll
    Remove-Item $unpack -Recurse -Force; Remove-Item $tgz
}
Copy-Item $pdfiumDll "$here\pdfium.dll" -Force   # Next to PdfReader.exe, for running the build in place.

# One notices file for everything PDFium includes, shipped with the app.
$notices = "$here\THIRD-PARTY-NOTICES.txt"
$parts = @("PDF Reader includes PDFium ($pdfiumTag build from https://github.com/bblanchon/pdfium-binaries).", '')
$parts += "==== pdfium-binaries ====", (Get-Content "$pdfiumDir\LICENSE" -Raw -Encoding UTF8), ''
foreach ($f in Get-ChildItem "$pdfiumDir\licenses" | Sort-Object Name) { $parts += "==== $($f.Name) ====", (Get-Content $f.FullName -Raw -Encoding UTF8), '' }
[IO.File]::WriteAllText($notices, ($parts -join "`r`n"), (New-Object Text.UTF8Encoding $false))

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
$lines += "`"$here\PdfReader.cs`"", "`"$here\PdfText.cs`"", "`"$here\TextTools.cs`"", "`"$here\ChatGptAuth.cs`"", "`"$here\ClaudeCode.cs`""
Set-Content -Path $rsp -Value $lines -Encoding ASCII

& "$fx\csc.exe" "@$rsp"
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Output "Built $here\PdfReader.exe"

# The installer carries the app inside it, so it's one file to copy to another PC.
$dist = "$here\dist"
New-Item -ItemType Directory -Force $dist | Out-Null
# The file name carries the version from Setup.cs, for example PDFReaderSetup-1.1.0.exe.
$version = [regex]::Match((Get-Content "$here\Setup.cs" -Raw), 'public const string Version = "([0-9.]+)"').Groups[1].Value
if (-not $version) { throw "Couldn't read the version from Setup.cs" }
$setupExe = "$dist\PDFReaderSetup-$version.exe"
$setupRsp = Join-Path $env:TEMP 'pdfreader-setup.rsp'
$setupLines = @('/nologo', '/nostdlib+', '/target:winexe', '/platform:x64', '/optimize+',
    "/win32manifest:`"$here\setup.manifest`"", "/win32icon:`"$ico`"", "/out:`"$setupExe`"",
    "/resource:`"$here\PdfReader.exe`",PdfReader.exe", "/resource:`"$here\Theme.xaml`",Theme.xaml",
    "/resource:`"$pdfiumDll`",pdfium.dll", "/resource:`"$notices`",THIRD-PARTY-NOTICES.txt",
    "/r:`"$fx\mscorlib.dll`"")
$setupLines += $refs | ForEach-Object { "/r:`"$_`"" }
$setupLines += "`"$here\Setup.cs`""
Set-Content -Path $setupRsp -Value $setupLines -Encoding ASCII
& "$fx\csc.exe" "@$setupRsp"
if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }
Write-Output "Built $setupExe"
