<#
    一键产出安装包：publish -> Inno Setup -> 更新清单。
    用法：
        .\build.ps1                  # 用 csproj 里的 Version
        .\build.ps1 -Version 1.2.0   # 临时指定版本（不改 csproj）
        .\build.ps1 -NoSetup         # 只 publish，不编译安装包
#>
param(
    [string]$Version,
    [switch]$NoSetup
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'NotepadSharp\NotepadSharp.csproj'
$artifacts = Join-Path $root 'artifacts'
$publishDir = Join-Path $artifacts 'publish'

if (-not $Version) {
    $Version = (Select-String -Path $proj -Pattern '<Version>([^<]+)</Version>').Matches[0].Groups[1].Value
    if (-not $Version) { throw "没能从 $proj 读出 <Version>" }
}
Write-Host "版本 $Version" -ForegroundColor Cyan

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

dotnet publish $proj -c Release -r win-x64 --self-contained false -o $publishDir --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）" }

# 停靠内核必须跟着走，少了它程序在别的机器上直接起不来
if (-not (Test-Path (Join-Path $publishDir 'VsDlls\Microsoft.VisualStudio.Shell.ViewManager.dll'))) {
    throw 'publish 产物里缺少 VsDlls，检查 csproj 的 Content 配置'
}

if ($NoSetup) { Write-Host "publish 完成：$publishDir" -ForegroundColor Green; return }

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe'
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw '找不到 ISCC.exe。先装 Inno Setup 6：winget install -e --id JRSoftware.InnoSetup'
}

& $iscc "/DMyAppVersion=$Version" "/DPublishDir=$publishDir" "/DOutputDir=$artifacts" (Join-Path $root 'packaging\NotepadSharp.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC 编译失败（退出码 $LASTEXITCODE）" }

$setup = Join-Path $artifacts "NotepadSharp-$Version-setup.exe"
if (-not (Test-Path $setup)) { throw "ISCC 说成功了但找不到 $setup" }

# 这个 json 提交到仓库的 release/latest.json，程序端靠它发现新版本
$sha = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = [ordered]@{
    version = $Version
    file    = "NotepadSharp-$Version-setup.exe"
    sha256  = $sha
    notes   = ''
}
$manifestPath = Join-Path $artifacts 'latest.json'
# 不能带 BOM：程序端拿到的是字符串，System.Text.Json 解析带 BOM 的字符串会直接抛
[System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))

$sizeMb = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host ''
Write-Host "安装包  $setup  ($sizeMb MB)" -ForegroundColor Green
Write-Host "sha256  $sha"
Write-Host "清单    $manifestPath  ->  提交为仓库的 release/latest.json" -ForegroundColor Green
Write-Host ''
Write-Host '发布还差两步（Gitee 的 API 看不到手动附件，附件只能网页上传）：' -ForegroundColor Yellow
Write-Host "  1. 建 Release，tag 用 $Version，上传 $setup"
Write-Host "  2. 把 latest.json 提交到仓库 release/ 目录并 push"
