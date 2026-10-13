using System;
using System.Linq;
using System.Windows.Forms;
using NetLock.Shared;

namespace NetLock.Setup
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            args = args ?? new string[0];
            string role = Value(args, "--role") ?? Installer.DefaultRole;
            if (role != "teacher" && role != "student") role = Installer.DefaultRole;

            // 命令行/无人值守：--silent（默认角色）或 --role student --pair f.json
            if (Has(args, "--selftest")) { Environment.ExitCode = Installer.SelfTest(); return; }
            if (Has(args, "--uninstall")) { Environment.ExitCode = Installer.Uninstall(role); return; }
            if (Has(args, "--silent"))
            {
                Environment.ExitCode = Installer.Install(role, Value(args, "--pair"), Value(args, "--dir"));
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new InstallerForm(role));
        }

        private static bool Has(string[] a, string n) { return a.Any(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase)); }
        private static string Value(string[] a, string n)
        {
            int i = Array.IndexOf(a, n);
            return (i >= 0 && a.Length > i + 1) ? a[i + 1] : null;
        }
    }
}
