#Requires -Version 5.1
<#
  老师后路：**一键收干净**（教师机/学生机都能用；**安全模式里也能跑**）。
  做什么：停服务 → 删服务 → 删遮罩计划任务 → 结束本程序进程 →（可选）把有线网卡 DNS 还原为自动获取 →（可选）删数据目录。
  不做：不动系统其它设置（这个程序本来也只写网卡 DNS 和那两个计划任务/服务）。
  用法：
    powershell -ExecutionPolicy Bypass -File cleanup-all.ps1            # 只收程序，保留数据目录
    powershell -ExecutionPolicy Bypass -File cleanup-all.ps1 -RestoreDns -PurgeData
#>
[CmdletBinding()]
param([switch]$RestoreDns, [switch]$PurgeData)
$ErrorActionPreference = 'Continue'
function Step([string]$desc, [scriptblock]$action) {
    Write-Host ('  · ' + $desc)
    try { & $action } catch { Write-Host ('    ⚠ ' + $_.Exception.Message) -ForegroundColor Yellow }
}

Write-Host '=== NetLock 收尾（老师后路）===' -ForegroundColor Cyan
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host '提示：当前不是管理员，删除服务和计划任务可能失败。' -ForegroundColor Yellow
}

Step '结束本程序进程（遮罩/学生端/教师端）' {
    Get-Process NetLock.Student, NetLock.Teacher -ErrorAction SilentlyContinue |
        ForEach-Object { try { Stop-Process -Id $_.Id -Force -ErrorAction Stop } catch { } }
}
Step '停止并删除服务 NetLockSvc' {
    & sc.exe stop NetLockSvc 2>&1 | Out-Null
    Start-Sleep -Milliseconds 500
    & sc.exe delete NetLockSvc 2>&1 | Out-Null
}
Step '删除遮罩计划任务 NetLock\Mask' { & schtasks.exe /delete /tn "NetLock\Mask" /f 2>&1 | Out-Null }
Step '删除教师端自启动任务 NetLock\Teacher' { & schtasks.exe /delete /tn "NetLock\Teacher" /f 2>&1 | Out-Null }
Step '删除教师端 URLACL / 防火墙规则' {
    & netsh http delete urlacl url=http://+:8210/ 2>&1 | Out-Null
    & netsh advfirewall firewall delete rule name="NetLock Teacher" 2>&1 | Out-Null
}
if ($RestoreDns) {
    Step '把**有线网卡** DNS 还原为自动获取' {
        foreach ($n in (Get-NetAdapter -Physical -ErrorAction SilentlyContinue |
                        Where-Object { $_.InterfaceType -eq 'Ethernet' })) {
            & netsh interface ip set dns name="$($n.Name)" source=dhcp 2>&1 | Out-Null
            Write-Host ('    ' + $n.Name + ' → 自动获取')
        }
        & ipconfig /flushdns | Out-Null
    }
}
if ($PurgeData) {
    Step '删除数据目录（含连接密钥）' {
        foreach ($d in @((Join-Path $env:ProgramData 'NetLock'), (Join-Path $env:ProgramData 'NetLockTeacher'))) {
            if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction Stop; Write-Host ('    已删 ' + $d) }
        }
    }
}
Write-Host ''
Write-Host '收尾完成。若还有残留：检查「服务」里有没有 NetLockSvc、「任务计划程序」里有没有 NetLock 文件夹。' -ForegroundColor Green
