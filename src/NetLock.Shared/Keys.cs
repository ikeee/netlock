using System;
using System.Security.Cryptography;
using System.Text;

namespace NetLock.Shared
{
    /// <summary>
    /// 连接密钥：**只有配对过的教师机与学生机共享同一把密钥**，因此：
    ///   · 学生机只认"签名正确 + 版本更大"的下发（别人冒充教师机发 DNS 会被拒）；
    ///   · 旧状态重放也会被版本号挡住。
    ///
    /// 密钥来源 = 教师端生成的 32 字节随机数（配对文件里带走），或由连接口令派生（老师手输时）。
    /// </summary>
    public static class ConnKey
    {
        public const int KeyBytes = 32;

        public static byte[] NewRandomKey()
        {
            var key = new byte[KeyBytes];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(key);
            return key;
        }

        /// <summary>口令 → 32 字节密钥（PBKDF2-SHA256，固定盐，两端一致）。用于"不想拷文件、直接输口令"的场合。</summary>
        public static byte[] FromPassword(string password)
        {
            if (string.IsNullOrEmpty(password)) throw new ArgumentException("口令不能为空", nameof(password));
            using (var pbkdf2 = new Rfc2898DeriveBytes(password, Encoding.UTF8.GetBytes("netlock-v1"), 100000))
                return pbkdf2.GetBytes(KeyBytes);
        }

        public static string ToBase64(byte[] key) { return Convert.ToBase64String(key ?? new byte[0]); }

        public static bool TryFromBase64(string b64, out byte[] key)
        {
            key = null;
            try
            {
                byte[] raw = Convert.FromBase64String((b64 ?? "").Trim());
                if (raw.Length != KeyBytes) return false;
                key = raw;
                return true;
            }
            catch { return false; }
        }

        /// <summary>指纹：密钥 SHA-256 前 8 字节 hex（人工核对"两边是不是同一把钥匙"用，不泄露密钥）。</summary>
        public static string Fingerprint(byte[] key)
        {
            if (key == null) return "?";
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(key);
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
