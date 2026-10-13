using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using NetLock.Shared;

namespace NetLock.Student
{
    internal static class Program
    {
        private const string ServiceName = "NetLockSvc";
        private const string MaskTask = StudentEngine.MaskTaskName;

        [STAThread]
        private static void Main(string[] args)
        {
            args = args ?? new string[0];

            // --data-dir <目录>：测试/便携模式（只影响本进程；一台机器上可以跑多个沙箱）
            string dir = Value(args, "--data-dir");
            if (!string.IsNullOrWhiteSpace(dir)) StudentConfig.DataDir = Path.GetFullPath(dir);

            // 遮罩模式（由服务用计划任务拉进当前登录会话；也可以手工跑来看效果）
            if (Has(args, "--mask")) { RunMask(); return; }

            var cfg = StudentConfig.Load();

            if (Has(args, "--pair"))
            {
                string file = Value(args, "--pair");
                if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
                {
                    Out("用法：NetLock.Student.exe --pair <教师机导出的配对文件.json> [--data-dir 目录]");
                    Environment.ExitCode = 2;
                    return;
                }
                try
                {
                    StudentConfig.ImportPairing(file, cfg);
                    Out("已配对：" + cfg.Pairing.Address + "  密钥指纹 " + ConnKey.Fingerprint(cfg.Pairing.Key));
                    Out("数据目录：" + StudentConfig.DataDir);
                }
                catch (Exception ex)
                {
                    Out("配对失败：" + ex.Message);
                    Environment.ExitCode = 1;
                }
                return;
            }

            if (Has(args, "--status"))
            {
                Out(AppInfo.ProductName + " 学生端 v" + AppInfo.Version + "（数据目录 " + StudentConfig.DataDir + "）");
                Out("  已配对        = " + cfg.IsPaired + (cfg.IsPaired ? "（" + cfg.Pairing.Address + " 指纹 " + cfg.Pairing.Fingerprint + "）" : ""));
                Out("  已应用版本    = " + cfg.AppliedVersion);
                Out("  遮罩开关      = " + (cfg.MaskOn ? "开" : "关") + "（本地缓存，来自教师机下发）");
                Out("  解遮罩口令    = " + (string.IsNullOrEmpty(cfg.MaskPasswordHash) ? "未收到（遮罩只能用后路解除）" : "已设置"));
                Out("  老师已解除    = " + (StudentEngine.ReadAck(cfg.Pairing.Key) == DateTime.MinValue ? "否" : "是（本次断网不再弹）"));
                Out("  遮罩正在显示  = " + StudentEngine.IsMaskRunning());
                Out("  最近错误      = " + (string.IsNullOrEmpty(cfg.LastError) ? "（无）" : cfg.LastError));
                Out("  有线网卡：");
                foreach (NicStatus n in WiredNic.Snapshot())
                    Out("    " + n + "  dns=" + (n.Dns.Count == 0 ? "-" : string.Join(",", n.Dns)));
                return;
            }

            if (Has(args, "--install")) { Install(Value(args, "--pair")); return; }
            if (Has(args, "--uninstall")) { Uninstall(Has(args, "--restore-dns")); return; }

            if (Has(args, "--console"))
            {
                int seconds;
                int.TryParse(Value(args, "--seconds"), out seconds);
                ConsoleMode(cfg, seconds);
                return;
            }

            if (Has(args, "--service") || !Environment.UserInteractive)
            {
                ServiceBase.Run(new ServiceBase[] { new StudentService(cfg) });
                return;
            }

            Out("这是学生端后台程序。常用命令：");
            Out("  --pair <配对文件.json>   导入教师机配对");
            Out("  --install [--pair f.json] 安装服务 + 遮罩计划任务 + 目录权限（需管理员）");
            Out("  --console [--seconds N]  前台调试模式（能看到全部日志）");
            Out("  --status                 看当前状态");
            Out("  --mask                   手工显示全屏遮罩（看效果用）");
            Out("  --uninstall [--restore-dns]  卸载；加 --restore-dns 会把有线网卡 DNS 还原为自动获取");
        }

