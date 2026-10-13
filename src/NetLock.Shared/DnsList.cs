using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace NetLock.Shared
{
    /// <summary>DNS 列表的解析与规范化（纯函数，便于自检）。</summary>
    public static class DnsList
    {
        /// <summary>把"一行一个 / 逗号分隔"的文本变成 IPv4 列表：去空、去重、保持顺序、**丢弃非法项**。</summary>
        public static List<string> Parse(string text)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return list;
            foreach (string raw in text.Split(new[] { ',', ';', ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string ip = raw.Trim();
                if (!IsIpv4(ip)) continue;
                if (!list.Contains(ip)) list.Add(ip);
            }
            return list;
        }

        public static bool IsIpv4(string s)
        {
            IPAddress addr;
            return !string.IsNullOrWhiteSpace(s) && IPAddress.TryParse(s.Trim(), out addr) &&
                   addr.AddressFamily == AddressFamily.InterNetwork;
        }

        public static string Join(IEnumerable<string> items)
        {
            return string.Join(",", (items ?? Enumerable.Empty<string>()).ToArray());
        }

        /// <summary>目标列表与网卡当前 DNS 是否一致（不一致才去动系统，避免每 2 秒刷一次）。</summary>
        public static bool Same(IEnumerable<string> current, IEnumerable<string> want)
        {
            List<string> a = (current ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim()).ToList();
            List<string> b = (want ?? Enumerable.Empty<string>()).ToList();
            if (a.Count != b.Count) return false;
            for (int i = 0; i < b.Count; i++)
                if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
    }
}
