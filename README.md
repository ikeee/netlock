# NetLock（断网遮罩 + DNS 统管）

一个**只做两件事**的机房小工具，一个仓库含教师端与学生端：

1. **有线网卡被拔网线（或被禁用/没拿到 IP）→ 学生机弹全屏遮罩**；教师机可以**启用/停用**这个功能。
   插回网线自动消失；老师可用口令在遮罩上解除（本次断网不再弹）。
2. **教师机下发 DNS → 学生机收到后立刻更新**（实测 0.6 秒内生效；只改**有线**网卡，不动无线/热点）。

> 这是 LabGuard（`ikeee/labguard`，13 个 Guard、122 个开关的完整版）的**精减版**：
> 只保留上面两件事 + 让老师能收回控制权的最小后路。**没有**防拆加固/隐藏/自愈/进程拦截/USB/浏览器/注册表加固，
> 因此**不防学生停服务或杀进程** —— 需要那些能力请用完整版 LabGuard。

## 为什么这么小

| 老版本的负担 | 这一版的做法 |
|---|---|
| 122 个开关、老师找不到 | **只有 3 个输入**：遮罩开关、DNS 列表、解遮罩口令 |
| 分组 / 临时课堂状态 / 审计 / 报表 | 不做（要那些用完整版） |
| 通信要公钥签名 + 配对 pin | **一把共享密钥**（配对文件带走）：够挡住"同网段有人冒充教师机发 DNS" |

## 架构（两个 exe）

| 组件 | 说明 |
|---|---|
| `NetLock.Teacher.exe` | 教师端：托盘 + 设置窗（两页：设置 / 学生机）+ 内置 HTTP 服务（`http://+:8210/`） |
| `NetLock.Student.exe` | 学生端：**同一个 exe 多种模式** —— 服务（`--service`，开机自启）/ 遮罩（`--mask`，由服务拉进当前登录会话）/ 前台调试（`--console`）/ 安装卸载（`--install` `--uninstall`）/ 状态（`--status`） |
| `NetLock.Shared.dll` | 两端共用：状态信封（HMAC 签名 + 版本单调防重放）、DNS 解析、有线网卡判定、口令散列 |

技术选型：**C# / .NET Framework 4.8（net48）**。理由：Win11 **自带** .NET Framework 4.8.1 → 学生机**零安装运行时**；
而 .NET 8 需要每台机装 Desktop Runtime（或自包含 70MB/exe）。老框架写现代 C#（`LangVersion latest`）没问题。

## 快速部署

### 方式一：双击安装包（推荐，老师用这个）

| 机器 | 安装包 | 说明 |
|---|---|---|
| 教师机 | `NetLock-Teacher-Setup-v0.1.1.exe`（黑底图标） | 右键 → 以管理员身份运行 → 点「开始安装」：拷程序、放行 `http://+:8210/` 与防火墙、建登录自启 |
| 学生机 | `NetLock-Student-Setup-v0.1.1.exe`（红底图标） | 右键 → 以管理员身份运行 → 选教师机导出的**配对文件** → 点「开始安装」：注册服务 + 遮罩任务 + 权限 + 配对 |

**50 台批量（无人值守）**：

```bat
NetLock-Student-Setup-v0.1.1.exe --silent --role student --pair D:\netlock-pairing.json
NetLock-Teacher-Setup-v0.1.1.exe  --silent --role teacher --port 8210
```

安装包是**单文件**（把程序包内嵌在里面，约 290 KB），运行时解压到临时目录再调对应 exe 的 `--install`；
`*.exe --selftest` 可以自己验证"内嵌程序包完整"。

### 方式二：命令行脚本（不改动安装包时更灵活）

```powershell
# 开发机：构建（产物进 dist\）
powershell -File scripts\build.ps1
# 开发机：打两个安装包（产物进 release\，并自检内嵌程序包）
powershell -File scripts\build-installer.ps1 -SkipBuild
# 教师机（把 dist\ 拷过去，然后管理员运行）：
powershell -ExecutionPolicy Bypass -File install-teacher.ps1
```

托盘图标 →「打开设置」→ 勾好**拔网线显示遮罩**、填 **DNS**、**设置解遮罩口令**（务必设！）→ **保存并下发**
→「导出配对文件…」拿到 `netlock-pairing.json`（**等于钥匙，别乱放**）。

### 学生机（每台约 1 分钟）

