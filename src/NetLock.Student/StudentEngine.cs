using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using NetLock.Shared;

namespace NetLock.Student
{
    /// <summary>
    /// 学生机引擎（**只有两件事**）：
    ///   ① 每 2 秒问教师机要状态（`GET /api/state`，验签 + 只认更大版本）→ DNS 变了**立刻**改网卡 DNS；
    ///   ② 每 2 秒看一次**有线网卡**：全都没网（拔网线/禁用/169.254）且开关为开 → 拉起全屏遮罩；恢复 → 关掉遮罩。
    ///
    /// 明确不做（精减版的取舍）：防拆/隐藏/自愈/进程拦截/USB/浏览器/注册表加固 —— 因此**不防学生停服务或杀进程**，
    /// 需要那类能力请用完整版 LabGuard。
    /// </summary>
    public sealed class StudentEngine : IDisposable
    {
        public const string MaskMutexName = @"Global\NetLockMask";         // 谁持有 = 遮罩正在显示
        public const string MaskExitEventName = @"Global\NetLockMaskExit"; // 服务置位 = 让遮罩退出
        public const string MaskTaskName = @"NetLock\Mask";
        public const string MaskAckFileName = "mask-ack.txt";              // 老师已解除（带 HMAC，防学生伪造）
        private const int PollSeconds = 2;
        private const int DisconnectConfirmSeconds = 5;   // 连续这么多秒没网才判"断网"（避免交换机抖动误报）
        private const int ReportSeconds = 10;

        private readonly StudentConfig _cfg;
        private readonly Action<string> _log;
        private Thread _thread;
        private volatile bool _running;
        private DateTime _disconnectedSince = DateTime.MinValue;
        private string _appliedDnsSig = "";           // 上次真正写进系统的 DNS（避免每 2 秒刷一次）
        private string _lastNic = "";
        private bool _maskShown;
        private DateTime _lastReport = DateTime.MinValue;
        private string _mode = "unknown";             // nic / nomask / offline
        private EventWaitHandle _maskExit;            // 服务创建并持有：置位即"请遮罩退出"
        private Mutex _maskSlot;                      // 服务创建并持有：让遮罩能"打开它"来确认自己在跑

        public StudentEngine(StudentConfig cfg, Action<string> log)
        {
            _cfg = cfg;
            _log = log ?? (_ => { });
        }

        public bool MaskShown { get { return _maskShown; } }
        public string WaitReason { get { return _mode; } }

        public void Start()
        {
            // 跨会话的两个命名对象**必须由服务侧创建**：交互用户默认没有 SeCreateGlobalPrivilege，
            // 自己新建 Global\ 对象会失败；但"打开已存在的"没问题。
            try
            {
                _maskExit = new EventWaitHandle(false, EventResetMode.ManualReset, MaskExitEventName);
                _maskSlot = new Mutex(false, MaskMutexName);
            }
            catch (Exception ex) { _log("创建遮罩信号对象失败（跨会话可能失效）：" + ex.Message); }
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "NetLock.Engine" };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { if (_thread != null && _thread.IsAlive) _thread.Join(3000); } catch { }
            StopMask();
        }

        public void Dispose() { Stop(); }

        private void Loop()
        {
            DateTime nextNet = DateTime.MinValue, nextSync = DateTime.MinValue, nextReport = DateTime.MinValue;
            while (_running)
            {
                DateTime now = DateTime.UtcNow;
                try
                {
                    if (now >= nextNet) { nextNet = now.AddSeconds(PollSeconds); CheckDisconnect(); }
                    if (now >= nextSync) { nextSync = now.AddSeconds(PollSeconds); SyncFromTeacher(); }
                }
                catch (Exception ex) { _log("【异常】" + ex.Message); }
                // 上报（每 10 秒一次）
                if ((now - _lastReport).TotalSeconds >= ReportSeconds)
                {
                    _lastReport = now;
                    try { Report(); } catch { }
                }
                for (int i = 0; i < PollSeconds * 10 && _running; i++) Thread.Sleep(100);
            }
        }

        // ------------------------------------------------------------------ ① 断网 → 遮罩

