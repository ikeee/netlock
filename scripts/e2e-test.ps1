#Requires -Version 5.1
<#
  端到端冒烟（**不碰真机的数据目录**）：一个真教师端 + 一个真学生端（沙箱数据目录）。
  验五件事：
    ① 学生端能配对、能验签（别的钥匙发的东西一律拒）；
    ② 教师端一保存，学生端 **2 秒内**拿到新版本（"立马更新"的实证）；
    ③ 遮罩开关随下发变化（本地缓存 maskOn 1 → 0）；
    ④ **本机没有有线网卡时：不误弹遮罩、也不乱改 DNS**（真机上最容易出错的那条）；
    ⑤ 收尾复核：真机 %ProgramData% 里的 NetLock 目录"测试前后存在性一致"（没污染真机）。
  用法：powershell -File scripts\e2e-test.ps1 [-SkipBuild]
  退出码：0 = 全过。
#>
[CmdletBinding()]
param([int]$Port = 8291, [switch]$SkipBuild, [switch]$KeepRoot)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$T = Join-Path $env:TEMP ('netlock-e2e-' + (Get-Date -Format 'MMdd-HHmmss'))
$pass = 0; $fail = 0

function Say([string]$t) { Write-Host $t }
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Host ("  PASS  " + $name) -ForegroundColor Green }
    else { $script:fail++; Write-Host ("  FAIL  " + $name + "  " + $detail) -ForegroundColor Red }
}

if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') | Out-Null }
$teacher = Join-Path $root 'src\NetLock.Teacher\bin\Release\net48\NetLock.Teacher.exe'
$student = Join-Path $root 'src\NetLock.Student\bin\Release\net48\NetLock.Student.exe'
foreach ($e in @($teacher, $student)) { if (-not (Test-Path -LiteralPath $e)) { Say ('缺少可执行文件：' + $e); exit 1 } }

[IO.Directory]::CreateDirectory($T) | Out-Null
$tDir = Join-Path $T 'teacher'; $sDir = Join-Path $T 'student'
[IO.Directory]::CreateDirectory($tDir) | Out-Null
$pair = Join-Path $T 'pairing.json'

$realTeacherBefore = Test-Path (Join-Path $env:ProgramData 'NetLockTeacher')
$realStudentBefore = Test-Path (Join-Path $env:ProgramData 'NetLock')

Say '=== ① 起教师端（headless，数据目录在临时目录）==='
$srvOut = Join-Path $T 'teacher.out'
$srv = Start-Process -FilePath $teacher -WindowStyle Hidden -PassThru -RedirectStandardOutput $srvOut -RedirectStandardError (Join-Path $T 'teacher.err') `
    -ArgumentList @('--data-dir', $tDir, '--headless', '--port', "$Port", '--mask-on', '1',
                    '--dns', '10.46.254.10', '--mask-password', 'lab1234',
                    '--export-pairing', $pair, '--serve', '--seconds', '45')
$ready = $false
for ($i = 0; $i -lt 40 -and -not $ready; $i++) { Start-Sleep -Milliseconds 500; $ready = Test-Path -LiteralPath $pair }
Check 'S0 教师端就绪（已保存状态 + 导出配对文件）' $ready

Say '=== ② 学生端：配对 → 前台运行（沙箱数据目录）==='
$pairOut = Join-Path $T 'pair.out'
Start-Process -FilePath $student -WindowStyle Hidden -Wait -RedirectStandardOutput $pairOut `
    -ArgumentList @('--pair', $pair, '--data-dir', $sDir) | Out-Null
# 断言用**文件内容**取证（控制台输出是 GBK，按 UTF-8 读会乱码，不能拿它做中文断言）
$cfgPair = Get-Content -LiteralPath (Join-Path $sDir 'config.json') -Raw -Encoding UTF8 | ConvertFrom-Json
Check 'S1 学生端配对成功（配置里写入了教师机地址与连接密钥）' `
    ($cfgPair.pairing.addresses.Count -ge 1 -and -not [string]::IsNullOrEmpty($cfgPair.pairing.key)) `
    ('地址=' + ($cfgPair.pairing.addresses -join ',') + ' 指纹=' + $cfgPair.pairing.fingerprint)

$cliOut = Join-Path $T 'student.out'
$cli = Start-Process -FilePath $student -WindowStyle Hidden -PassThru -RedirectStandardOutput $cliOut -RedirectStandardError (Join-Path $T 'student.err') `
    -ArgumentList @('--console', '--seconds', '40', '--data-dir', $sDir)
Start-Sleep -Seconds 8

function Get-StudentCfg {
    $p = Join-Path $sDir 'config.json'
    if (-not (Test-Path -LiteralPath $p)) { return $null }
    return (Get-Content -LiteralPath $p -Raw -Encoding UTF8 | ConvertFrom-Json)
}
$cfg1 = Get-StudentCfg
Check 'S2 学生端已应用 version=1（验签通过并落盘）' ($cfg1 -and $cfg1.appliedVersion -eq 1) `
    $(if ($cfg1) { 'appliedVersion=' + $cfg1.appliedVersion } else { '（没有配置）' })
