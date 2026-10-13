using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace NetLock.Shared
{
    /// <summary>教师机下发的"当前期望状态"（**只有这两件事**：断网遮罩开关 + DNS 列表）。</summary>
    public sealed class DesiredState
    {
        /// <summary>版本号：每次保存 +1。学生机只接受**更大**的版本（防重放/防乱序）。</summary>
        public long Version;
        /// <summary>拔网线时是否显示全屏遮罩（老师在教师机上启用/停用）。</summary>
        public bool MaskOn;
        /// <summary>要下发的 DNS（IPv4，按顺序）。空 = 不下发/不强改。</summary>
        public List<string> Dns = new List<string>();
        /// <summary>老师解除遮罩用的口令散列（学生机本地校验；空 = 只能用密码之外的"后路"解除）。</summary>
        public string MaskPasswordHash = "";
        /// <summary>HMAC 签名（hex）。</summary>
        public string Signature = "";

        /// <summary>签名载荷（两端必须逐字节一致，所以单独抽出来）。</summary>
        public string Payload()
        {
            return Version.ToString(System.Globalization.CultureInfo.InvariantCulture)
                   + "|" + (MaskOn ? "1" : "0")
                   + "|" + DnsList.Join(Dns)
                   + "|" + (MaskPasswordHash ?? "");
        }
    }

    /// <summary>
    /// 状态信封：**签名 + 版本单调**。
    /// 为什么最小版也要签名：不签名的话，同一网段里任何人都能假冒教师机把学生机 DNS 改成任意地址
    /// （把学生引到钓鱼站），这是"下发 DNS"这个功能自带的攻击面，必须堵。
    /// </summary>
    public static class Protocol
    {
        public const string Format = "netlock-state/1";
        private static readonly JavaScriptSerializer Ser = new JavaScriptSerializer { MaxJsonLength = 256 * 1024 };

        public static string Sign(string payload, byte[] key)
        {
            using (var h = new HMACSHA256(key))
                return Hex(h.ComputeHash(Encoding.UTF8.GetBytes(payload ?? "")));
        }

        /// <summary>把状态序列化成下发的 JSON（含签名）。</summary>
        public static string ToJson(DesiredState st, byte[] key)
        {
            st.Signature = Sign(st.Payload(), key);
            var doc = new Dictionary<string, object>
            {
                { "format", Format },
                { "version", st.Version },
                { "maskOn", st.MaskOn },
                { "dns", st.Dns ?? new List<string>() },
                { "maskPasswordHash", st.MaskPasswordHash ?? "" },
                { "sig", st.Signature },
            };
            return Ser.Serialize(doc);
        }

        /// <summary>
        /// 解析并验签。任何一步不对都返回 false 并给出原因（调用方保持现状、不应用）。
        /// </summary>
        public static bool TryParse(string json, byte[] key, out DesiredState st, out string error)
        {
            st = null;
            error = null;
            if (string.IsNullOrWhiteSpace(json)) { error = "空响应"; return false; }
            Dictionary<string, object> doc;
            try { doc = Ser.Deserialize<Dictionary<string, object>>(json); }
            catch (Exception ex) { error = "JSON 解析失败：" + ex.Message; return false; }
            if (doc == null) { error = "不是 JSON 对象"; return false; }
            if (!string.Equals(Str(doc, "format"), Format, StringComparison.Ordinal))
            { error = "格式不认识：" + Str(doc, "format"); return false; }

            var st2 = new DesiredState
            {
                Version = Long(doc, "version"),
                MaskOn = Bool(doc, "maskOn"),
                MaskPasswordHash = Str(doc, "maskPasswordHash") ?? "",
                Signature = Str(doc, "sig") ?? "",
            };
            if (doc.TryGetValue("dns", out object dnsRaw) && dnsRaw is System.Collections.IEnumerable en && !(dnsRaw is string))
                foreach (object o in en) { string s = o as string; if (!string.IsNullOrWhiteSpace(s)) st2.Dns.Add(s.Trim()); }

            if (st2.Version <= 0) { error = "version 非法：" + st2.Version; return false; }
            string want = Sign(st2.Payload(), key);
            if (!string.Equals(want, st2.Signature, StringComparison.OrdinalIgnoreCase))
            { error = "**签名校验失败**（不是配对的教师机，或内容被改过）"; return false; }
            st = st2;
            return true;
        }

        /// <summary>版本是否"更新"（严格更大才算，避免重放旧状态）。</summary>
        public static bool IsNewer(long candidate, long applied) { return candidate > applied; }

        private static string Hex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        private static string Str(Dictionary<string, object> d, string k)
        {
            return d.TryGetValue(k, out object v) ? v as string : null;
        }

        private static long Long(Dictionary<string, object> d, string k)
        {
            return d.TryGetValue(k, out object v) && v is IConvertible ? Convert.ToInt64(v) : 0;
        }

        private static bool Bool(Dictionary<string, object> d, string k)
        {
            return d.TryGetValue(k, out object v) && v is bool b && b;
        }
    }
}
