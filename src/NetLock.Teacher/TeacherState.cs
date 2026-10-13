using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Web.Script.Serialization;
using NetLock.Shared;

namespace NetLock.Teacher
{
    /// <summary>
    /// 教师端状态（**只有这两件事**：断网遮罩开关 + DNS 列表）。
    /// 每次保存 version +1；学生机只认更大版本 → 天然防重放。
    /// 落盘：%ProgramData%\NetLockTeacher\teacher.json（含连接密钥，**不要外传**）。
    /// </summary>
    public sealed class TeacherState
    {
        public const string Format = "netlock-teacher/1";
        public byte[] Key = null;                  // 连接密钥（配对文件里带走）
        public long Version;                       // 0 = 还没保存过（学生机会看到 503）
        public bool MaskOn = true;                 // 拔网线是否显示遮罩
        public List<string> Dns = new List<string>();
        public string MaskPasswordHash = "";       // 老师解除遮罩的口令（只存散列）
        public int Port = 8210;

        /// <summary>数据目录覆盖（`--data-dir`，测试/便携用；null = 默认 %ProgramData%\NetLockTeacher）。</summary>
        public static string DirOverride;

        public static string Dir
        {
            get
            {
                return DirOverride ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetLockTeacher");
            }
        }
        public static string FilePath { get { return Path.Combine(Dir, "teacher.json"); } }

        public static TeacherState LoadOrCreate()
        {
            var st = new TeacherState();
            st.ReloadFromDisk();
            if (st.Key == null || st.Key.Length != ConnKey.KeyBytes) st.Key = ConnKey.NewRandomKey();
            return st;
        }

        private string _diskSig;

        /// <summary>
        /// 从磁盘重读并**就地更新**本实例（供运行中的教师端热拾取外部改动：脚本/另一个进程用 CLI 改了设置）。
        /// 返回 true = 确实变了。用内容签名判断，不用时间戳（老项目踩过）。
        /// </summary>
        public bool TryReloadFromDisk()
        {
            try
            {
                if (!File.Exists(FilePath)) return false;
                string text = File.ReadAllText(FilePath, Encoding.UTF8);
                string sig = Hash.Of(text);
                if (sig == _diskSig) return false;
                _diskSig = sig;
                bool changed = ReloadFromDisk();
                return changed;
            }
            catch { return false; }
        }

        /// <summary>真正读盘并覆盖字段。返回值只表示"读到了"，不表示内容变化。</summary>
        private bool ReloadFromDisk()
        {
            var st = new TeacherState();
            try
            {
                if (File.Exists(FilePath))
                {
                    var doc = new JavaScriptSerializer { MaxJsonLength = 256 * 1024 }
                        .Deserialize<Dictionary<string, object>>(File.ReadAllText(FilePath, Encoding.UTF8));
                    if (doc != null)
                    {
                        byte[] key;
                        if (ConnKey.TryFromBase64(Str(doc, "key"), out key)) st.Key = key;
                        st.Version = Long(doc, "version");
                        st.MaskOn = Bool(doc, "maskOn");
                        st.MaskPasswordHash = Str(doc, "maskPasswordHash") ?? "";
                        int port = (int)Long(doc, "port");
                        if (port > 0 && port <= 65535) st.Port = port;
                        if (doc.TryGetValue("dns", out object dnsRaw) && dnsRaw is System.Collections.IEnumerable en && !(dnsRaw is string))
                            foreach (object o in en) { string s = o as string; if (!string.IsNullOrWhiteSpace(s)) st.Dns.Add(s.Trim()); }
                    }
                }
            }
            catch { /* 读坏了就按默认来（下面会重写一份） */ }
            if (st.Key != null && st.Key.Length == ConnKey.KeyBytes) Key = st.Key;
            Version = st.Version;
            MaskOn = st.MaskOn;
            MaskPasswordHash = st.MaskPasswordHash;
            if (st.Port > 0) Port = st.Port;
            Dns = st.Dns;
            return true;
        }

        public void Save()
        {
            Directory.CreateDirectory(Dir);
            var doc = new Dictionary<string, object>
            {
                { "format", Format },
                { "key", ConnKey.ToBase64(Key) },
                { "version", Version },
                { "maskOn", MaskOn },
                { "dns", Dns ?? new List<string>() },
                { "maskPasswordHash", MaskPasswordHash ?? "" },
                { "port", Port },
            };
            Util.AtomicWrite(FilePath, new JavaScriptSerializer().Serialize(doc));
        }

        /// <summary>导出配对文件：学生机 `--pair` 用它（**等于钥匙，别乱放**）。</summary>
        public void ExportPairing(string path)
        {
            var doc = new Dictionary<string, object>
            {
                { "format", "netlock-pairing/1" },
                { "addresses", LocalIpv4() },
                { "port", Port },
                { "key", ConnKey.ToBase64(Key) },
                { "fingerprint", ConnKey.Fingerprint(Key) },
                { "exportedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) },
            };
            File.WriteAllText(path, new JavaScriptSerializer().Serialize(doc), new UTF8Encoding(false));
        }

        /// <summary>本机 IPv4（排除回环/169.254）；教师机可能有多个网卡，全列出来让学生机挑。</summary>
        public static string[] LocalIpv4()
        {
            try
            {
                return System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName())
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) &&
                                !a.ToString().StartsWith("169.254."))
                    .Select(a => a.ToString()).ToArray();
            }
            catch { return new string[0]; }
        }

        /// <summary>当前期望状态（下发给学生机的那一份）。</summary>
        public DesiredState ToDesired()
        {
            return new DesiredState
            {
                Version = Version,
                MaskOn = MaskOn,
                Dns = new List<string>(Dns ?? new List<string>()),
                MaskPasswordHash = MaskPasswordHash ?? "",
            };
        }

        private static string Str(Dictionary<string, object> d, string k) { return d.TryGetValue(k, out object v) ? v as string : null; }
        private static long Long(Dictionary<string, object> d, string k) { return d.TryGetValue(k, out object v) && v is IConvertible ? Convert.ToInt64(v) : 0; }
        private static bool Bool(Dictionary<string, object> d, string k) { return d.TryGetValue(k, out object v) && v is bool b && b; }
    }
}
