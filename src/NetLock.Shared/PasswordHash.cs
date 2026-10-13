using System;
using System.Security.Cryptography;
using System.Text;

namespace NetLock.Shared
{
    /// <summary>
    /// 遮罩口令散列（PBKDF2-SHA256）。**只存散列、不存明文**；学生机本地比对，
    /// 所以"老师能解除遮罩"这件事不依赖教师机在线。
    /// 格式：pbkdf2-sha256$&lt;迭代&gt;$&lt;saltBase64&gt;$&lt;hashBase64&gt;
    /// </summary>
    public static class PasswordHash
    {
        private const int Iterations = 100000;
        private const int SaltBytes = 16;

        public static string Create(string password)
        {
            if (string.IsNullOrEmpty(password)) throw new ArgumentException("口令不能为空", nameof(password));
            var salt = new byte[SaltBytes];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(salt);
            byte[] hash = Pbkdf2(password, salt, Iterations);
            return "pbkdf2-sha256$" + Iterations + "$" + Convert.ToBase64String(salt) + "$" + Convert.ToBase64String(hash);
        }

        public static bool Verify(string password, string stored)
        {
            if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(stored)) return false;
            string[] parts = stored.Split('$');
            if (parts.Length != 4 || parts[0] != "pbkdf2-sha256") return false;
            int iters;
            if (!int.TryParse(parts[1], out iters) || iters <= 0) return false;
            byte[] salt, expect;
            try { salt = Convert.FromBase64String(parts[2]); expect = Convert.FromBase64String(parts[3]); }
            catch { return false; }
            byte[] actual = Pbkdf2(password, salt, iters);
            if (actual.Length != expect.Length) return false;
            int diff = 0;
            for (int i = 0; i < actual.Length; i++) diff |= actual[i] ^ expect[i];   // 定长比较，防时序侧信道
            return diff == 0;
        }

        private static byte[] Pbkdf2(string password, byte[] salt, int iterations)
        {
            using (var p = new Rfc2898DeriveBytes(password, salt, iterations))
                return p.GetBytes(32);
        }
    }
}
