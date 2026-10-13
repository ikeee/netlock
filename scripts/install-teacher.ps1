#Requires -Version 5.1
<#
  教师机一键部署（**需要在教师机上以管理员身份运行**）：
    ① 把程序拷到 C:\Program Files\NetLockTeacher
    ② 给 http://+:8210/ 加 URLACL（否则 HttpListener 绑不上通配前缀，学生机连不上）
    ③ 放行防火墙 TCP 8210
    ④ 建一个"登录时启动"的计划任务（老师登录后自动起托盘）
  用法：powershell -ExecutionPolicy Bypass -File install-teacher.ps1 [-Port 8210] [-SourceDir ..\dist]
#>
[CmdletBinding()]
param([int]$Port = 8210, [string]$SourceDir, [string]$InstallDir = 'C:\Program Files\NetLockTeacher')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $SourceDir) { $SourceDir = Join-Path $root 'dist' }

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host '失败：请以管理员身份运行本脚本。' -ForegroundColor Red
    exit 2
}
if (-not (Test-Path -LiteralPath (Join-Path $SourceDir 'NetLock.Teacher.exe'))) {
    Write-Host ('先在开发机上跑 scripts\build.ps1，并把 dist\ 整个目录拷过来。当前找不到：' + $SourceDir) -ForegroundColor Red
    exit 1
}

Write-Host '① 复制程序…' -ForegroundColor Cyan
[IO.Directory]::CreateDirectory($InstallDir) | Out-Null
Copy-Item -Path (Join-Path $SourceDir '*') -Destination $InstallDir -Recurse -Force

Write-Host '② 加 URLACL（http://+:' + $Port + '/）…' -ForegroundColor Cyan
& netsh http delete urlacl url=("http://+:" + $Port + "/") 2>&1 | Out-Null
$acl = & netsh http add urlacl url=("http://+:" + $Port + "/") user=Everyone 2>&1
Write-Host ('   ' + ($acl -join ' '))

Write-Host '③ 放行防火墙 TCP ' + $Port + '…' -ForegroundColor Cyan
& netsh advfirewall firewall delete rule name="NetLock Teacher" 2>&1 | Out-Null
& netsh advfirewall firewall add rule name="NetLock Teacher" dir=in action=allow protocol=TCP localport=$Port | Out-Null

Write-Host '④ 建登录自启动计划任务…' -ForegroundColor Cyan
$exe = Join-Path $InstallDir 'NetLock.Teacher.exe'
& schtasks /delete /tn "NetLock\Teacher" /f 2>&1 | Out-Null
$tr = '"' + $exe + '"'
& schtasks /create /tn "NetLock\Teacher" /tr $tr /sc onlogon /rl highest /f | Out-Null
& schtasks /run /tn "NetLock\Teacher" | Out-Null

Write-Host ''
Write-Host '完成。接下来：' -ForegroundColor Green
Write-Host '  1) 右下角托盘出现 NetLock 图标 → 打开设置 → 勾好「拔网线显示遮罩」、填 DNS、**设置解遮罩口令** → 保存并下发'
Write-Host '  2) 点「导出配对文件…」，把 netlock-pairing.json 拷到 U 盘 → 每台学生机装的时候用它（--pair）'
Write-Host ('  3) 程序目录：' + $InstallDir + '    监听：http://+:' + $Port + '/')
