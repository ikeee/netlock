using System.IO;
using System.Text;

namespace NetLock.Teacher
{
    /// <summary>安全写入（写临时文件 → 原子替换）：断电/崩溃不留半截文件。</summary>
    public static class Util
    {
        public static void AtomicWrite(string path, string text)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(text ?? "");
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, null, true); return; }
                catch { try { File.Delete(path); } catch { } }
            }
            File.Move(tmp, path);
        }
    }
}
