using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetLock.Shared
{
    /// <summary>一块有线网卡的即时状态。</summary>
    public sealed class NicStatus
    {
        public string Name;
        public string Description;
        public bool Up;
        public string Ipv4;            // null / "0.0.0.0" / "169.254.*" 都算"没有可用地址"
        public List<string> Dns = new List<string>();

        /// <summary>这块网卡当前算不算"没网"（断网判定的最小判据）。</summary>
        public bool LooksDown { get { return WiredNic.IsDown(Ipv4, Up); } }

        public override string ToString()
        {
            return Name + "(" + Description + ") " + (Up ? "Up" : "Down") + " ip=" + (Ipv4 ?? "-") +
                   (LooksDown ? " [没网]" : " [通]");
        }
    }

    /// <summary>
    /// "有线网卡"的识别与断网判定（**纯逻辑 + 一个枚举方法**，方便自检）。
    ///
    /// 为什么只挑有线：无线/热点（手机热点、Wi-Fi）本来就会时断时续，拿它当判据必然误报；
    /// 机房要的是"**网线被拔掉**（或网卡被禁）→ 遮罩"。
    /// </summary>
    public static class WiredNic
    {
        /// <summary>网卡类型算不算"有线"（不含无线/回环/隧道/虚拟）。</summary>
        public static bool IsWired(NetworkInterfaceType t)
        {
            switch (t)
            {
                case NetworkInterfaceType.Ethernet:
                case NetworkInterfaceType.Ethernet3Megabit:
                case NetworkInterfaceType.FastEthernetT:
                case NetworkInterfaceType.FastEthernetFx:
                case NetworkInterfaceType.GigabitEthernet:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 描述里带这些关键字的**一律不算判据网卡**。为什么要这一步（真机踩到）：
        /// **蓝牙 PAN（"Bluetooth Device (Personal Area Network)"）在 Windows 里被归类成 Ethernet**，
        /// 如果不排除，一台"只有蓝牙 + 无线、没有真实网线"的机器会被判成"有线网卡全断了" → **误弹遮罩**。
        /// 同理排除虚拟网卡/WAN Miniport/Wi-Fi Direct/Hyper-V/VMware 等。
        /// </summary>
        public static bool IsVirtualOrWireless(string description)
        {
            string d = (description ?? "").ToLowerInvariant();
            string[] bad =
            {
                "bluetooth", "蓝牙", "personal area network", "virtual", "虚拟",
                "miniport", "wi-fi direct", "wifi direct", "wireless", "无线",
                "hyper-v", "vmware", "virtualbox", "tap-", "vpn", "loopback",
                "kernel debug", "npcap", "wintun", "zerotier", "openvpn",
            };
            foreach (string k in bad) if (d.Contains(k)) return true;
            return false;
        }

        /// <summary>断网判定（纯函数）：网卡没起来 / 没地址 / 0.0.0.0 / 169.254.*（没拿到 DHCP）都算断。</summary>
        public static bool IsDown(string ipv4, bool nicUp)
        {
            if (!nicUp) return true;
            if (string.IsNullOrEmpty(ipv4)) return true;
            if (ipv4 == "0.0.0.0") return true;
            return ipv4.StartsWith("169.254.");
        }

        /// <summary>当前所有**有线且已启用**的网卡状态（含各自 DNS）。</summary>
        public static List<NicStatus> Snapshot()
        {
            var list = new List<NicStatus>();
            IEnumerable<NetworkInterface> all;
            try { all = NetworkInterface.GetAllNetworkInterfaces(); }
            catch { return list; }
            foreach (NetworkInterface nic in all)
            {
                try
                {
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    if (!IsWired(nic.NetworkInterfaceType)) continue;
                    if (IsVirtualOrWireless(nic.Description)) continue;   // 蓝牙 PAN/虚拟网卡等（见 IsVirtualOrWireless 注释）
                    var st = new NicStatus
                    {
                        Name = nic.Name,
                        Description = nic.Description,
                        Up = nic.OperationalStatus == OperationalStatus.Up,
                    };
                    IPInterfaceProperties props = nic.GetIPProperties();
                    var unicast = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                    st.Ipv4 = unicast?.Address.ToString();
                    st.Dns = props.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                        .Select(a => a.ToString()).ToList();
                    list.Add(st);
                }
                catch { /* 单块网卡读失败不影响其它 */ }
            }
            return list;
        }

        /// <summary>按名字找一块网卡（应用 DNS 前确认它还在）。</summary>
        public static NicStatus Find(List<NicStatus> snapshot, string name)
        {
            return (snapshot ?? new List<NicStatus>())
                .FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }
}
