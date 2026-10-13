using System.Security.Cryptography;
using System.Text;

namespace NetLock.Shared
{
    /// <summary>
    /// 内容签名（SHA-256 hex）：用来判断"磁盘上的文件变了没有"。
    /// **不要用文件时间戳判断**（老项目 LabGuard 踩过：原子写之后时间戳不一定变新，会漏掉改动）。
    /// </summary>
    public static class Hash
    {
        public static string Of(string text)
        {
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""));
                var sb = new StringBuilder(64);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