        /// <summary>断网判定：**只认有线网卡**。没有任何有线网卡时，"无法判定"，不遮罩（避免误伤只有 WiFi 的机器）。</summary>
        private void CheckDisconnect()
        {
            List<NicStatus> nics = WiredNic.Snapshot();
            if (nics.Count == 0)
            {
                _mode = "nic-missing";       // 一个有线网卡都没有 → 不判
                _disconnectedSince = DateTime.MinValue;
                StopMask();
                return;
            }
            var up = nics.Where(n => !n.LooksDown).ToList();
            bool disconnected = up.Count == 0;
            _lastNic = up.Count > 0 ? up[0].Name : nics[0].Name;

            if (!disconnected)
            {
                // 网络恢复：清掉"老师已解除"标记（下次再拔，重新弹），并收掉遮罩
                _disconnectedSince = DateTime.MinValue;
                if (_cfg.MaskAckUtc != DateTime.MinValue)
                {
                    _cfg.MaskAckUtc = DateTime.MinValue;
                    try { _cfg.Save(); } catch { }
                }
                StopMask();
                _mode = "online";
                return;
            }

            if (_disconnectedSince == DateTime.MinValue)
            {
                _disconnectedSince = DateTime.Now;
                _log("检测到有线网卡没网（" + nics[0].Name + "），" + DisconnectConfirmSeconds + " 秒后确认…");
                return;
            }
            if ((DateTime.Now - _disconnectedSince).TotalSeconds < DisconnectConfirmSeconds) return;

            if (!_cfg.MaskOn) { _mode = "mask-off"; StopMask(); return; }       // 老师在教师机上停用了
            if (_cfg.MaskAckUtc != DateTime.MinValue) { _mode = "acked"; StopMask(); return; }  // 老师这次已解除

            _mode = "mask";
            EnsureMask();
        }

        /// <summary>遮罩进程是否在跑（它持有同名 Mutex）。</summary>
        public static bool IsMaskRunning()
        {
            try
            {
                using (Mutex.OpenExisting(MaskMutexName)) return true;
            }
            catch { return false; }
        }

        private void EnsureMask()
        {
            if (IsMaskRunning())
            {
                _maskShown = true;
                return;
            }
            try { _maskExit?.Reset(); } catch { }
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            // 优先用计划任务把遮罩拉进**当前登录会话**（服务跑在 session 0，不能直接显示界面）
            int rc = Run("schtasks.exe", "/run /tn \"" + MaskTaskName + "\" /i");
            if (rc != 0)
            {
                _log("计划任务未就绪（rc=" + rc + "）→ 直接启动遮罩进程（仅 --console 模式有效）");
                try { Process.Start(new ProcessStartInfo(exe, "--mask") { UseShellExecute = false }); } catch (Exception ex) { _log("启动遮罩失败：" + ex.Message); }
            }
            _maskShown = IsMaskRunning();
            if (!_maskShown) _log("遮罩尚未起来（下一次检查会再试）");
        }

        private void StopMask()
        {
            // ⚠ 不能用 taskkill 按镜像名杀：遮罩和服务是**同一个 exe**，按名字杀会把服务一起杀掉。
            // 用命名事件通知遮罩自己退出（跨会话有效）。
            if (!IsMaskRunning()) { _maskShown = false; return; }
            try { _maskExit?.Set(); } catch { }
            for (int i = 0; i < 15 && IsMaskRunning(); i++) Thread.Sleep(100);
            _maskShown = IsMaskRunning();
            if (_maskShown) _log("遮罩未在 1.5 秒内退出（下次检查会再通知一次）");
        }

        // ------------------------------------------------------------------ ② 教师机下发 → DNS

        private void SyncFromTeacher()
        {
            if (!_cfg.IsPaired) { _mode = "unpaired"; return; }
            string json = null;
            Exception last = null;
            foreach (string addr in _cfg.Pairing.Addresses)
            {
                try
                {
                    json = Http.GetText(addr + ":" + _cfg.Pairing.Port, "/api/state");
                    if (json != null) break;
                }
                catch (Exception ex) { last = ex; }
            }
            if (json == null)
            {
                if (last != null) { _cfg.LastError = "连教师机失败：" + last.Message; _log(_cfg.LastError); }
                return;                                   // 连不上就照本地缓存继续（遮罩照旧工作）
            }
            DesiredState st;
            string err;
            if (!Protocol.TryParse(json, _cfg.Pairing.Key, out st, out err))
            {
                _cfg.LastError = err;
                _log("拒绝本次下发：" + err);
                return;
            }
            if (!Protocol.IsNewer(st.Version, _cfg.AppliedVersion))
            {
                // 版本没前进：不动系统（防重放；也避免无谓写盘）
                ApplyDnsIfNeeded(st.Dns);
                return;
            }

            _log("收到新状态 version=" + st.Version + "（遮罩=" + (st.MaskOn ? "开" : "关") +
                 "  DNS=" + (st.Dns.Count == 0 ? "未设置" : DnsList.Join(st.Dns)) + "）");
            _cfg.MaskOn = st.MaskOn;
            _cfg.MaskPasswordHash = st.MaskPasswordHash;
            _cfg.AppliedVersion = st.Version;
            _cfg.LastError = "";
            try { _cfg.Save(); } catch { }

            ApplyDnsIfNeeded(st.Dns);   // **同一轮内立刻改 DNS**
        }

