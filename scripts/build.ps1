#Requires -Version 5.1
<#
  构建 + 自检 + 产物整理。
  用法：powershell -File scripts\build.ps1 [-Clean] [-NoSelfTest]
  产物：dist\（教师端 / 学生端 / 共享 dll / 自检 / 脚本 / 文档）
#>
[CmdletBinding()]
param([switch]$Clean, [switch]$NoSelfTest)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'

if ($Clean) {
    Get-ChildItem -LiteralPath (Join-Path $root 'src') -Recurse -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -in @('bin', 'obj') } |
        ForEach-Object { try { [IO.Directory]::Delete($_.FullName, $true) } catch { } }
    if (Test-Path -LiteralPath $dist) { [IO.Directory]::Delete($dist, $true) }
}

Write-Host '== 构建 ==' -ForegroundColor Cyan
& dotnet build (Join-Path $root 'NetLock.sln') -c Release --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw '构建失败' }

if (-not $NoSelfTest) {
    Write-Host '== 自检 ==' -ForegroundColor Cyan
    & (Join-Path $root 'src\NetLock.SelfTest\bin\Release\net48\NetLock.SelfTest.exe')
    if ($LASTEXITCODE -ne 0) { throw '自检失败' }
}

Write-Host '== 红线检查 ==' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'check-redlines.ps1')
if ($LASTEXITCODE -ne 0) { throw '红线检查失败（见上）' }

Write-Host '== 整理产物 ==' -ForegroundColor Cyan
[IO.Directory]::CreateDirectory($dist) | Out-Null
foreach ($proj in @('NetLock.Teacher', 'NetLock.Student')) {
    $src = Join-Path $root ("src\$proj\bin\Release\net48")
    Get-ChildItem -LiteralPath $src -File |
        Where-Object { $_.Extension -in @('.exe', '.dll', '.config') } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $dist -Force }
}
Copy-Item -LiteralPath (Join-Path $root 'src\NetLock.SelfTest\bin\Release\net48\NetLock.SelfTest.exe') -Destination $dist -Force
Copy-Item -Path (Join-Path $root 'scripts\*') -Destination $dist -Force
Copy-Item -LiteralPath (Join-Path $root 'docs') -Destination $dist -Recurse -Force
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $dist -Force
Get-ChildItem -LiteralPath $dist | Select-Object Name, Length | Format-Table -AutoSize | Out-String | Write-Host
Write-Host ('完成 → ' + $dist) -ForegroundColor Green
