# ============================================================
# publish-nuget.ps1 — 打包并发布 NuGet 包 (PowerShell)
#
# 用法:
#   .\scripts\publish-nuget.ps1                     # 使用 csproj 中的版本号
#   .\scripts\publish-nuget.ps1 -Version 1.0.0      # 覆盖版本号
#   .\scripts\publish-nuget.ps1 -Version 1.0.0-preview.1  # 预览版
#   .\scripts\publish-nuget.ps1 -SkipTests           # 跳过测试（慎用）
#   .\scripts\publish-nuget.ps1 -SkipConfirm         # 跳过发布确认（CI 用）
#
# 环境变量:
#   NUGET_CENTER_API_KEY  — NuGet API Key（必须）
#   NUGET_SOURCE          — NuGet 源地址（可选，默认 nuget.org）
# ============================================================

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Version,

    [switch]$SkipTests,

    [switch]$SkipConfirm
)

$ErrorActionPreference = 'Stop'

# ── 辅助函数 ──
function Write-Info  { param([string]$Message) Write-Host "[INFO] $Message" -ForegroundColor Green }
function Write-Warn  { param([string]$Message) Write-Host "[WARN] $Message" -ForegroundColor Yellow }
function Write-Err   { param([string]$Message) Write-Host "[ERROR] $Message" -ForegroundColor Red }

# ── 定位项目根目录 ──
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$RootDir = Split-Path -Parent $ScriptDir

# ── 配置 ──
$NugetSource = if ($env:NUGET_SOURCE) { $env:NUGET_SOURCE } else { 'https://api.nuget.org/v3/index.json' }
$Configuration = 'Release'
$OutputDir = Join-Path $RootDir 'artifacts\nupkg'

# 要发布的项目列表
$Projects = @(
    'src\Mugu.AI.VectorLite\Mugu.AI.VectorLite.csproj'
    'src\Mugu.AI.VectorLite.SemanticKernel\Mugu.AI.VectorLite.SemanticKernel.csproj'
)

# ── 前置检查 ──
$ApiKey = $env:NUGET_CENTER_API_KEY
if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    Write-Err '环境变量 NUGET_CENTER_API_KEY 未设置。'
    Write-Err '请设置后重试: $env:NUGET_CENTER_API_KEY = "your-api-key"'
    exit 1
}

Write-Info "NuGet 源: $NugetSource"
Write-Info "构建配置: $Configuration"
if ($Version) {
    Write-Info "版本号: $Version"
} else {
    Write-Info "版本号: 使用项目文件中的默认版本"
}

# ── 清理输出目录 ──
if (Test-Path $OutputDir) {
    Remove-Item $OutputDir -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

# ── 构建 ──
Write-Info '正在构建...'
Push-Location $RootDir
try {
    dotnet build --configuration $Configuration
    if ($LASTEXITCODE -ne 0) {
        Write-Err '构建失败，终止发布。'
        exit 1
    }
    Write-Info '构建成功 ✓'

    # ── 运行测试 ──
    if (-not $SkipTests) {
        Write-Info '正在运行测试...'
        dotnet test --configuration $Configuration --no-build --verbosity minimal
        if ($LASTEXITCODE -ne 0) {
            Write-Err '测试未通过，终止发布。'
            exit 1
        }
        Write-Info '所有测试通过 ✓'
    } else {
        Write-Warn '已跳过测试（-SkipTests）'
    }
} finally {
    Pop-Location
}

# ── 打包 ──
Write-Info '正在打包...'
foreach ($Project in $Projects) {
    $ProjectPath = Join-Path $RootDir $Project
    $ProjectName = [System.IO.Path]::GetFileNameWithoutExtension($Project)

    if (-not (Test-Path $ProjectPath)) {
        Write-Err "项目文件不存在: $ProjectPath"
        exit 1
    }

    $PackArgs = @(
        'pack', $ProjectPath,
        '--configuration', $Configuration,
        '--output', $OutputDir,
        '--no-build'
    )

    if ($Version) {
        $PackArgs += @('-p:Version=' + $Version)
    }

    Write-Info "  打包 $ProjectName ..."
    & dotnet @PackArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Err "打包 $ProjectName 失败。"
        exit 1
    }
}

# ── 列出生成的包 ──
$Packages = Get-ChildItem -Path $OutputDir -Filter '*.nupkg'
if ($Packages.Count -eq 0) {
    Write-Err '未找到任何 .nupkg 文件。'
    exit 1
}

Write-Info '生成的 NuGet 包:'
foreach ($Pkg in $Packages) {
    Write-Info "  $($Pkg.Name)"
}

# ── 发布确认 ──
if (-not $SkipConfirm) {
    Write-Host ''
    Write-Warn "即将发布以上 $($Packages.Count) 个包到 $NugetSource"
    $Confirm = Read-Host '确认发布？[y/N]'
    if ($Confirm -notin @('y', 'Y')) {
        Write-Info "已取消发布。包文件保留在: $OutputDir"
        exit 0
    }
}

# ── 推送到 NuGet ──
Write-Info '正在推送到 NuGet...'
$PushSuccess = 0
$PushFail = 0

foreach ($Pkg in $Packages) {
    $PkgPath = $Pkg.FullName
    $PkgName = $Pkg.Name
    Write-Info "  推送 $PkgName ..."

    dotnet nuget push $PkgPath `
        --api-key $ApiKey `
        --source $NugetSource `
        --skip-duplicate

    if ($LASTEXITCODE -eq 0) {
        Write-Info "  ✓ $PkgName 推送成功"
        $PushSuccess++
    } else {
        Write-Err "  ✗ $PkgName 推送失败"
        $PushFail++
    }
}

# ── 结果汇总 ──
Write-Host ''
Write-Info '════════════════════════════════════════'
Write-Info "发布完成: $PushSuccess 成功, $PushFail 失败"
Write-Info "包文件目录: $OutputDir"
Write-Info '════════════════════════════════════════'

if ($PushFail -gt 0) {
    exit 1
}