        /// <summary>
        /// 把 DNS 写到**有线且已启用**的网卡上（无线/热点不动）。
        /// 只在"期望值变了"或"网卡当前值和期望不一致"时执行，避免每 2 秒刷一次 DNS。
        /// </summary>
        public void ApplyDnsIfNeeded(List<string> want)
        {
            want = want ?? new List<string>();
            if (want.Count == 0) return;                      // 空 = 不下发、不强改
            string sig = DnsList.Join(want);
            List<NicStatus> nics = WiredNic.Snapshot().Where(n => !n.LooksDown).ToList();
            if (nics.Count == 0) { _log("现在没有可用的有线网卡，DNS 等有线恢复后再应用"); _appliedDnsSig = ""; return; }

            bool needWork = false;
            foreach (NicStatus n in nics) if (!DnsList.Same(n.Dns, want)) { needWork = true; break; }
            if (!needWork && sig == _appliedDnsSig) return;

            foreach (NicStatus n in nics)
            {
                if (DnsList.Same(n.Dns, want)) continue;
                SetDns(n.Name, want);
                _log("已更新「" + n.Name + "」的 DNS → " + sig);
            }
            Run("ipconfig.exe", "/flushdns");
            _appliedDnsSig = sig;
        }

        private void SetDns(string nicName, List<string> servers)
        {
            foreach (string args in BuildSetDnsArgs(nicName, servers)) Run("netsh.exe", args);
        }

        /// <summary>
        /// 构造 netsh 参数（**纯函数**，便于自检）：网卡名可能带空格/中文，必须加双引号——
        /// 老项目踩过"网卡名有空格导致 set dns 静默失败"的坑。
        /// </summary>
        public static List<string> BuildSetDnsArgs(string nicName, List<string> servers)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(nicName) || servers == null || servers.Count == 0) return list;
            list.Add("interface ip set dns name=\"" + nicName + "\" static " + servers[0] + " primary");
            for (int i = 1; i < servers.Count; i++)
                list.Add("interface ip add dns name=\"" + nicName + "\" " + servers[i] + " index=" + (i + 1));
            return list;
        }

        // ------------------------------------------------------------------ 老师"已解除"标记（带 HMAC，防伪造）

        /// <summary>"老师已解除"标记文件（遮罩进程写、服务读）。</summary>
        public static string MaskAckPath { get { return Path.Combine(StudentConfig.DataDir, MaskAckFileName); } }

        /// <summary>写标记（遮罩在校验口令成功后调用）。用**连接密钥**签 HMAC → 学生伪造不了。</summary>
        public static void WriteAck(byte[] key)
        {
            string stamp = DateTime.UtcNow.ToString("o");
            File.WriteAllText(MaskAckPath, stamp + "|" + Protocol.Sign("mask-ack|" + stamp, key), new UTF8Encoding(false));
        }

        /// <summary>读并校验标记（服务用）。签名不对（伪造/篡改）一律当没有。</summary>
        public static DateTime ReadAck(byte[] key)
        {
            try
            {
                if (!File.Exists(MaskAckPath)) return DateTime.MinValue;
                string[] parts = File.ReadAllText(MaskAckPath, Encoding.UTF8).Trim().Split('|');
                if (parts.Length != 2) return DateTime.MinValue;
                if (!string.Equals(Protocol.Sign("mask-ack|" + parts[0], key), parts[1], StringComparison.OrdinalIgnoreCase))
                    return DateTime.MinValue;
                DateTime t;
                return DateTime.TryParse(parts[0], out t) ? t.ToUniversalTime() : DateTime.MinValue;
            }
            catch { return DateTime.MinValue; }
        }

        // ------------------------------------------------------------------ 上报

        private void Report()
        {
            if (!_cfg.IsPaired) return;
            List<NicStatus> nics = WiredNic.Snapshot();
            var doc = new Dictionary<string, object>
            {
                { "machine", Environment.MachineName },
                { "appliedVersion", _cfg.AppliedVersion },
                { "dns", nics.Count == 0 ? "" : DnsList.Join(nics[0].Dns) },
                { "nic", string.IsNullOrEmpty(_lastNic) ? (nics.Count == 0 ? "" : nics[0].Name) : _lastNic },
                { "maskShown", _maskShown },
                { "error", _cfg.LastError ?? "" },
            };
            string body = new JavaScriptSerializer().Serialize(doc);
            Exception last = null;
            foreach (string addr in _cfg.Pairing.Addresses)
            {
                try { Http.PostJson(addr + ":" + _cfg.Pairing.Port, "/api/report", body); return; }
                catch (Exception ex) { last = ex; }
            }
            if (last != null) _log("上报失败：" + last.Message);
        }

        private int Run(string file, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit(15000);
                    return p.HasExited ? p.ExitCode : -1;
                }
            }
            catch (Exception ex) { _log("执行 " + file + " " + args + " 失败：" + ex.Message); return -1; }
        }
    }
}