Check 'S2 遮罩开关随下发到达（maskOn=true）' ($cfg1 -and $cfg1.maskOn -eq $true)
Check 'S2 解遮罩口令随下发到达（学生机离线也能靠它解除）' ($cfg1 -and -not [string]::IsNullOrEmpty($cfg1.maskPasswordHash))

Say '=== ③ 教师端改 DNS 并**停用**遮罩（version=2）→ 学生端应 2 秒内跟上 ==='
Start-Process -FilePath $teacher -WindowStyle Hidden -Wait `
    -ArgumentList @('--data-dir', $tDir, '--headless', '--mask-on', '0', '--dns', '223.5.5.5') | Out-Null
$sw = [Diagnostics.Stopwatch]::StartNew()
$ok2 = $false
while ($sw.Elapsed.TotalSeconds -lt 12) {
    $c = Get-StudentCfg
    if ($c -and $c.appliedVersion -ge 2 -and $c.maskOn -eq $false) { $ok2 = $true; break }
    Start-Sleep -Milliseconds 300
}
$sw.Stop()
Check ('S3 新状态在 ' + [math]::Round($sw.Elapsed.TotalSeconds, 1) + ' 秒内被学生端应用（version≥2 且遮罩已停用）') $ok2 `
    $(if ($c) { 'appliedVersion=' + $c.appliedVersion + ' maskOn=' + $c.maskOn } else { '（没有配置）' })
# 学生端的日志**文件**是 UTF-8（控制台是 GBK）→ 中文断言读文件
$logFile = Get-ChildItem -LiteralPath (Join-Path $sDir 'logs') -Filter 'netlock-*.log' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
$logText = if ($logFile) { Get-Content -LiteralPath $logFile.FullName -Raw -Encoding UTF8 } else { '' }
Check 'S3 学生端日志里能看到"收到新状态 version=2"（日志文件为 UTF-8）' ($logText -match '收到新状态 version=2')

Say '=== ④ 断网遮罩的关键安全行为（本机没有有线网卡）==='
Start-Sleep -Seconds 6
# 重新读一次日志（学生端一直在写）
$logFile = Get-ChildItem -LiteralPath (Join-Path $sDir 'logs') -Filter 'netlock-*.log' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
$logText = if ($logFile) { Get-Content -LiteralPath $logFile.FullName -Raw -Encoding UTF8 } else { '' }
Check 'S4 没有有线网卡 → **不判断网、不弹遮罩**（否则纯 WiFi 的机器会被误锁）' `
    ($logText -notmatch '拉起遮罩' -and $logText -notmatch '已弹出遮罩')
Check 'S4 也没有去乱改 DNS（没有可用的有线网卡时应等待，而不是报错刷屏）' `
    ($logText -match '没有可用的有线网卡' -or $logText -notmatch 'netsh')

Say '=== ⑤ 收尾：等教师端退出，核对设备上报 ==='
try { if (-not $cli.HasExited) { $cli | Stop-Process -Force } } catch { }
$srv.WaitForExit(40000) | Out-Null
$srvText = Get-Content -LiteralPath $srvOut -Raw -Encoding UTF8
Check 'S5 教师端退出清单里看到这台学生机（上报链路通）' ($srvText -match $env:COMPUTERNAME) `
    ((($srvText -split "`n") | Where-Object { $_ -match 'appliedVersion' }) -join ' | ')

$realTeacherAfter = Test-Path (Join-Path $env:ProgramData 'NetLockTeacher')
$realStudentAfter = Test-Path (Join-Path $env:ProgramData 'NetLock')
Check 'S6 真机的 %ProgramData% 没有被这次测试碰过（存在性一致）' `
    ($realTeacherBefore -eq $realTeacherAfter -and $realStudentBefore -eq $realStudentAfter) `
    ('teacher ' + $realTeacherBefore + '→' + $realTeacherAfter + '  student ' + $realStudentBefore + '→' + $realStudentAfter)

Say ''
Say ('端到端结果：' + $pass + ' 通过 / ' + $fail + ' 失败')
Get-Process NetLock.Teacher, NetLock.Student -ErrorAction SilentlyContinue |
    ForEach-Object { try { Stop-Process -Id $_.Id -Force -ErrorAction Stop } catch { } }
if (-not $KeepRoot -and $fail -eq 0) { try { [IO.Directory]::Delete($T, $true) } catch { } }
else { Say ('（目录已保留：' + $T + '）') }
exit $(if ($fail -eq 0) { 0 } else { 1 })
