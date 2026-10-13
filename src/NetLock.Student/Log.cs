using System;
using System.IO;
using System.Text;

namespace NetLock.Student
{
    /// <summary>极简日志：控制台 + 数据目录 logs\（服务模式下没有控制台，日志就是唯一线索）。</summary>
    public static class Log
    {
        private static readonly object Gate = new object();

        public static void Write(string msg)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg;
            lock (Gate)
            {
                try { Console.WriteLine(line); } catch { }
                try
                {
                    Directory.CreateDirectory(Path.Combine(StudentConfig.DataDir, "logs"));
                    File.AppendAllText(
                        Path.Combine(StudentConfig.DataDir, "logs", "netlock-" + DateTime.Now.ToString("yyyyMMdd") + ".log"),
                        line + Environment.NewLine, new UTF8Encoding(false));
                }
                catch { }
            }
        }
    }
}
