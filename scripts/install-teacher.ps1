#Requires -Version 5.1
<#
  教师机部署（命令行版；**双击安装请直接用 release\NetLock-Teacher-Setup-*.exe**）
  做两件事：把 dist\ 拷到 C:\Program Files\NetLockTeacher → 调那个 exe 自己的 --install
  （URLACL / 防火墙 / 登录自启 都在 exe 里实现，与安装包**共用同一份代码**，不会漂移）
  用法：powershell -ExecutionPolicy Bypass -File install-teacher.ps1 [-Port 8210] [-SourceDir ..\dist]
#>
[CmdletBinding()]
param([int]$Port = 8210, [string]$SourceDir, [string]$InstallDir = 'C:\Program Files\NetLockTeacher')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $SourceDir) { $SourceDir = Join-Path $root 'dist' }

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host '失败：请以管理员身份运行本脚本（或直接双击 release\NetLock-Teacher-Setup-*.exe）。' -ForegroundColor Red
    exit 2
}
if (-not (Test-Path -LiteralPath (Join-Path $SourceDir 'NetLock.Teacher.exe'))) {
    Write-Host ('先跑 scripts\build.ps1 生成 dist\ 并拷到本机。当前找不到：' + $SourceDir) -ForegroundColor Red
    exit 1
}

Write-Host '① 复制程序…' -ForegroundColor Cyan
[IO.Directory]::CreateDirectory($InstallDir) | Out-Null
Copy-Item -Path (Join-Path $SourceDir '*') -Destination $InstallDir -Recurse -Force

Write-Host '② 调教师端自己的 --install（URLACL + 防火墙 + 登录自启）…' -ForegroundColor Cyan
& (Join-Path $InstallDir 'NetLock.Teacher.exe') --install --port $Port
exit $LASTEXITCODE
