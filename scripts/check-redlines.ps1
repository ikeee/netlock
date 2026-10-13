#Requires -Version 5.1
<#
  红线检查（把"这个项目绝不做的事"变成可执行断言）

  背景（老师给的真实约束）：学生机要用 **USB WiFi 做热点给机器人连**，所以：
    · **不能屏蔽 USB**（不做 usbstor / RemovableStorageDevices / AllowStorage 那一套）；
    · **不能"检测到 IP 被改就还原"**（不做 IP/网关还原，也不禁用网卡）。
  这些能力在完整版 LabGuard 里是有的（那是它的产品定位），但**本项目不需要**，而且会破坏机器人的热点连接。
  所以在这里钉成检查：谁（人或 AI）以后把它们加回来，构建就会失败，必须显式改这份清单才能通过。

  用法：powershell -File scripts\check-redlines.ps1
  退出码：0 = 干净；1 = 命中红线。
#>
[CmdletBinding()]
param()
$root = Split-Path -Parent $PSScriptRoot

$rules = @(
    @{ name = '屏蔽 USB 存储（usbstor / 可移动存储策略）'; rx = 'usbstor|USBSTOR|RemovableStorageDevices|AllowStorage|USB 存储|UsbGuard|Usb\.Enabled' },
    @{ name = '还原/设置 IP 地址或网关'; rx = 'interface ip set address|set address name=|RestoreOriginalIp|Restore\(baseLine' },
    @{ name = '启用/禁用网卡'; rx = 'netsh interface set interface|Disable-NetAdapter|Enable-NetAdapter' },
    @{ name = '操作本程序之外的服务'; rx = 'Run\("sc\.exe"'; allowIfContains = 'ServiceName|NetLockSvc' },
    @{ name = '写注册表'; rx = 'Registry\.LocalMachine|Registry\.CurrentUser|RegistryKey\.OpenBaseKey|CreateSubKey' },
    @{ name = '去动无线/蓝牙（本项目只看有线）'; rx = 'WlanSvc|netsh wlan|netsh interface ip .*name="WLAN' }
)

$files = Get-ChildItem -LiteralPath (Join-Path $root 'src') -Recurse -File -Include *.cs
$bad = New-Object System.Collections.ArrayList
foreach ($f in $files) {
    $text = Get-Content -LiteralPath $f.FullName -Raw -Encoding UTF8
    $lines = $text -split "`r?`n"
    foreach ($rule in $rules) {
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match $rule.rx) {
                # 允许注释里讨论（避免"我是来禁止它的"这句话自己被判违规）：只看非注释行
                $trim = $lines[$i].TrimStart()
                if ($trim.StartsWith('//') -or $trim.StartsWith('///') -or $trim.StartsWith('*')) { continue }
                # 例外：命中允许串的（例如操作**我们自己的**服务 NetLockSvc / 变量 ServiceName）不算违规
                if ($rule.ContainsKey('allowIfContains') -and $lines[$i] -match $rule.allowIfContains) { continue }
                [void]$bad.Add(('{0}:{1}  【{2}】  {3}' -f $f.Name, ($i + 1), $rule.name, $lines[$i].Trim()))
            }
        }
    }
}

if ($bad.Count -eq 0) {
    Write-Host '红线检查通过：不屏蔽 USB、不还原 IP/网关、不禁用网卡、不写注册表、不动无线。' -ForegroundColor Green
    exit 0
}
Write-Host '红线被触碰（本项目**故意不做**这些事）：' -ForegroundColor Red
$bad | ForEach-Object { Write-Host ('  ' + $_) }
Write-Host ''
Write-Host '如果确实需要其中某一条，先改 scripts\check-redlines.ps1 里的清单（= 一次有意识的决定），再动代码。' -ForegroundColor Yellow
exit 1
