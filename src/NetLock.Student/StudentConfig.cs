using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using NetLock.Shared;

namespace NetLock.Student
{
    /// <summary>配对信息（从教师机导出的 pairing.json 导入）。</summary>
    public sealed class PairingInfo
    {
        public List<string> Addresses = new List<string>();
        public int Port = 8210;
        public byte[] Key;
        public string Fingerprint = "";

        public string Address { get { return (Addresses.Count == 0 ? "?" : Addresses[0]) + ":" + Port; } }
    }

    /// <summary>
    /// 学生端配置：配对信息 + 已应用版本 + 本地缓存的"遮罩开关"。
    /// 缓存的意义：教师机暂时不在线时，断网遮罩照旧工作（**不依赖在线**）。
    /// 落盘：&lt;数据目录&gt;\config.json（默认 %ProgramData%\NetLock；`--data-dir` 可覆盖）。
    /// </summary>
    public sealed class StudentConfig
    {
        public const string DefaultDirName = "NetLock";

        public static string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), DefaultDirName);

        public PairingInfo Pairing = new PairingInfo();
        public long AppliedVersion;          // 已应用版本（只接受更大的版本：防重放）
        public bool MaskOn = true;           // 本地缓存的"是否显示遮罩"
        public string MaskPasswordHash = ""; // 解遮罩口令散列（下发后缓存，离线也能解）
        public DateTime MaskAckUtc = DateTime.MinValue;   // 老师已解除（本次断网不再弹）
        public string LastError = "";        // 最近一次出错原因（上报给教师机看）

        public static string ConfigPath { get { return Path.Combine(DataDir, "config.json"); } }

        public static StudentConfig Load()
        {
            var cfg = new StudentConfig();
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var doc = new JavaScriptSerializer { MaxJsonLength = 256 * 1024 }
                        .Deserialize<Dictionary<string, object>>(File.ReadAllText(ConfigPath, Encoding.UTF8));
                    if (doc != null)
                    {
                        cfg.AppliedVersion = Long(doc, "appliedVersion");
                        cfg.MaskOn = Bool(doc, "maskOn", true);
                        cfg.MaskPasswordHash = Str(doc, "maskPasswordHash") ?? "";
                        DateTime ack;
                        if (DateTime.TryParse(Str(doc, "maskAckUtc"), out ack)) cfg.MaskAckUtc = ack.ToUniversalTime();
                        cfg.LastError = Str(doc, "lastError") ?? "";
                        if (doc.TryGetValue("pairing", out object p) && p is Dictionary<string, object> pd)
                        {
                            byte[] key;
                            if (ConnKey.TryFromBase64(Str(pd, "key"), out key)) cfg.Pairing.Key = key;
                            cfg.Pairing.Fingerprint = Str(pd, "fingerprint") ?? "";
                            int port = (int)Long(pd, "port");
                            if (port > 0) cfg.Pairing.Port = port;
                            if (pd.TryGetValue("addresses", out object addrs) && addrs is System.Collections.IEnumerable en && !(addrs is string))
                                foreach (object o in en) { string s = o as string; if (!string.IsNullOrWhiteSpace(s)) cfg.Pairing.Addresses.Add(s.Trim()); }
                        }
                    }
                }
            }
            catch { /* 读坏了按未配置处理 */ }
            return cfg;
        }

        public void Save()
        {
            Directory.CreateDirectory(DataDir);
            var doc = new Dictionary<string, object>
            {
                { "appliedVersion", AppliedVersion },
                { "maskOn", MaskOn },
                { "maskPasswordHash", MaskPasswordHash ?? "" },
                { "maskAckUtc", MaskAckUtc == DateTime.MinValue ? "" : MaskAckUtc.ToString("o") },
                { "lastError", LastError ?? "" },
                { "pairing", new Dictionary<string, object>
                    {
                        { "addresses", Pairing.Addresses },
                        { "port", Pairing.Port },
                        { "key", ConnKey.ToBase64(Pairing.Key) },
                        { "fingerprint", Pairing.Fingerprint ?? "" },
                    } },
            };
            File.WriteAllText(ConfigPath, new JavaScriptSerializer().Serialize(doc), new UTF8Encoding(false));
        }

        public bool IsPaired
        {
            get { return Pairing.Key != null && Pairing.Key.Length == ConnKey.KeyBytes && Pairing.Addresses.Count > 0; }
        }

        /// <summary>导入教师机导出的配对文件。</summary>
        public static void ImportPairing(string pairPath, StudentConfig cfg)
        {
            var doc = new JavaScriptSerializer { MaxJsonLength = 256 * 1024 }
                .Deserialize<Dictionary<string, object>>(File.ReadAllText(pairPath, Encoding.UTF8));
            if (doc == null) throw new InvalidDataException("配对文件不是 JSON 对象");
            string format = Str(doc, "format");
            if (format != "netlock-pairing/1") throw new InvalidDataException("配对文件格式不认识：" + format);
            byte[] key;
            if (!ConnKey.TryFromBase64(Str(doc, "key"), out key)) throw new InvalidDataException("配对文件里的密钥不合法");
            var info = new PairingInfo { Key = key, Fingerprint = Str(doc, "fingerprint") ?? "" };
            int port = (int)Long(doc, "port");
            if (port > 0) info.Port = port;
            if (doc.TryGetValue("addresses", out object addrs) && addrs is System.Collections.IEnumerable en && !(addrs is string))
                foreach (object o in en) { string s = o as string; if (!string.IsNullOrWhiteSpace(s)) info.Addresses.Add(s.Trim()); }
            if (info.Addresses.Count == 0) throw new InvalidDataException("配对文件里没有教师机地址");
            cfg.Pairing = info;
            cfg.Save();
        }

        private static string Str(Dictionary<string, object> d, string k) { return d.TryGetValue(k, out object v) ? v as string : null; }
        private static long Long(Dictionary<string, object> d, string k) { return d.TryGetValue(k, out object v) && v is IConvertible ? Convert.ToInt64(v) : 0; }
        private static bool Bool(Dictionary<string, object> d, string k, bool fallback)
        {
            return d.TryGetValue(k, out object v) && v is bool b ? b : fallback;
        }
    }
}