```powershell
powershell -ExecutionPolicy Bypass -File install-student.ps1 -Pair D:\netlock-pairing.json
```

它会：拷到 `C:\Program Files\NetLock` → 注册服务（开机自启）→ 建"遮罩"计划任务（按需拉起，跑在当前登录会话）
→ 收紧数据目录权限（**学生读不到连接密钥**）→ 导入配对。

验收（就这么简单）：**在教师机上点保存 → 到学生机上看两件事**：① 网卡 DNS 变了；② 拔掉网线 → 全屏遮罩，插回 → 消失。

## 老师后路（务必知道）

| 情况 | 怎么做 |
|---|---|
| 遮罩上要解除 | 在遮罩窗口输入**老师口令** → 本次断网不再弹（网络恢复后再拔会重新弹） |
| 忘了口令 / 装错了 / 要退役 | 跑 `cleanup-all.ps1`（**安全模式里也能跑**）：停服务、删服务、删任务、结束进程；`-RestoreDns` 顺手把有线网卡 DNS 还原为自动获取，`-PurgeData` 清数据目录 |
| 单机不再受控 | 服务按设计在**安全模式不加载**——进安全模式就能彻底处理 |

## 明确不做（避免误解）

- **不锁键鼠**：遮罩靠"全屏 + 每 500ms 置顶"占住屏幕，学生按 Win 键仍能切走。老版本的 `BlockInput` 需要更高权限且容易被杀，这一版不做。
- **不防拆**：学生可以停服务/杀进程（下次开机服务会自启）。
- **不记屏幕/不远程控制**：本项目永久不做远程桌面、屏幕监控、截屏录屏、文件传输、聊天。
- **不屏蔽 USB**：**一条 USB 相关代码都没有**（学生机要用 USB WiFi 做热点给机器人连；机器人/串口/键鼠同理）。
  完整版 LabGuard 有 USB 存储管控开关，本项目故意不做。
- **不改 / 不还原 IP 与网关**：只"判断网 + 写 DNS"，**绝不**做"检测到 IP 被改就还原" —— 那会打断 ICS/移动热点/机器人连接。
  也**不禁用网卡**（禁用 USB WiFi 热点就废了）。
- **不去动无线/蓝牙网卡**：只认有线（拔网线才是判据），DNS 也只写有线网卡。
  **蓝牙 PAN 在 Windows 里被归类成 Ethernet**，已按描述关键字排除（否则只有 WiFi 的机器会误弹遮罩）。

> 上面这几条**不是口头承诺**：`scripts\check-redlines.ps1` 会扫源码把它们钉住（构建/CI 都会跑）。
> 谁（人或 AI）以后想把它们加回来，构建直接失败，必须显式修改那份清单 —— 也就是**一次有意识的决定**。

### 红线清单（`scripts/check-redlines.ps1`）

| 红线 | 判据（扫源码） |
|---|---|
| 不屏蔽 USB 存储 | `usbstor` / `RemovableStorageDevices` / `AllowStorage` / `UsbGuard` |
| 不还原/设置 IP 与网关 | `interface ip set address` / `RestoreOriginalIp` |
| 不禁用网卡 | `netsh interface set interface` / `Disable-NetAdapter` |
| 不操作别人的服务 | `sc.exe stop/start/delete`（只允许操作 `NetLockSvc`） |
| 不写注册表 | `Registry.LocalMachine` / `CreateSubKey` |
| 不动无线/蓝牙 | `WlanSvc` / `netsh wlan` |

## 开发

```powershell
powershell -File scripts\build.ps1        # 构建 + 自检（39 项纯逻辑断言）+ 产物进 dist\
powershell -File scripts\e2e-test.ps1     # 端到端：真教师端 + 真学生端（沙箱数据目录）11 项断言
powershell -File scripts\build-installer.ps1 -SkipBuild   # 打两个安装包到 release\（并自检内嵌程序包）

# 换图标（人物像素一个都不动，只改底色）：
py -3 _tools\make_app_icon.py --bg "#000000" --out src\Assets\teacher-black.ico   # 教师端：黑底
py -3 _tools\make_app_icon.py --bg "#C62828" --out src\Assets\student-red.ico   # 学生端：红底
```

协议与实现细节见 [docs/protocol.md](docs/protocol.md)；现场使用与排障见 [docs/usage.md](docs/usage.md)。