        /// <summary>
        /// 只在"该管"的环境里运行：**安全模式不加载**（给老师留一条后路：出问题/不再使用都能进安全模式处理）。
        /// </summary>
        public static bool ShouldRunInThisEnvironment(out string why)
        {
            why = "";
            try
            {
                if (GetSystemMetrics(SM_CLEANBOOT) != 0) { why = "当前是安全模式（按设计不加载）"; return false; }
            }
            catch { }
            return true;
        }

        private const int SM_CLEANBOOT = 67;
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);

        // ------------------------------------------------------------------ 前台调试模式
        private static void ConsoleMode(StudentConfig cfg, int seconds)
        {
            Log.Write(AppInfo.ProductName + " 学生端 v" + AppInfo.Version + "（前台模式，数据目录 " + StudentConfig.DataDir + "）");
            if (!ShouldRunInThisEnvironment(out string why)) { Log.Write("不加载管控：" + why); return; }
            if (!cfg.IsPaired) { Log.Write("⚠ 还没配对（先 --pair 导入教师机导出的配对文件）"); }
            using (var engine = new StudentEngine(cfg, Log.Write))
            {
                engine.Start();
                if (seconds > 0)
                {
                    Log.Write("运行 " + seconds + " 秒后自动退出…");
                    for (int i = 0; i < seconds; i++) Thread.Sleep(1000);
                }
                else
                {
                    Log.Write("按回车退出（或 Ctrl+C）…");
                    try { Console.ReadLine(); } catch { Thread.Sleep(Timeout.Infinite); }
                }
                Log.Write("正在退出…状态：已应用版本=" + cfg.AppliedVersion + " 遮罩=" + (engine.MaskShown ? "显示中" : "未显示") +
                          "（" + engine.WaitReason + "）" + (string.IsNullOrEmpty(cfg.LastError) ? "" : "  最近错误=" + cfg.LastError));
            }
        }

