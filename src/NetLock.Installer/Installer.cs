using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Text;

namespace NetLock.Setup
{
    /// <summary>
    /// 安装器内核（教师端/学生端两个安装包共用这一份代码，只是编译常量与图标不同）。
    ///
    /// 做法：**把 dist\ 打成 payload.zip 内嵌进 exe**（单文件），运行时解压到临时目录，
    /// 再调被装进去的程序自己的 `--install`（教师端与学生端的部署逻辑都写在各自 exe 里，
    /// 与 PowerShell 脚本共用同一份实现，避免"脚本能装、安装包装不上"这种漂移）。
    /// </summary>
    public static class Installer
    {
#if ROLE_TEACHER
        public const string DefaultRole = "teacher";
#else
        public const string DefaultRole = "student";
#endif
        public const string PayloadName = "payload.zip";

        public static readonly string[] RequiredFiles =
        {
            "NetLock.Teacher.exe", "NetLock.Student.exe", "NetLock.Shared.dll",
            "install-teacher.ps1", "install-student.ps1", "cleanup-all.ps1", "README.md",
        };

        public static string RoleTitle(string role)
        {
            return role == "teacher" ? "教师端（NetLock.Teacher）" : "学生端（NetLock.Student）";
        }

        /// <summary>解压内嵌 payload 到临时目录；失败给出原因。</summary>
        public static bool Extract(out string dir, out string error)
        {
            dir = null; error = null;
            try
            {
                Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadName);
                if (s == null)
                {
                    error = "这个 exe 里没有内嵌程序包（payload.zip）—— 请用 scripts\\build-installer.ps1 重新生成安装包。";
                    return false;
                }
                dir = Path.Combine(Path.GetTempPath(), "netlock-setup-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(dir);
                string zipPath = Path.Combine(dir, "payload.zip");
                using (var fs = File.Create(zipPath)) s.CopyTo(fs);
                ZipFile.ExtractToDirectory(zipPath, dir);
                File.Delete(zipPath);
                return true;
            }
            catch (Exception ex) { error = "解压内嵌程序包失败：" + ex.Message; return false; }
        }

        /// <summary>自检（构建脚本与 CI 会跑）：解压 + 校验必需文件都在 + 打印清单。</summary>
        public static int SelfTest()
        {
            Console.WriteLine("NetLock 安装包自检（角色：" + DefaultRole + "）");
            string dir, err;
            if (!Extract(out dir, out err)) { Console.WriteLine("失败：" + err); return 1; }
            var missing = RequiredFiles.Where(f => !File.Exists(Path.Combine(dir, f))).ToList();
            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
            Console.WriteLine("  解压到：" + dir);
            Console.WriteLine("  程序包内文件：" + files.Length + " 个");
            foreach (string f in files.OrderBy(x => x)) Console.WriteLine("    " + f.Substring(dir.Length + 1));
            if (missing.Count > 0)
            {
                Console.WriteLine("失败：内嵌程序包缺少 " + string.Join(", ", missing));
                return 1;
            }
            Console.WriteLine("自检通过 ✅");
            return 0;
        }

        public static int Install(string role, string pairFile, string targetDir, Action<string> log = null)
        {
            log = log ?? Console.WriteLine;
            if (!IsAdmin()) { log("失败：安装需要以管理员身份运行（右键 → 以管理员身份运行）。"); return 2; }
            string dir, err;
            if (!Extract(out dir, out err)) { log("失败：" + err); return 1; }
            log("已解压程序包 → " + dir);

            if (string.IsNullOrWhiteSpace(targetDir))
                targetDir = role == "teacher" ? @"C:\Program Files\NetLockTeacher" : @"C:\Program Files\NetLock";

            log("复制程序 → " + targetDir);
            Directory.CreateDirectory(targetDir);
            foreach (string f in Directory.GetFiles(dir))
                File.Copy(f, Path.Combine(targetDir, Path.GetFileName(f)), true);
            foreach (string sub in Directory.GetDirectories(dir))
                CopyDir(sub, Path.Combine(targetDir, Path.GetFileName(sub)));

            if (role == "teacher")
            {
                log("配置教师端（URLACL + 防火墙 + 登录自启）…");
                return Run(Path.Combine(targetDir, "NetLock.Teacher.exe"), "--install --silent", log);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(pairFile) || !File.Exists(pairFile))
                {
                    log("失败：学生端安装必须提供教师机导出的**配对文件**（netlock-pairing.json）。");
                    return 2;
                }
                log("配置学生端（服务 + 遮罩任务 + 权限 + 导入配对）…");
                return Run(Path.Combine(targetDir, "NetLock.Student.exe"), "--install --pair \"" + pairFile + "\"", log);
            }
        }

        public static int Uninstall(string role, Action<string> log = null)
        {
            log = log ?? Console.WriteLine;
            if (!IsAdmin()) { log("失败：卸载需要管理员身份。"); return 2; }
            string exe = role == "teacher"
                ? @"C:\Program Files\NetLockTeacher\NetLock.Teacher.exe"
                : @"C:\Program Files\NetLock\NetLock.Student.exe";
            if (!File.Exists(exe))
            {
                log("找不到已安装的程序：" + exe + "（可能装在别的目录，或已经卸载过）");
                return 1;
            }
            return Run(exe, "--uninstall", log);
        }

        private static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            foreach (string d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        private static int Run(string exe, string args, Action<string> log)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                };
                using (Process p = Process.Start(psi))
                {
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) log(e.Data); };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) log("  [err] " + e.Data); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    p.WaitForExit(120000);
                    int rc = p.HasExited ? p.ExitCode : -1;
                    log("（" + Path.GetFileName(exe) + " 退出码 " + rc + "）");
                    return rc;
                }
            }
            catch (Exception ex) { log("调用失败：" + ex.Message); return -1; }
        }

        public static bool IsAdmin()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }
}
