using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;

namespace NetLock.Teacher
{
    /// <summary>
    /// 教师机部署（**从 install-teacher.ps1 挪进来的**，让"脚本"和"安装包"共用同一份实现）：
    ///   ① URLACL：给 http://+:&lt;port&gt;/ 放行（否则 HttpListener 绑不上通配前缀，学生机永远连不上）
    ///   ② 防火墙：放行 TCP port（学生机要能连进来）
    ///   ③ 登录自启：建计划任务 NetLock\Teacher（老师登录后自动起托盘）
    /// </summary>
    internal static class Install
    {
        public const string TaskName = @"NetLock\Teacher";

        public static int Run(int port, bool silent)
        {
            if (!IsAdmin())
            {
                Console.WriteLine("失败：安装需要**以管理员身份**运行。");
                return 2;
            }
            string exe = Process.GetCurrentProcess().MainModule.FileName;

            Console.WriteLine("① 设置 URLACL： http://+:" + port + "/");
            Run("netsh.exe", "http delete urlacl url=http://+:" + port + "/");
            Run("netsh.exe", "http add urlacl url=http://+:" + port + "/ user=Everyone");

            Console.WriteLine("② 放行防火墙 TCP " + port);
            Run("netsh.exe", "advfirewall firewall delete rule name=\"NetLock Teacher\"");
            Run("netsh.exe", "advfirewall firewall add rule name=\"NetLock Teacher\" dir=in action=allow protocol=TCP localport=" + port);

            Console.WriteLine("③ 建登录自启计划任务：" + TaskName);
            int rc = CreateLogonTask(exe);
            if (rc != 0) Console.WriteLine("  ⚠ 建任务失败（rc=" + rc + "）：老师也可以手工双击 NetLock.Teacher.exe 启动");
            Run("schtasks.exe", "/run /tn \"" + TaskName + "\"");

            Console.WriteLine();
            Console.WriteLine("完成。接下来：");
            Console.WriteLine("  1) 右下角托盘出现 NetLock 图标 → 打开设置 → 勾「拔网线显示遮罩」、填 DNS、**设置解遮罩口令** → 保存并下发");
            Console.WriteLine("  2) 「导出配对文件…」→ 把 netlock-pairing.json 拷到 U 盘（学生机装的时候要用）");
            Console.WriteLine("  3) 监听地址：http://+:" + port + "/   数据目录：" + TeacherState.Dir);
            if (!silent) { }
            return 0;
        }

        public static int Uninstall(int port)
        {
            if (!IsAdmin()) { Console.WriteLine("失败：卸载需要管理员身份。"); return 2; }
            Run("schtasks.exe", "/delete /tn \"" + TaskName + "\" /f");
            Run("netsh.exe", "http delete urlacl url=http://+:" + port + "/");
            Run("netsh.exe", "advfirewall firewall delete rule name=\"NetLock Teacher\"");
            Console.WriteLine("已删除：登录自启任务、URLACL、防火墙规则。");
            Console.WriteLine("数据目录仍保留：" + TeacherState.Dir + "（要用 cleanup-all.ps1 -PurgeData 才删）");
            return 0;
        }

        /// <summary>登录自启任务：用 XML 导入（Command/Arguments 分开写，避开 schtasks /tr 的多层转义）。</summary>
        private static int CreateLogonTask(string exe)
        {
            string xmlPath = Path.Combine(Path.GetTempPath(), "netlock-teacher-task.xml");
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers>\r\n" +
                "  <Principals><Principal id=\"Author\"><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
                "  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>" +
                "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries></Settings>\r\n" +
                "  <Actions Context=\"Author\"><Exec><Command>" + System.Security.SecurityElement.Escape(exe) +
                "</Command></Exec></Actions>\r\n" +
                "</Task>\r\n";
            try { File.WriteAllText(xmlPath, xml, new UnicodeEncoding(false, true)); }
            catch (Exception ex) { Console.WriteLine("  ⚠ 写任务 XML 失败：" + ex.Message); return -1; }
            Run("schtasks.exe", "/delete /tn \"" + TaskName + "\" /f");
            int rc = Run("schtasks.exe", "/create /tn \"" + TaskName + "\" /xml \"" + xmlPath + "\" /f");
            try { File.Delete(xmlPath); } catch { }
            return rc;
        }

        private static bool IsAdmin()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private static int Run(string file, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args)
                {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                using (Process p = Process.Start(psi)) { p.WaitForExit(20000); return p.HasExited ? p.ExitCode : -1; }
            }
            catch { return -1; }
        }
    }
}
