using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using NetLock.Shared;
using NetLock.Student;

namespace NetLock.SelfTest
{
    /// <summary>
    /// 自检：**只测纯逻辑**（不碰系统、不显示界面、不改 DNS、不碰真机数据目录）。
    /// 覆盖：签名与防篡改、版本单调（防重放）、DNS 解析、断网判定、口令散列、"老师已解除"标记防伪造。
    /// 退出码 0 = 全过。
    /// </summary>
    internal static class Program
    {
        private static int _pass, _fail;

        private static void Check(string name, bool ok, string detail = null)
        {
            Console.WriteLine((ok ? "  [PASS] " : "  [FAIL] ") + name + (detail == null ? "" : "  -> " + detail));
            if (ok) _pass++; else _fail++;
        }

        private static int Main()
        {
            Console.WriteLine("=== NetLock 自检（纯逻辑，不碰系统）===");
            TestKeys();
            TestProtocol();
            TestDns();
            TestNic();
            TestPassword();
            TestAck();
            Console.WriteLine();
            Console.WriteLine(_fail == 0 ? ("全部通过 ✅（" + _pass + " 项）") : ("失败 " + _fail + " 项 ❌"));
            return _fail == 0 ? 0 : 1;
        }

        private static void TestKeys()
        {
            Console.WriteLine("[1] 连接密钥");
            byte[] a = ConnKey.NewRandomKey(), b = ConnKey.NewRandomKey();
            Check("1.1 随机密钥 32 字节", a.Length == 32 && b.Length == 32);
            Check("1.2 两次随机不同", !a.SequenceEqual(b));
            Check("1.3 指纹稳定且 16 位 hex", ConnKey.Fingerprint(a) == ConnKey.Fingerprint(a) && ConnKey.Fingerprint(a).Length == 16);
            byte[] p1 = ConnKey.FromPassword("laoshi-1234"), p2 = ConnKey.FromPassword("laoshi-1234");
            Check("1.4 同一口令派生同一密钥（两端才连得上）", p1.SequenceEqual(p2));
            Check("1.5 不同口令派生不同密钥", !ConnKey.FromPassword("x1").SequenceEqual(ConnKey.FromPassword("x2")));
            byte[] back;
            Check("1.6 Base64 往返", ConnKey.TryFromBase64(ConnKey.ToBase64(a), out back) && back.SequenceEqual(a));
            Check("1.7 长度不对的 Base64 拒绝", !ConnKey.TryFromBase64(Convert.ToBase64String(new byte[16]), out back));
        }

        private static void TestProtocol()
        {
            Console.WriteLine("[2] 状态信封（签名 + 防重放）");
            byte[] key = ConnKey.FromPassword("pair-key");
            var st = new DesiredState
            {
                Version = 3, MaskOn = true,
                Dns = new List<string> { "10.46.254.10", "223.5.5.5" },
                MaskPasswordHash = PasswordHash.Create("abc123"),
            };
            string json = Protocol.ToJson(st, key);
            DesiredState got; string err;
            Check("2.1 正常解析 + 验签通过", Protocol.TryParse(json, key, out got, out err), err);
            Check("2.2 字段往返一致（版本/遮罩/DNS/口令散列）",
                got != null && got.Version == 3 && got.MaskOn && got.Dns.Count == 2 && got.Dns[1] == "223.5.5.5" &&
                got.MaskPasswordHash == st.MaskPasswordHash);

            Check("2.3 **换一把密钥 → 拒绝**（别的教师机冒充不了）", !Protocol.TryParse(json, ConnKey.FromPassword("wrong"), out _, out err));
            Check("2.4 **DNS 被改一个字符 → 拒绝**（内容被篡改）",
                !Protocol.TryParse(json.Replace("223.5.5.5", "8.8.8.8"), key, out _, out err));
            Check("2.5 **版本被改大 → 拒绝**（签名覆盖版本）",
                !Protocol.TryParse(json.Replace("\"version\":3", "\"version\":4"), key, out _, out err));
            Check("2.6 坏 JSON / 空响应 → 拒绝且不抛异常",
                !Protocol.TryParse("{坏", key, out _, out err) && !Protocol.TryParse("", key, out _, out err));
            Check("2.7 格式不认识 → 拒绝",
                !Protocol.TryParse(json.Replace(Protocol.Format, "netlock-state/9"), key, out _, out err));
            Check("2.8 版本单调：只有严格更大才算新", Protocol.IsNewer(4, 3) && !Protocol.IsNewer(3, 3) && !Protocol.IsNewer(2, 3));
        }

        private static void TestDns()
        {
            Console.WriteLine("[3] DNS 列表解析");
            var list = DnsList.Parse("10.46.254.10\n223.5.5.5, 10.46.254.10  ; 1.1.1.1");
            Check("3.1 换行/逗号/分号混用都能解析，且去重保序",
                list.Count == 3 && list[0] == "10.46.254.10" && list[1] == "223.5.5.5" && list[2] == "1.1.1.1",
                DnsList.Join(list));
            Check("3.2 非法项被丢弃（不会把垃圾写进网卡）",
                DnsList.Parse("abc,999.1.1.1,10.0.0.1").SequenceEqual(new[] { "10.0.0.1" }));
            Check("3.3 空文本 → 空列表（= 不下发、不强改）", DnsList.Parse("   ").Count == 0);
            Check("3.4 Same()：顺序不同算不同（网卡 DNS 有优先级）",
                DnsList.Same(new[] { "1.1.1.1", "2.2.2.2" }, new[] { "1.1.1.1", "2.2.2.2" }) &&
                !DnsList.Same(new[] { "2.2.2.2", "1.1.1.1" }, new[] { "1.1.1.1", "2.2.2.2" }) &&
                !DnsList.Same(new string[0], new[] { "1.1.1.1" }));
        }

