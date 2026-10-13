using System;
using System.IO;
using System.Net;
using System.Text;

namespace NetLock.Shared
{
    /// <summary>
    /// 极简 HTTP：机房局域网明文（完整性由状态签名保证，不依赖传输层）。
    /// 纪律（老项目踩过的）：**3 秒超时**（网卡/交换机抖动时不能挂住轮询线程）、**不走系统代理**
    /// （机房常配代理，会直接连不上教师机）。
    /// </summary>
    public static class Http
    {
        public static HttpWebRequest Request(string address, string path, string method)
        {
            var req = (HttpWebRequest)WebRequest.Create("http://" + address + path);
            req.Method = method;
            req.Timeout = 3000;
            req.ReadWriteTimeout = 3000;
            req.Proxy = null;                       // 不走系统代理
            req.KeepAlive = false;
            req.UserAgent = AppInfo.ProductName + "/" + AppInfo.Version;
            return req;
        }

        /// <summary>GET 文本；服务端未就绪（503）返回 null；其它错误抛异常（由调用方记日志）。</summary>
        public static string GetText(string address, string path)
        {
            try
            {
                using (var resp = (HttpWebResponse)Request(address, path, "GET").GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                if (r != null && (int)r.StatusCode == 503) return null;   // 教师机还没配过 → 等
                throw;
            }
        }

        /// <summary>POST JSON（用不上返回值也读掉，避免连接被提前回收）。</summary>
        public static void PostJson(string address, string path, string json)
        {
            var req = Request(address, path, "POST");
            req.ContentType = "application/json; charset=utf-8";
            byte[] bytes = Encoding.UTF8.GetBytes(json ?? "{}");
            req.ContentLength = bytes.Length;
            using (Stream s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) { }
        }
    }
}
