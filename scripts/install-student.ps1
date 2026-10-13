#Requires -Version 5.1
<#
  学生机一键部署（**需要在学生机上以管理员身份运行**）：
    把程序拷到 C:\Program Files\NetLock → 调 NetLock.Student.exe --install
    （它会：注册服务、建"遮罩"计划任务、收紧数据目录权限；可同时 --Pair 导入配对文件）
  用法：
    powershell -ExecutionPolicy Bypass -File install-student.ps1 -Pair D:\netlock-pairing.json
    powershell -ExecutionPolicy Bypass -File install-student.ps1 -Pair \\服务器\share\netlock-pairing.json
  说明：**只有配对过的机器才会听教师机的话**（连接密钥在配对文件里，等于钥匙，别乱放）。
#>
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$Pair, [string]$SourceDir, [string]$InstallDir = 'C:\Program Files\NetLock')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $SourceDir) { $SourceDir = Join-Path $root 'dist' }

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host '失败：请以管理员身份运行本脚本。' -ForegroundColor Red
    exit 2
}
if (-not (Test-Path -LiteralPath (Join-Path $SourceDir 'NetLock.Student.exe'))) {
    Write-Host ('找不到 NetLock.Student.exe：' + $SourceDir + '（先在开发机上 build，并拷 dist\ 过来）') -ForegroundColor Red
    exit 1
}
if (-not (Test-Path -LiteralPath $Pair)) { Write-Host ('找不到配对文件：' + $Pair) -ForegroundColor Red; exit 1 }

[IO.Directory]::CreateDirectory($InstallDir) | Out-Null
Copy-Item -Path (Join-Path $SourceDir '*') -Destination $InstallDir -Recurse -Force
$exe = Join-Path $InstallDir 'NetLock.Student.exe'

Write-Host '注册服务 + 遮罩计划任务 + 导入配对…' -ForegroundColor Cyan
& $exe --install --pair $Pair
$rc = $LASTEXITCODE
Write-Host ''
Write-Host '=== 部署后建议核对（在这台学生机上）===' -ForegroundColor Green
& $exe --status
Write-Host ''
Write-Host '若「已配对」为 True 且指纹与教师机上显示的一致 → 部署成功。'
Write-Host '注意：本精减版**不防学生停服务/杀进程**（那是完整版 LabGuard 的能力）。'
exit $rc
