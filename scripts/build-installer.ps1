#Requires -Version 5.1
<#
  打**双端安装包 exe**（双击即装，老师不用碰 PowerShell）：
    ① 先构建出 dist\（可 -SkipBuild 复用）
    ② 把 dist\ 内嵌进两个安装器（payload.zip 编译进 exe = 单文件）
    ③ 产物：release\NetLock-Teacher-Setup-v<版本>.exe / NetLock-Student-Setup-v<版本>.exe
    ④ **对每个安装包跑 --selftest**（解压内嵌程序包 + 校验必需文件都在）——不是"文件存在"就算过
  用法：powershell -File scripts\build-installer.ps1 [-SkipBuild]
#>
[CmdletBinding()]
param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$release = Join-Path $root 'release'
$payload = Join-Path $root 'src\NetLock.Installer\payload.zip'

$props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw -Encoding UTF8
$version = [regex]::Match($props, '<Version>([^<]+)</Version>').Groups[1].Value.Trim()
Write-Host ('NetLock 安装包构建：版本 ' + $version) -ForegroundColor Cyan

if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') }
if (-not (Test-Path -LiteralPath (Join-Path $dist 'NetLock.Student.exe'))) { throw ('dist 里没有产物：' + $dist) }

Write-Host '== 内嵌程序包（dist → payload.zip）==' -ForegroundColor Cyan
if (Test-Path -LiteralPath $payload) { Remove-Item -LiteralPath $payload -Force }
Compress-Archive -Path (Join-Path $dist '*') -DestinationPath $payload -Force
Write-Host ('  payload.zip ' + [math]::Round((Get-Item -LiteralPath $payload).Length / 1KB, 1) + ' KB')

Write-Host '== 编译两个安装器（把 payload 嵌进去）==' -ForegroundColor Cyan
& dotnet build (Join-Path $root 'src\NetLock.Setup.Teacher\NetLock.Setup.Teacher.csproj') -c Release --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw '教师端安装包构建失败' }
& dotnet build (Join-Path $root 'src\NetLock.Setup.Student\NetLock.Setup.Student.csproj') -c Release --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw '学生端安装包构建失败' }

[IO.Directory]::CreateDirectory($release) | Out-Null
$pairs = @(
    @{ src = 'src\NetLock.Setup.Teacher\bin\Release\net48\NetLock-Teacher-Setup.exe'; name = ('NetLock-Teacher-Setup-v' + $version + '.exe') },
    @{ src = 'src\NetLock.Setup.Student\bin\Release\net48\NetLock-Student-Setup.exe'; name = ('NetLock-Student-Setup-v' + $version + '.exe') }
)
foreach ($p in $pairs) {
    $src = Join-Path $root $p.src
    if (-not (Test-Path -LiteralPath $src)) { throw ('缺少安装包：' + $src) }
    $dst = Join-Path $release $p.name
    Copy-Item -LiteralPath $src -Destination $dst -Force
    $f = Get-Item -LiteralPath $dst
    $sha = (Get-FileHash -LiteralPath $dst -Algorithm SHA256).Hash
    Write-Host ('  ' + $f.Name + '  ' + $f.Length + ' B  SHA256 ' + $sha.Substring(0, 16) + '…')
    Write-Host '  --- 自检（解压内嵌程序包并校验）---'
    & $dst --selftest
    if ($LASTEXITCODE -ne 0) { throw ('安装包自检失败：' + $f.Name) }
}
Write-Host '== 图标闸门（含两个安装包）==' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'check-icons.ps1') -IncludeRelease
if ($LASTEXITCODE -ne 0) { throw '图标检查失败（见上）' }

Write-Host ''
Write-Host ('完成：安装包在 ' + $release) -ForegroundColor Green
Write-Host '  教师机：右键以管理员身份运行 NetLock-Teacher-Setup-*.exe'
Write-Host '  学生机：右键以管理员身份运行 NetLock-Student-Setup-*.exe（向导里选教师机导出的配对文件）'
Write-Host '  无人值守：NetLock-Student-Setup-v*.exe --silent --role student --pair D:\netlock-pairing.json'
