#Requires -Version 5.1
<#
  学生机部署（命令行版；**双击安装请直接用 release\NetLock-Student-Setup-*.exe**）
  做两件事：把 dist\ 拷到 C:\Program Files\NetLock → 调那个 exe 自己的 --install（注册服务、
  建遮罩计划任务、收紧数据目录权限、导入配对文件；与安装包**共用同一份代码**）
  用法：powershell -ExecutionPolicy Bypass -File install-student.ps1 -Pair D:\netlock-pairing.json
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
    Write-Host ('找不到 NetLock.Student.exe：' + $SourceDir + '（先 build 并把 dist\ 拷过来）') -ForegroundColor Red
    exit 1
}
if (-not (Test-Path -LiteralPath $Pair)) { Write-Host ('找不到配对文件：' + $Pair) -ForegroundColor Red; exit 1 }

[IO.Directory]::CreateDirectory($InstallDir) | Out-Null
Copy-Item -Path (Join-Path $SourceDir '*') -Destination $InstallDir -Recurse -Force
$exe = Join-Path $InstallDir 'NetLock.Student.exe'

Write-Host '注册服务 + 遮罩计划任务 + 收紧权限 + 导入配对…' -ForegroundColor Cyan
& $exe --install --pair $Pair
$rc = $LASTEXITCODE
Write-Host ''
Write-Host '=== 部署后核对（这台学生机）===' -ForegroundColor Green
& $exe --status
Write-Host ''
Write-Host '「已配对 = True」且指纹与教师机一致 → 部署成功。'
Write-Host '注意：本精减版**不防学生停服务/杀进程**（那是完整版 LabGuard 的能力）。'
exit $rc
