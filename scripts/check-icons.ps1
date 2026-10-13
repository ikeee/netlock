#Requires -Version 5.1
<#
  图标闸门：把"图标没被人动过 / exe 自带对的底色"变成一条命令（构建与 CI 都会跑）。
    ① 深检两个 .ico：帧尺寸、四角底色、从**源图**重算 256 帧逐字节比对（_tools\verify-icons.py，需要 Pillow）
    ② 查 exe **自己**的图标资源（System.Drawing 从 exe 取）：dist\ 两个 exe 必须自带对的底色；
       -IncludeRelease 时再查 release\NetLock-*-Setup-v<版本>.exe（打完安装包跑）
  用法：
    powershell -File scripts\check-icons.ps1
    powershell -File scripts\check-icons.ps1 -IncludeRelease
  退出码：0 = 通过。
#>
[CmdletBinding()]
param(
    [string]$Dist = '',
    [switch]$IncludeRelease,
    [switch]$SkipDeep
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Dist) { $Dist = Join-Path $root 'dist' }
$release = Join-Path $root 'release'
$failed = 0

# ---------- ① 两个 .ico 的深检 ----------
if (-not $SkipDeep) {
    Write-Host '== 图标深检（帧 / 底色 / 从源图重算逐字节比对）==' -ForegroundColor Cyan
    $pyExe = $null
    $pyArgs = @()
    foreach ($c in @(@('py', '-3'), @('python'))) {
        if (-not (Get-Command $c[0] -ErrorAction SilentlyContinue)) { continue }
        $pre = @()
        if ($c.Count -gt 1) { $pre = $c[1..($c.Count - 1)] }
        & $c[0] @pre -c "import PIL.Image" 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { $pyExe = $c[0]; $pyArgs = $pre; break }
    }
    if (-not $pyExe) {
        Write-Host '  ❌ 找不到带 Pillow 的 Python（深检需要，不能静默跳过）' -ForegroundColor Red
        Write-Host '     装一下：py -3 -m pip install Pillow' -ForegroundColor DarkGray
        $failed++
    } else {
        & $pyExe @pyArgs -X utf8 (Join-Path $root '_tools\verify-icons.py')
        if ($LASTEXITCODE -ne 0) { $failed++ }
    }
}

# ---------- ② exe 自带的图标资源 ----------
Add-Type -AssemblyName System.Drawing
function Check-ExeIcon([string]$exe, [string]$expect, [int]$r, [int]$g, [int]$b) {
    if (-not (Test-Path -LiteralPath $exe)) { Write-Host ('  ❌ 缺少：' + $exe); return $false }
    $ico = [System.Drawing.Icon]::ExtractAssociatedIcon($exe)
    if ($null -eq $ico) { Write-Host ('  ❌ 取不到图标：' + $exe); return $false }
    $bmp = $ico.ToBitmap()
    try {
        # 32x32 帧的四角应是底色，中心应不是（人物）
        $c1 = $bmp.GetPixel(0, 0)
        $c2 = $bmp.GetPixel($bmp.Width - 1, $bmp.Height - 1)
        $ct = $bmp.GetPixel([int]($bmp.Width / 2), [int]($bmp.Height / 2))
        $ok = ([Math]::Abs($c1.R - $r) -le 4 -and [Math]::Abs($c1.G - $g) -le 4 -and [Math]::Abs($c1.B - $b) -le 4) -and
              ([Math]::Abs($c2.R - $r) -le 4 -and [Math]::Abs($c2.G - $g) -le 4 -and [Math]::Abs($c2.B - $b) -le 4)
        Write-Host ('  ' + (Split-Path $exe -Leaf).PadRight(34) + $expect +
                    '  左上(' + $c1.R + ',' + $c1.G + ',' + $c1.B + ')  中心(' + $ct.R + ',' + $ct.G + ',' + $ct.B + ')  ' +
                    $(if ($ok) { '✅' } else { '❌' }))
        return $ok
    } finally {
        $bmp.Dispose(); $ico.Dispose()
    }
}

Write-Host '== exe 自带图标（教师端黑底 / 学生端红底）==' -ForegroundColor Cyan
$ok = $true
$ok = (Check-ExeIcon (Join-Path $Dist 'NetLock.Teacher.exe') '教师端 黑底 #000000' 0 0 0) -and $ok
$ok = (Check-ExeIcon (Join-Path $Dist 'NetLock.Student.exe') '学生端 红底 #C62828' 198 40 40) -and $ok

if ($IncludeRelease) {
    $props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw -Encoding UTF8
    $version = [regex]::Match($props, '<Version>([^<]+)</Version>').Groups[1].Value.Trim()
    $t = Join-Path $release ('NetLock-Teacher-Setup-v' + $version + '.exe')
    $s = Join-Path $release ('NetLock-Student-Setup-v' + $version + '.exe')
    foreach ($p in @($t, $s)) {
        if (-not (Test-Path -LiteralPath $p)) { Write-Host ('  ❌ 缺少安装包：' + $p); $ok = $false }
    }
    if ($ok) {
        $ok = (Check-ExeIcon $t ('教师安装包 黑底（v' + $version + '）') 0 0 0) -and $ok
        $ok = (Check-ExeIcon $s ('学生安装包 红底（v' + $version + '）') 198 40 40) -and $ok
    }
}
if (-not $ok) { $failed++ }

Write-Host ''
if ($failed -gt 0) {
    Write-Host ('图标检查失败 ❌（' + $failed + ' 组）') -ForegroundColor Red
    exit 1
}
Write-Host '图标检查通过 ✅' -ForegroundColor Green
exit 0
