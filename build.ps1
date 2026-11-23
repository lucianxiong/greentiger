[CmdletBinding()]
param(
    [switch]$DebugBuild,
    [string]$Out = "",
    [switch]$NoIcon,
    [switch]$WebView2
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($root)) { $root = Split-Path -Parent $MyInvocation.MyCommand.Path }

# 构建门禁：版本落点与 UI 内嵌资源清单必须与事实源一致，不一致直接拒绝编译。
# 事实源分别是 src\Config.cs 的 AppInfo.Version 与 tools\project-manifest.js 的 UI_ASSETS。
& node (Join-Path $root "tools\check-version.js")
if ($LASTEXITCODE -ne 0) { throw "Version/resource consistency check failed" }

$framework = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319"
$csc = Join-Path $framework "csc.exe"
if (-not (Test-Path -LiteralPath $csc)) {
    $framework = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319"
    $csc = Join-Path $framework "csc.exe"
}
if (-not (Test-Path -LiteralPath $csc)) { throw "Cannot find csc.exe from .NET Framework 4.x" }

$ui = Join-Path $root "ui\index.html"
if (-not (Test-Path -LiteralPath $ui)) { throw "Missing ui\index.html" }
$shellJs = Join-Path $root "ui\shell.js"
if (-not (Test-Path -LiteralPath $shellJs)) { throw "Missing ui\shell.js" }
# 注：曾经的 ui\shell.css（独立页面外壳样式）已删除 —— 主控制台从未 link 过它，
# 保留只会让人以为在用。新增 UI 资源请同步三处：这里、App.ExportEmbeddedUi、WebServer 静态资源路由。
$themeCss = Join-Path $root "ui\theme.css"
if (-not (Test-Path -LiteralPath $themeCss)) { throw "Missing ui\theme.css" }
$gardenCss = Join-Path $root "ui\garden.css"
if (-not (Test-Path -LiteralPath $gardenCss)) { throw "Missing ui\garden.css" }
$gardenJs = Join-Path $root "ui\garden.js"
if (-not (Test-Path -LiteralPath $gardenJs)) { throw "Missing ui\garden.js" }
$styleCss = Join-Path $root "ui\style.css"
if (-not (Test-Path -LiteralPath $styleCss)) { throw "Missing ui\style.css" }
# P3：ES 模块随程序集内嵌，新增模块必须同时登记到 $resources 与 App.cs / WebServer.cs
$jsDir = Join-Path $root "ui\js"
foreach ($m in @("util.js","markdown.js","tools.js","chat.js","model.js","app.js","toolbox.js")) {
    if (-not (Test-Path -LiteralPath (Join-Path $jsDir $m))) { throw "Missing ui\js\$m" }
}
$manifest = Join-Path $root "app.manifest"
if (-not (Test-Path -LiteralPath $manifest)) { throw "Missing app.manifest" }
$webmanifest = Join-Path $root "ui\manifest.webmanifest"
if (-not (Test-Path -LiteralPath $webmanifest)) { throw "Missing ui\manifest.webmanifest" }
$icon192 = Join-Path $root "ui\icon-192.png"
if (-not (Test-Path -LiteralPath $icon192)) { throw "Missing ui\icon-192.png" }
$icon512 = Join-Path $root "ui\icon-512.png"
if (-not (Test-Path -LiteralPath $icon512)) { throw "Missing ui\icon-512.png" }
$swirl = Join-Path $root "ui\swirl.png"
if (-not (Test-Path -LiteralPath $swirl)) { throw "Missing ui\swirl.png" }

$outDir = Join-Path $root "dist"
if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
if ([string]::IsNullOrWhiteSpace($Out)) {
    $Out = Join-Path $outDir "GreenTiger.exe"
} elseif (-not [IO.Path]::IsPathRooted($Out)) {
    $Out = Join-Path $root $Out
}
$Out = [IO.Path]::GetFullPath($Out)

$sources = @(Get-ChildItem -LiteralPath (Join-Path $root "src") -Filter "*.cs" -File | ForEach-Object { $_.FullName })
if ($sources.Count -eq 0) { throw "No C# sources found in src" }

$cscArgs = New-Object System.Collections.Generic.List[string]
$cscArgs.Add("/nologo")
$cscArgs.Add("/target:winexe")
$cscArgs.Add("/platform:anycpu")
$cscArgs.Add("/codepage:65001")
$cscArgs.Add("/optimize+")
$cscArgs.Add($(if ($DebugBuild) { "/debug+" } else { "/debug-" }))
$cscArgs.Add("/warn:4")
$cscArgs.Add("/win32manifest:" + $manifest)
# 资源参数：路径含空格才加引号。PowerShell 7 会把字面引号原样传给 csc，
# 导致 CS2021（文件名含引号）；无空格路径直接拼，csc 用逗号分隔资源名。
function New-ResourceArg([string]$path, [string]$name) {
    if ($path.Contains(' ')) { return '/resource:"' + $path + '",' + $name }
    return '/resource:' + $path + ',' + $name
}
$resources = @(
    @($ui, "GreenTiger.ui.index.html"),
    @($styleCss, "GreenTiger.ui.style.css"),
    @($themeCss, "GreenTiger.ui.theme.css"),
    @((Join-Path $jsDir "util.js"), "GreenTiger.ui.js/util.js"),
    @((Join-Path $jsDir "markdown.js"), "GreenTiger.ui.js/markdown.js"),
    @((Join-Path $jsDir "tools.js"), "GreenTiger.ui.js/tools.js"),
    @((Join-Path $jsDir "chat.js"), "GreenTiger.ui.js/chat.js"),
    @((Join-Path $jsDir "model.js"), "GreenTiger.ui.js/model.js"),
    @((Join-Path $jsDir "app.js"), "GreenTiger.ui.js/app.js"),
    @((Join-Path $jsDir "toolbox.js"), "GreenTiger.ui.js/toolbox.js"),

    @($shellJs, "GreenTiger.ui.shell.js"),
    @($gardenCss, "GreenTiger.ui.garden.css"),
    @($gardenJs, "GreenTiger.ui.garden.js"),
    @($webmanifest, "GreenTiger.ui.manifest.webmanifest"),
    @($icon192, "GreenTiger.ui.icon-192.png"),
    @($icon512, "GreenTiger.ui.icon-512.png"),
    @($swirl, "GreenTiger.ui.swirl.png")
)
foreach ($r in $resources) { $cscArgs.Add((New-ResourceArg $r[0] $r[1])) }

