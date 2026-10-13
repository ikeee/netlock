using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using NetLock.Shared;

namespace NetLock.Teacher
{
    internal static class Program
    {
        public static TeacherState State;
        public static ApiServer Api;
        private static NotifyIcon _tray;
        private static SettingsForm _form;

        [STAThread]
        private static void Main(string[] args)
        {
            args = args ?? new string[0];
            // --data-dir <目录>：测试/便携（只影响本进程，不碰真机 %ProgramData%\NetLockTeacher）
            string dataDir = Value(args, "--data-dir");
            if (!string.IsNullOrWhiteSpace(dataDir)) TeacherState.DirOverride = Path.GetFullPath(dataDir);
            State = TeacherState.LoadOrCreate();

            // 无界面模式（给脚本/无人值守用）：--headless [--mask-on 1|0] [--dns "1.1.1.1,8.8.8.8"]
            //   [--mask-password xxx] [--port 8210] [--no-save] [--export-pairing f.json]
            //   [--print-state] [--seconds N]
            if (Has(args, "--headless")) { Headless(args); return; }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            StartApi(State.Port);
            BuildTray();
            Application.Run();
        }

        // ------------------------------------------------------------------ 无界面（脚本/测试用）
        private static void Headless(string[] args)
        {
            Out(AppInfo.ProductName + " 教师端（无界面）  版本 " + AppInfo.Version);
            bool dirty = false;
            string dns = Value(args, "--dns");
            if (dns != null) { State.Dns = DnsList.Parse(dns); dirty = true; }
            string mask = Value(args, "--mask-on");
            if (mask != null) { State.MaskOn = mask == "1" || mask.Equals("true", StringComparison.OrdinalIgnoreCase); dirty = true; }
            string pwd = Value(args, "--mask-password");
            if (!string.IsNullOrEmpty(pwd)) { State.MaskPasswordHash = PasswordHash.Create(pwd); dirty = true; }
            string portArg = Value(args, "--port");
            int port;
            if (int.TryParse(portArg, out port) && port > 0) { State.Port = port; dirty = true; }
            if (dirty && !Has(args, "--no-save"))
            {
                State.Version++;                       // 每次保存 +1（学生机只认更大的版本）
                State.Save();
            }
            Out("状态：version=" + State.Version + "  maskOn=" + State.MaskOn +
                "  dns=" + (State.Dns.Count == 0 ? "（未设置）" : DnsList.Join(State.Dns)) +
                "  密钥指纹=" + ConnKey.Fingerprint(State.Key));

            string pairing = Value(args, "--export-pairing");
            if (!string.IsNullOrEmpty(pairing))
            {
                State.ExportPairing(pairing);
                Out("已导出配对文件：" + pairing);
            }
            if (Has(args, "--print-state")) Out(Protocol.ToJson(State.ToDesired(), State.Key));
            if (!Has(args, "--serve")) return;         // 只改状态/导出配对 → 立刻退出

            if (!StartApi(State.Port)) { Out("失败：HTTP 监听启动失败（端口被占用或无权限）"); Environment.ExitCode = 2; return; }
            Out("已监听 " + Api.BoundPrefix + "（学生机每 2 秒轮询一次）");
            int seconds;
            int.TryParse(Value(args, "--seconds"), out seconds);
            if (seconds > 0)
            {
                for (int i = 0; i < seconds; i++) Thread.Sleep(1000);
            }
            else
            {
                Out("按 Ctrl+C 结束。");
                var quit = new ManualResetEventSlim(false);
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; quit.Set(); };
                quit.Wait();
            }
            Out("学生机一览（最近 30 秒内有上报）：");
            DeviceReport[] list = Api.Snapshot();
            if (list.Length == 0) Out("  （无）");
            foreach (DeviceReport d in list)
                Out("  " + d.Machine + "  " + d.Ip + "  appliedVersion=" + d.AppliedVersion +
                    (State.Version > 0 && d.AppliedVersion >= State.Version ? "（已是最新）" : "（待更新）") +
                    "  dns=" + (string.IsNullOrEmpty(d.Dns) ? "-" : d.Dns) +
                    "  nic=" + (string.IsNullOrEmpty(d.Nic) ? "-" : d.Nic) +
                    (d.MaskShown ? "  [遮罩中]" : "") +
                    (string.IsNullOrEmpty(d.Error) ? "" : "  error=" + d.Error));
            Api.Stop();
        }

