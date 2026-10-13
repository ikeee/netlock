using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using NetLock.Shared;

namespace NetLock.Teacher
{
    /// <summary>一台学生机最近一次上报。</summary>
    public sealed class DeviceReport
    {
        public string Machine;
        public string Ip;
        public long AppliedVersion;
        public string Dns;          // 学生机当前网卡 DNS
        public string Nic;          // 判据网卡名
        public bool MaskShown;      // 现在是不是正在显示遮罩
        public string Error;
        public DateTime LastSeenUtc;
    }

    /// <summary>
    /// 教师机内置 HTTP 服务（**只有两个接口**）：
    ///   GET  /api/state  → 当前状态（签名 JSON；没配过 → 503）
    ///   POST /api/report → 学生机上报（机器名/已应用版本/当前 DNS/遮罩状态）
    /// 学生机每 2 秒轮询一次，所以老师一按保存，全班 2 秒内换 DNS。
    /// </summary>
    public sealed class ApiServer
    {
        public const int DefaultPort = 8210;
        private readonly TeacherState _state;
        private readonly ConcurrentDictionary<string, DeviceReport> _devices =
            new ConcurrentDictionary<string, DeviceReport>(StringComparer.OrdinalIgnoreCase);
        private HttpListener _listener;
        private Thread _thread;
        private volatile bool _running;

        public string BoundPrefix { get; private set; }

        public ApiServer(TeacherState state) { _state = state; }

        /// <summary>200 次/秒/IP 上限（防上报把服务端刷爆），单纯计数用。</summary>
        private readonly ConcurrentDictionary<string, int> _rate = new ConcurrentDictionary<string, int>();

        /// <summary>依次尝试 `+:port`（需管理员/URLACL）→ localhost → 127.0.0.1。</summary>
        public bool Start(int port, string hostname = null)
        {
            string host = string.IsNullOrEmpty(hostname) ? "+" : hostname;
            string[] candidates = host == "+"
                ? new[] { "http://+:" + port + "/", "http://localhost:" + port + "/", "http://127.0.0.1:" + port + "/" }
                : new[] { "http://" + host + ":" + port + "/" };
            foreach (string prefix in candidates)
            {
                try
                {
                    var l = new HttpListener();
                    l.Prefixes.Add(prefix);
                    l.Start();
                    _listener = l;
                    BoundPrefix = prefix;
                    _running = true;
                    _thread = new Thread(Loop) { IsBackground = true, Name = "NetLock.Api" };
                    _thread.Start();
                    return true;
                }
                catch (HttpListenerException) { /* 该 prefix 被拒（权限/占用），试下一个 */ }
            }
            return false;
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            _listener = null;
        }

        public DeviceReport[] Snapshot()
        {
            return _devices.Values.OrderBy(d => d.Machine, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private void Loop()
        {
            while (_running)
            {
                HttpListenerContext ctx = null;
                try { ctx = _listener.GetContext(); }
                catch { if (!_running) return; continue; }
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { Handle(ctx); }
                    catch { try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { } }
                });
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            string path = ctx.Request.Url.AbsolutePath.TrimEnd('/');
            string ip = ctx.Request.RemoteEndPoint?.Address.ToString() ?? "?";
            if (ctx.Request.HttpMethod == "GET" && path == "/api/state")
            {
                // 热拾取：外部（脚本/另一个 CLI 进程）改了设置文件，运行中的教师端要能立刻按新的发
                // （真实流程是 GUI 改内存 → 不需要；但"用脚本改 → 学生机 2 秒内跟上"必须成立）
                _state.TryReloadFromDisk();
                if (_state.Version <= 0)
                {
                    Reply(ctx, 503, "text/plain; charset=utf-8", "not configured");
                    return;
                }
                Reply(ctx, 200, "application/json; charset=utf-8",
                    Protocol.ToJson(_state.ToDesired(), _state.Key));
                return;
            }
            if (ctx.Request.HttpMethod == "POST" && path == "/api/report")
            {
                long n = _rate.AddOrUpdate(ip, 1, (k, v) => v + 1);
                if (n > 200) { Reply(ctx, 429, "text/plain", "too many"); return; }
                string body;
                using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = sr.ReadToEnd();
                if (string.IsNullOrEmpty(body) || body.Length > 4096) { Reply(ctx, 400, "text/plain", "bad request"); return; }
                Dictionary<string, object> doc;
                try { doc = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 }.Deserialize<Dictionary<string, object>>(body); }
                catch { Reply(ctx, 400, "text/plain", "bad json"); return; }
                if (doc == null) { Reply(ctx, 400, "text/plain", "bad json"); return; }
                string machine = Clip(Str(doc, "machine"), 64);
                if (string.IsNullOrEmpty(machine)) { Reply(ctx, 400, "text/plain", "missing machine"); return; }
                var rec = new DeviceReport
                {
                    Machine = machine,
                    Ip = ip,
                    AppliedVersion = Long(doc, "appliedVersion"),
                    Dns = Clip(Str(doc, "dns"), 128),
                    Nic = Clip(Str(doc, "nic"), 64),
                    MaskShown = Bool(doc, "maskShown"),
                    Error = Clip(Str(doc, "error"), 128),
                    LastSeenUtc = DateTime.UtcNow,
                };
                _devices[machine] = rec;
                Reply(ctx, 200, "application/json", "{\"ok\":true}");
                return;
            }
            Reply(ctx, 404, "text/plain", "not found");
        }

        private static void Reply(HttpListenerContext ctx, int status, string type, string body)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body ?? "");
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = type;
            ctx.Response.ContentLength64 = bytes.Length;
            if (ctx.Request.HttpMethod != "HEAD")
            {
                using (Stream s = ctx.Response.OutputStream) s.Write(bytes, 0, bytes.Length);
            }
            ctx.Response.Close();
        }

        private static string Clip(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max);
        }
        private static string Str(Dictionary<string, object> d, string k) { return d.TryGetValue(k, out object v) ? v as string : null; }
        private static long Long(Dictionary<string, object> d, string k) { return d.TryGetValue(k, out object v) && v is IConvertible ? Convert.ToInt64(v) : 0; }
        private static bool Bool(Dictionary<string, object> d, string k) { return d.TryGetValue(k, out object v) && v is bool b && b; }
    }
}