        // ------------------------------------------------------------------ 遮罩模式
        private static void RunMask()
        {
            var cfg = StudentConfig.Load();
            // 同一时间只允许一个遮罩：服务创建了 Global\ 互斥体，我们"打开并抢到"才算拿到
            Mutex slot = null;
            try
            {
                slot = Mutex.OpenExisting(StudentEngine.MaskMutexName);
                if (!slot.WaitOne(0)) { return; }      // 已经有一个遮罩在显示 → 直接退出
            }
            catch { /* 没有互斥体（手工跑）→ 照常显示 */ }
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MaskForm(cfg.Pairing.Key, cfg.MaskPasswordHash));
            }
            finally
            {
                try { slot?.ReleaseMutex(); } catch { }
            }
        }

        // ------------------------------------------------------------------ 安装 / 卸载
        private static void Install(string pairFile)
        {
            if (!IsAdmin()) { Out("失败：安装需要**以管理员身份**运行（右键 → 以管理员身份运行）。"); Environment.ExitCode = 2; return; }
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            if (!string.IsNullOrWhiteSpace(pairFile)) { var cfg = StudentConfig.Load(); StudentConfig.ImportPairing(pairFile, cfg); Out("已导入配对：" + cfg.Pairing.Address); }

            Directory.CreateDirectory(StudentConfig.DataDir);
            HardDir(StudentConfig.DataDir);
            Out("数据目录：" + StudentConfig.DataDir + "（权限：仅管理员/SYSTEM，学生读不到连接密钥）");

            Run("schtasks.exe", "/delete /tn \"" + MaskTask + "\" /f");
            // 用 **XML 导入**建任务，而不是 `/tr "命令行"`：路径带空格 + 额外的 --mask 参数，
            // 在 /tr 里要写 `"\"path\" --mask"` 这种多层转义（真机实测 schtasks 会解析错，
            // 把 Command 存成 `"\`）；XML 里 Command/Arguments 分开写，没有引号问题。
            int rc1 = CreateMaskTask(exe);
            Out(rc1 == 0 ? "已创建遮罩计划任务：" + MaskTask + "（按需运行，跑在当前登录会话里）"
                         : "⚠ 创建遮罩计划任务失败（rc=" + rc1 + "）—— 服务将退化为直接启动遮罩（只在当前会话有效）");

            Run("sc.exe", "stop " + ServiceName);
            Run("sc.exe", "delete " + ServiceName);
            int rc2 = Run("sc.exe", "create " + ServiceName + " binPath= \"" + exe + " --service\" start= auto obj= LocalSystem DisplayName= \"NetLock 学生端（拔网线遮罩 + DNS）\"");
            Run("sc.exe", "description " + ServiceName + " \"NetLock：只做两件事——拔网线全屏遮罩、接收教师机下发的 DNS\"");
            Run("sc.exe", "failure " + ServiceName + " actions= restart/60000/restart/60000/restart/60000 reset= 900");
            int rc3 = Run("sc.exe", "start " + ServiceName);
            Out(rc2 == 0 ? "已注册服务：" + ServiceName + "（开机自启）" : "⚠ 注册服务失败（rc=" + rc2 + "）");
            Out(rc3 == 0 ? "服务已启动" : "⚠ 服务启动失败（rc=" + rc3 + "），可到「服务」里手动启动看报错");
            Out("完成。看状态：NetLock.Student.exe --status");
        }

        /// <summary>
        /// 用 XML 导入建"遮罩"任务：无触发器（只能手动 /run）、最高权限、在**当前登录会话**里跑。
        /// 为什么不用 `/tr "命令行"`：路径带空格 + 额外的 --mask 参数，/tr 要写多层转义
        /// （`"\"path\" --mask"`），真机实测 schtasks 会把 Command 存成 `"\` 而解析错；
        /// XML 里 Command / Arguments 分开写，没有引号问题。
        /// </summary>
        private static int CreateMaskTask(string exe)
        {
            string xmlPath = Path.Combine(Path.GetTempPath(), "netlock-mask-task.xml");
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <Principals><Principal id=\"Author\"><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
                "  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy></Settings>\r\n" +
                "  <Actions Context=\"Author\"><Exec><Command>" +
                System.Security.SecurityElement.Escape(exe) + "</Command><Arguments>--mask</Arguments></Exec></Actions>\r\n" +
                "</Task>\r\n";
            try { File.WriteAllText(xmlPath, xml, new UnicodeEncoding(false, true)); }
            catch (Exception ex) { Out("⚠ 写任务 XML 失败：" + ex.Message); return -1; }
            int rc = Run("schtasks.exe", "/create /tn \"" + MaskTask + "\" /xml \"" + xmlPath + "\" /f");
            try { File.Delete(xmlPath); } catch { }
            return rc;
        }

        private static void Uninstall(bool restoreDns)
        {
            if (!IsAdmin()) { Out("失败：卸载需要管理员身份。"); Environment.ExitCode = 2; return; }
            Run("sc.exe", "stop " + ServiceName);
            Run("sc.exe", "delete " + ServiceName);
            Run("schtasks.exe", "/delete /tn \"" + MaskTask + "\" /f");
            Out("已删除服务与遮罩计划任务。");
            if (restoreDns)
            {
                foreach (NicStatus n in WiredNic.Snapshot())
                {
                    Run("netsh.exe", "interface ip set dns name=\"" + n.Name + "\" source=dhcp");
                    Out("已把「" + n.Name + "」的 DNS 还原为自动获取");
                }
                Run("ipconfig.exe", "/flushdns");
            }
            else Out("（DNS 未改动；要还原成自动获取请加 --restore-dns）");
            Out("数据目录仍保留：" + StudentConfig.DataDir + "（含连接密钥；确认不再使用可手工删除）");
        }

        /// <summary>目录权限：只给 Administrators / SYSTEM（学生读不到连接密钥、也伪造不了"老师已解除"标记）。</summary>
        private static void HardDir(string dir)
        {
            try
            {
                var info = new DirectoryInfo(dir);
                DirectorySecurity sec = info.GetAccessControl();
                sec.SetAccessRuleProtection(true, false);
                var admin = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                sec.AddAccessRule(new FileSystemAccessRule(admin, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                info.SetAccessControl(sec);
            }
            catch (Exception ex) { Out("⚠ 设置目录权限失败（学生可能读到连接密钥）：" + ex.Message); }
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

        // ------------------------------------------------------------------ 小工具
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

        private static bool Has(string[] args, string name) { return args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)); }
        private static string Value(string[] args, string name)
        {
            int i = Array.IndexOf(args ?? new string[0], name);
            return (i >= 0 && args.Length > i + 1) ? args[i + 1] : null;
        }
        private static void Out(string s) { Console.WriteLine(s); }

    }
}