        private static bool StartApi(int port)
        {
            Api = new ApiServer(State);
            return Api.Start(port);
        }

        // ------------------------------------------------------------------ 托盘
        private static void BuildTray()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("打开设置…", null, (s, e) => OpenForm());
            var maskItem = new ToolStripMenuItem("拔网线时显示遮罩") { CheckOnClick = false };
            maskItem.Click += (s, e) => { SaveState(!State.MaskOn, null); RefreshTray(); };
            menu.Items.Add(maskItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("导出配对文件…", null, (s, e) => ExportPairing());
            menu.Items.Add("打开数据目录", null, (s, e) =>
            {
                try { System.Diagnostics.Process.Start("explorer.exe", TeacherState.Dir); } catch { }
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (s, e) =>
            {
                if (MessageBox.Show("退出教师端？学生机将收不到新的 DNS/遮罩设置（已生效的设置仍然保持）。",
                    AppInfo.ProductName, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                    Application.Exit();
            });

            _tray = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Visible = true,
                ContextMenuStrip = menu,
            };
            _tray.DoubleClick += (s, e) => OpenForm();
            _tray.Tag = maskItem;
            RefreshTray();
        }

        private static void RefreshTray()
        {
            if (_tray == null) return;
            var item = _tray.Tag as ToolStripMenuItem;
            if (item != null) item.Checked = State.MaskOn;
            int online = Api?.Snapshot().Count(x => (DateTime.UtcNow - x.LastSeenUtc).TotalSeconds <= 30) ?? 0;
            _tray.Text = AppInfo.ProductName + " 教师端 v" + AppInfo.Version + "\n" +
                         (State.Version == 0 ? "尚未保存过设置（学生机会收到 503）"
                                             : "version=" + State.Version + "  遮罩=" + (State.MaskOn ? "开" : "关") +
                                               "  DNS=" + (State.Dns.Count == 0 ? "未设置" : string.Join(",", State.Dns))) +
                         "\n在线学生机：" + online + " 台";
        }

        /// <summary>保存并下发（version+1）。**这是"学生机立马更新"的唯一触发点。**</summary>
        public static void SaveState(bool? maskOn, List<string> dns)
        {
            if (maskOn.HasValue) State.MaskOn = maskOn.Value;
            if (dns != null) State.Dns = dns;
            State.Version++;
            State.Save();
            RefreshTray();
        }

        private static void OpenForm()
        {
            if (_form == null || _form.IsDisposed)
            {
                _form = new SettingsForm();
                _form.Show();
            }
            else _form.Activate();
        }

        private static void ExportPairing()
        {
            using (var dlg = new SaveFileDialog
            {
                Title = "导出配对文件（拷到学生机执行 NetLock.Student.exe --pair 这个文件）",
                Filter = "配对文件|*.json",
                FileName = "netlock-pairing.json",
            })
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;
                try
                {
                    State.ExportPairing(dlg.FileName);
                    MessageBox.Show(
                        "已导出：" + dlg.FileName +
                        "\n\n密钥指纹（学生机上核对用）：" + ConnKey.Fingerprint(State.Key) +
                        "\n\n⚠ 这个文件等于钥匙，请只放在教师机和你的 U 盘里。",
                        AppInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("导出失败：" + ex.Message, AppInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        // ------------------------------------------------------------------ 小工具
        private static bool Has(string[] args, string name) { return args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)); }
        private static string Value(string[] args, string name)
        {
            int i = Array.IndexOf(args ?? new string[0], name);
            return (i >= 0 && args.Length > i + 1) ? args[i + 1] : null;
        }
        private static void Out(string s) { Console.WriteLine(s); }
    }
}