        private static void TestNic()
        {
            Console.WriteLine("[4] 断网判定（只看有线网卡）");
            Check("4.1 网卡 Down → 断", WiredNic.IsDown(null, false));
            Check("4.2 没有 IP → 断", WiredNic.IsDown(null, true) && WiredNic.IsDown("", true));
            Check("4.3 0.0.0.0 → 断", WiredNic.IsDown("0.0.0.0", true));
            Check("4.4 169.254.*（没拿到 DHCP）→ 断", WiredNic.IsDown("169.254.3.9", true));
            Check("4.5 正常私有地址 → 通", !WiredNic.IsDown("10.46.254.42", true));
            Check("4.6 有线网卡类型识别（Ethernet/Gigabit 算，无线/回环不算）",
                WiredNic.IsWired(NetworkInterfaceType.Ethernet) &&
                WiredNic.IsWired(NetworkInterfaceType.GigabitEthernet) &&
                !WiredNic.IsWired(NetworkInterfaceType.Wireless80211) &&
                !WiredNic.IsWired(NetworkInterfaceType.Loopback));
            Check("4.6b 描述过滤：**蓝牙 PAN（Win 归类为 Ethernet）必须排除**，否则没有网线的机器会误弹遮罩",
                WiredNic.IsVirtualOrWireless("Bluetooth Device (Personal Area Network)") &&
                WiredNic.IsVirtualOrWireless("Microsoft Wi-Fi Direct Virtual Adapter") &&
                WiredNic.IsVirtualOrWireless("Hyper-V Virtual Ethernet Adapter") &&
                WiredNic.IsVirtualOrWireless("WAN Miniport (IKEv2)") &&
                !WiredNic.IsVirtualOrWireless("Realtek PCIe GbE Family Controller") &&
                !WiredNic.IsVirtualOrWireless("Intel(R) Ethernet Connection I219-V"));
            var snap = WiredNic.Snapshot();
            Check("4.7 能枚举本机有线网卡（本机可能是 0 块，不断言数量）", snap != null);
            Console.WriteLine("      本机有线网卡：" + (snap.Count == 0 ? "（无）" :
                string.Join("；", snap.Select(n => n.ToString()))));
            var args = StudentEngine.BuildSetDnsArgs("以太网 2", new List<string> { "10.46.254.10", "223.5.5.5" });
            Check("4.8 netsh 参数构造：网卡名带空格/中文要加引号，主 DNS + 备用 DNS 顺序正确（2 条命令）",
                args.Count == 2 &&
                args[0] == "interface ip set dns name=\"以太网 2\" static 10.46.254.10 primary" &&
                args[1] == "interface ip add dns name=\"以太网 2\" 223.5.5.5 index=2",
                string.Join(" || ", args));
            Check("4.9 网卡名为空 / DNS 为空 → 不产生任何命令（绝不去动系统）",
                StudentEngine.BuildSetDnsArgs("", new List<string> { "1.1.1.1" }).Count == 0 &&
                StudentEngine.BuildSetDnsArgs("eth0", new List<string>()).Count == 0);
        }

        private static void TestPassword()
        {
            Console.WriteLine("[5] 遮罩口令散列");
            string h = PasswordHash.Create("abc123");
            Check("5.1 正确口令通过", PasswordHash.Verify("abc123", h));
            Check("5.2 错误口令不通过", !PasswordHash.Verify("abc124", h));
            Check("5.3 空口令/坏散列不通过（也不抛异常）",
                !PasswordHash.Verify("", h) && !PasswordHash.Verify("abc123", "") &&
                !PasswordHash.Verify("abc123", "明文不是散列") && !PasswordHash.Verify("abc123", "pbkdf2-sha256$0$x$y"));
            Check("5.4 同一口令两次散列不同（带随机盐）", PasswordHash.Create("abc123") != h);
            Check("5.5 散列里不含明文口令", h.IndexOf("abc123", StringComparison.Ordinal) < 0);
        }

        private static void TestAck()
        {
            Console.WriteLine("[6] 「老师已解除」标记（HMAC 防伪造）");
            string old = StudentConfig.DataDir;
            string tmp = Path.Combine(Path.GetTempPath(), "netlock-selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                StudentConfig.DataDir = tmp;
                Directory.CreateDirectory(tmp);
                byte[] key = ConnKey.FromPassword("ack-key");
                Check("6.1 没有标记文件 → 视为未解除", StudentEngine.ReadAck(key) == DateTime.MinValue);
                StudentEngine.WriteAck(key);
                DateTime ack = StudentEngine.ReadAck(key);
                Check("6.2 老师解除后能读回（约等于现在）", ack != DateTime.MinValue && Math.Abs((DateTime.UtcNow - ack).TotalMinutes) < 1);
                Check("6.3 **换个密钥就认不出**（学生另造一份没用）", StudentEngine.ReadAck(ConnKey.FromPassword("别的")) == DateTime.MinValue);
                File.WriteAllText(StudentEngine.MaskAckPath, DateTime.UtcNow.ToString("o") + "|deadbeef");
                Check("6.4 手改签名 → 视为未解除", StudentEngine.ReadAck(key) == DateTime.MinValue);
                File.WriteAllText(StudentEngine.MaskAckPath, "乱七八糟");
                Check("6.5 格式不对 → 视为未解除（不抛异常）", StudentEngine.ReadAck(key) == DateTime.MinValue);
            }
            finally
            {
                StudentConfig.DataDir = old;
                try { Directory.Delete(tmp, true); } catch { }
            }
        }
    }
}
