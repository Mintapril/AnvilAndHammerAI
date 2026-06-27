# AnvilAndHammerAI 构建脚本 —— 用本机 dotnet SDK(不依赖 PATH)。
# 用法: .\build.ps1            (Release 构建并部署到游戏 Modules)
#       .\build.ps1 -Config Debug
param(
    [string]$Config = "Release",
    [string]$Dotnet = "C:\Users\rangt\.dotnet\dotnet.exe"
)

$ErrorActionPreference = "Stop"
$proj = Join-Path $PSScriptRoot "src\AnvilAndHammerAI\AnvilAndHammerAI.csproj"

if (-not (Test-Path $Dotnet)) {
    throw "找不到 dotnet: $Dotnet —— 用 -Dotnet 指定路径。"
}

Write-Host "[A&H] 构建 $Config ..." -ForegroundColor Cyan
& $Dotnet build $proj -c $Config -v minimal
if ($LASTEXITCODE -ne 0) { throw "构建失败 (exit $LASTEXITCODE)" }
Write-Host "[A&H] 完成。" -ForegroundColor Green