$icon = Join-Path $root "assets\GreenTiger.ico"
if (-not $NoIcon -and (Test-Path -LiteralPath $icon)) { $cscArgs.Add("/win32icon:" + $icon) }

$references = @(
    "System.dll",
    "System.Core.dll",
    "System.Security.dll",
    "System.Drawing.dll",
    "System.Windows.Forms.dll",
    "System.Web.Extensions.dll",
    "System.Net.WebSockets.dll",
    "System.Net.WebSockets.Client.dll"
)
foreach ($reference in $references) {
    $path = Join-Path $framework $reference
    if (Test-Path -LiteralPath $path) { $cscArgs.Add("/r:" + $path) }
    else { $cscArgs.Add("/r:" + $reference) }
}

# 绿虎浏览器：lib\WebView2\ 存在时自动启用 WebView2 托管程序集（Core/WinForms + Loader）。
# 组件来自本机已有的 WebView2 SDK 部署；缺失时构建不带浏览器窗口（前端给出提示）。
# 注意：路径一律用 Join-Path 逐段拼接，源码中不写任何反斜杠字面量（外层会吞掉反斜杠）。
$wvEnabled = $false
$wvDir = Join-Path $root 'lib'
$wvDir = Join-Path $wvDir 'WebView2'
$wvCore = Join-Path $wvDir 'Microsoft.Web.WebView2.Core.dll'
$wvWinForms = Join-Path $wvDir 'Microsoft.Web.WebView2.WinForms.dll'
$wvLoader = Join-Path $wvDir 'WebView2Loader.dll'
if ($WebView2 -or (Test-Path -LiteralPath $wvWinForms)) {
    if (-not (Test-Path -LiteralPath $wvCore) -or -not (Test-Path -LiteralPath $wvWinForms) -or -not (Test-Path -LiteralPath $wvLoader)) {
        throw "WebView2 assemblies incomplete in lib\WebView2\ (Core/WinForms/Loader required)."
    }
    $cscArgs.Add('/define:WEBVIEW2_HOST')
    $cscArgs.Add('/r:' + $wvCore)
    $cscArgs.Add('/r:' + $wvWinForms)
    $wvEnabled = $true
    Write-Host ("绿虎浏览器已启用: " + $wvDir) -ForegroundColor Green
}

$cscArgs.Add("/out:" + $Out)
foreach ($source in $sources) { $cscArgs.Add($source) }

Write-Host "Compiling $Out ..." -ForegroundColor Cyan
Push-Location $root
try {
    $cscOut = & $csc $cscArgs.ToArray() 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { Write-Host $cscOut -ForegroundColor Red; throw "Compilation failed with exit code $LASTEXITCODE" }
} finally {
    Pop-Location
}

# 绿虎浏览器依赖的托管程序集与 WebView2Loader.dll 必须位于 exe 同级的 lib\WebView2。
if ($wvEnabled) {
    $outParent = Split-Path -Parent $Out
    $destWv = Join-Path (Join-Path $outParent 'lib') 'WebView2'
    $srcFull = [IO.Path]::GetFullPath($wvDir).TrimEnd('\')
    $dstFull = [IO.Path]::GetFullPath($destWv).TrimEnd('\')
    if (-not [string]::Equals($srcFull, $dstFull, [StringComparison]::OrdinalIgnoreCase)) {
        if (-not (Test-Path -LiteralPath $destWv)) { New-Item -ItemType Directory -Path $destWv | Out-Null }
        foreach ($dll in @($wvCore, $wvWinForms, $wvLoader)) {
            $dest = Join-Path $destWv ([IO.Path]::GetFileName($dll))
            try { Copy-Item -LiteralPath $dll -Destination $dest -Force }
            catch {
                if (Test-Path -LiteralPath $dest) {
                    Write-Host ("WebView2 " + [IO.Path]::GetFileName($dll) + " 被运行中的实例占用，跳过复制（沿用现有组件）。") -ForegroundColor Yellow
                } else { throw }
            }
        }
        Write-Host ("WebView2 runtime ensured at " + $destWv) -ForegroundColor Green
    }
}

$file = Get-Item -LiteralPath $Out
$hash = (Get-FileHash -LiteralPath $Out -Algorithm SHA256).Hash

Write-Host ""
Write-Host ("Output : " + $file.FullName) -ForegroundColor Green
Write-Host ("Size   : " + [math]::Round($file.Length / 1KB, 1) + " KB")
Write-Host ("SHA256 : " + $hash) -ForegroundColor DarkGray
