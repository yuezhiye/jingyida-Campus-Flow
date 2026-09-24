# =============================================================================
#  SrunAutoLogin 一键编译脚本
#  用途：用 Windows 自带的 .NET Framework 编译器把 src\SrunAutoLogin.cs
#        编译成 build\SrunAutoLogin.exe
#  用法：右键「使用 PowerShell 运行」，或在 PowerShell 中执行
#        powershell -ExecutionPolicy Bypass -File .\build.ps1
# =============================================================================

$ErrorActionPreference = 'Stop'

$Root       = Split-Path -Parent $MyInvocation.MyCommand.Definition
$SourceDir  = Join-Path $Root 'src'
$OutputDir  = Join-Path $Root 'build'
$SourceFile = Join-Path $SourceDir 'SrunAutoLogin.cs'
$OutputExe  = Join-Path $OutputDir 'SrunAutoLogin.exe'

Write-Host ''
Write-Host '=============================================' -ForegroundColor Cyan
Write-Host '  SrunAutoLogin 编译' -ForegroundColor Cyan
Write-Host '  深澜校园网自动认证' -ForegroundColor Cyan
Write-Host '=============================================' -ForegroundColor Cyan
Write-Host ''

# ---- 1. 检查源码 ----
if (-not (Test-Path $SourceFile)) {
    Write-Host "[错误] 找不到源码：$SourceFile" -ForegroundColor Red
    exit 1
}
Write-Host "[1/4] 源码就绪：$SourceFile" -ForegroundColor Green

# ---- 2. 定位编译器 ----
$candidates = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
$Compiler = $null
foreach ($c in $candidates) {
    if (Test-Path $c) { $Compiler = $c; break }
}

if (-not $Compiler) {
    Write-Host '[错误] 未找到 .NET Framework 4.x 编译器。' -ForegroundColor Red
    Write-Host '       请安装 .NET Framework 4.8 Developer Pack，或使用自带编译器的 Windows 10/11。'
    exit 1
}
Write-Host "[2/4] 编译器：$Compiler" -ForegroundColor Green

# ---- 3. 准备输出目录 ----
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

# ---- 4. 编译 ----
$refs = @(
    'System.dll',
    'System.Core.dll',
    'System.Drawing.dll',
    'System.Windows.Forms.dll',
    'System.Web.Extensions.dll'
)

# 注意：路径可能含空格，csc 不接受数组元素自身被引号包住（会被当成文件名的一部分），
#       要让 PowerShell 只在传参边界补引号，所以这里存原始路径、由下面统一处理。
$argList = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+'
)
foreach ($r in $refs) { $argList += '/reference:' + $r }

Write-Host '[3/4] 正在编译…' -ForegroundColor Yellow
# 用数组展开 + 每项独立传参：/out: 与源文件路径含空格时由 PowerShell 自动加引号
& $Compiler @argList ("/out:" + $OutputExe) $SourceFile

if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host '[错误] 编译失败，请查看上方错误信息。' -ForegroundColor Red
    exit $LASTEXITCODE
}

$size = (Get-Item $OutputExe).Length

# 同步一份到项目根目录，方便直接双击运行（编译产物始终以 build\ 下为源）
$RootExe = Join-Path $Root 'SrunAutoLogin.exe'
if ($OutputExe -ne $RootExe) {
    Copy-Item -LiteralPath $OutputExe -Destination $RootExe -Force
}

Write-Host ''
Write-Host '[4/4] 编译成功！' -ForegroundColor Green
Write-Host "      产物：$OutputExe" -ForegroundColor Green
Write-Host "      大小：$([math]::Round($size / 1KB, 1)) KB" -ForegroundColor Green
Write-Host "      已同步：$RootExe" -ForegroundColor Green
Write-Host ''
Write-Host '      双击根目录的 SrunAutoLogin.exe 即可运行。' -ForegroundColor White
Write-Host ''
