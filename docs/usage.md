# 现场使用与排障

## 一、部署顺序（照这个来，别跳）

```text
1) 教师机（管理员）：双击 NetLock-Teacher-Setup-*.exe → 开始安装
   （命令行等价：powershell -ExecutionPolicy Bypass -File install-teacher.ps1）
   托盘 → 打开设置 → 勾「拔网线显示遮罩」+ 填 DNS + **设置解遮罩口令** → 保存并下发
   →「导出配对文件…」→ netlock-pairing.json（拷到 U 盘）
2) 每台学生机（管理员）：双击 NetLock-Student-Setup-*.exe → 选配对文件 → 开始安装
   （批量无人值守：NetLock-Student-Setup-v*.exe --silent --role student --pair D:\netlock-pairing.json）
3) 核对（任一台学生机）：
   NetLock.Student.exe --status
   → 「已配对 = True」且指纹 = 教师机上显示的密钥指纹；「已应用版本」应等于教师端当前的 version
```

## 二、验收（3 分钟，一台学生机即可）

| 步骤 | 期望 |
|---|---|
| 教师端改 DNS → 保存 | 学生机 **2 秒内**网卡 DNS 变成新值（`ipconfig /all` 看 DNS 服务器） |
| 拔掉学生机网线 | **5 秒左右**出现全屏遮罩，显示"网络已断开"；教师端「学生机」页那台显示「遮罩=是」 |
| 插回网线 | 遮罩在 **2 秒内**自动消失 |
| 在遮罩上输入老师口令 → 点解除 | 遮罩消失；**本次断网期间不再弹**（再拔一次才弹） |
| 教师端把「拔网线时显示遮罩」取消 → 保存 | 之后拔网线**不再**弹遮罩（学生机 2 秒内收到） |

## 三、排障

### 遮罩不出现

1. 这台学生机有没有**有线网卡**？本项目**只用有线当判据**。纯 WiFi / 只有蓝牙的机器**不会**弹（这是设计，防止误锁）。
2. `NetLock.Student.exe --status` 看：已配对？已应用版本？最近错误？
3. 服务在跑吗：`Get-Service NetLockSvc`；看日志 `%ProgramData%\NetLock\logs\netlock-*.log`。
4. 遮罩由计划任务拉进当前登录会话：`schtasks /query /tn "NetLock\Mask"` 应存在；`schtasks /run /tn "NetLock\Mask" /i` 手工试试。

### 遮罩关不掉

- 输入**老师口令**（在教师端设置里设的那个）→ 点「解除」。
- 忘口令：跑 `cleanup-all.ps1`（安全模式里也能跑），或进**安全模式**（本程序按设计在安全模式不加载）。

### DNS 没变

1. 只改**有线且已启用**的网卡；无线不动。
2. 学生机 `--status` 看「已应用版本」是否等于教师端 version —— 不等说明没收到（查网络/端口/配对）。
3. 教师机端确认：监听是否成功（设置窗底栏有 `监听 http://+:8210/`）；防火墙是否放行（install-teacher 会加规则）。
4. 想还原为自动获取：`NetLock.Student.exe --uninstall --restore-dns`，或 `cleanup-all.ps1 -RestoreDns`。

### 学生机能自己关掉它吗？

**能**（这一版不防拆）：停服务、杀进程、甚至改注册表都行。我们只保证：
① 服务开机自启；② 遮罩被关掉后 5 秒内会被重新拉起；③ 配对密钥学生读不到（数据目录 ACL 只给管理员/SYSTEM）。
要"学生关不掉"的能力，用完整版 LabGuard。

## 四、卸载（三档，按需要选）

```powershell
# 学生机：卸服务与任务，DNS 还原为自动获取，保留数据目录
NetLock.Student.exe --uninstall --restore-dns

# 教师机/学生机通用：一键收干净（可加 -RestoreDns -PurgeData）
powershell -ExecutionPolicy Bypass -File cleanup-all.ps1

# 彻底删数据（含连接密钥）
powershell -ExecutionPolicy Bypass -File cleanup-all.ps1 -RestoreDns -PurgeData
```
