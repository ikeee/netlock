using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using NetLock.Shared;

namespace NetLock.Setup
{
    /// <summary>
    /// 双击即装的向导（两页都不要，只一屏）：说明 → 选配对文件（学生端）→ 装/卸，日志实时打在下面。
    /// </summary>
    internal sealed class InstallerForm : Form
    {
        private readonly string _role;
        private readonly TextBox _log = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill, BackColor = Color.FromArgb(24, 26, 30), ForeColor = Color.Gainsboro,
            Font = new Font("Consolas", 9F), WordWrap = false,
        };
        private readonly TextBox _pair = new TextBox { Width = 380, ReadOnly = true };
        private readonly Button _install = new Button { Text = "开始安装", Width = 130, Height = 34 };
        private readonly Button _uninstall = new Button { Text = "卸载", Width = 90, Height = 34 };

        public InstallerForm(string role)
        {
            _role = role;
            Icon = AppIcon.Get();
            Text = AppInfo.ProductName + " 安装向导 —— " + Installer.RoleTitle(role) + "  v" + AppInfo.Version;
            Font = new Font("微软雅黑", 9F);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(760, 520);
            MinimumSize = new Size(700, 440);
            BackColor = Color.FromArgb(248, 249, 251);

            _install.BackColor = Color.FromArgb(0, 120, 215);
            _install.ForeColor = Color.White;
            _install.FlatStyle = FlatStyle.Flat;
            _install.Click += (s, e) => DoInstall();
            _uninstall.FlatStyle = FlatStyle.Flat;
            _uninstall.Click += (s, e) => DoUninstall();

            var top = new Panel { Dock = DockStyle.Top, Height = 132, BackColor = Color.White, Padding = new Padding(16, 12, 16, 8) };
            var title = new Label
            {
                Text = "将安装：" + Installer.RoleTitle(role), AutoSize = true, Left = 0, Top = 0,
                Font = new Font("微软雅黑", 12F, FontStyle.Bold),
            };
            var what = new Label
            {
                Text = role == "teacher"
                    ? "教师端会：拷程序到 C:\\Program Files\\NetLockTeacher；放行 http://+:8210/（URLACL）与防火墙；\n" +
                      "建「登录时启动」计划任务；装完请在托盘里设置遮罩开关 / DNS / 解遮罩口令，并导出配对文件。"
                    : "学生端会：拷程序到 C:\\Program Files\\NetLock；注册服务（开机自启）；建「遮罩」计划任务；\n" +
                      "收紧数据目录权限；并导入下面这个**配对文件**（等于钥匙，由教师机导出）。",
                AutoSize = true, Left = 0, Top = 26, ForeColor = Color.FromArgb(90, 95, 105),
            };
            var pairLabel = new Label { Text = "配对文件：", AutoSize = true, Left = 0, Top = 80 };
            _pair.SetBounds(72, 77, 380, 24);
            var browse = new Button { Text = "选择…", Left = 460, Top = 76, Width = 80, Height = 26, FlatStyle = FlatStyle.Flat };
            browse.Click += (s, e) => BrowsePair();
            var adminHint = new Label
            {
                Text = Installer.IsAdmin() ? "✔ 当前已是管理员权限" : "⚠ 当前不是管理员：安装/卸载会失败，请右键 → 以管理员身份运行",
                AutoSize = true, Left = 556, Top = 80,
                ForeColor = Installer.IsAdmin() ? Color.FromArgb(0, 130, 80) : Color.FromArgb(200, 90, 0),
            };
            top.Controls.AddRange(new Control[] { title, what, pairLabel, _pair, browse, adminHint });
            if (role == "student") { } else { _pair.Enabled = false; browse.Enabled = false; pairLabel.Text = "配对文件："; }

            var actions = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Color.White };
            _install.SetBounds(16, 9, 130, 34);
            _uninstall.SetBounds(156, 9, 90, 34);
            var close = new Button { Text = "关闭", Left = 640, Top = 9, Width = 90, Height = 34, FlatStyle = FlatStyle.Flat };
            close.Click += (s, e) => Close();
            var autoMsg = new Label
            {
                Text = "（无人值守：NetLock-Setup.exe --silent 或 --role student --pair xxx.json）",
                AutoSize = true, Left = 260, Top = 19, ForeColor = Color.FromArgb(150, 155, 165),
            };
            actions.Controls.AddRange(new Control[] { _install, _uninstall, autoMsg, close });

            Controls.Add(_log);
            Controls.Add(actions);
            Controls.Add(top);

            Log(AppInfo.ProductName + " 安装包 v" + AppInfo.Version + "（角色：" + role + "）");
            Log("内嵌程序包：" + string.Join("、", Installer.RequiredFiles));
            if (role == "student") Log("提示：请先在教师机上「导出配对文件…」，再在这里选它。");
        }

        private void BrowsePair()
        {
            using (var dlg = new OpenFileDialog { Title = "选择教师机导出的配对文件", Filter = "配对文件|*.json" })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK) _pair.Text = dlg.FileName;
            }
        }

        private void DoInstall()
        {
            _install.Enabled = false; _uninstall.Enabled = false;
            try
            {
                Log("=== 开始安装 ===");
                int rc = Installer.Install(_role, _pair.Text, null, Log);
                Log(rc == 0 ? "=== 安装成功 ===" : ("=== 安装失败（退出码 " + rc + "）==="));
                if (rc == 0) Log(_role == "teacher"
                    ? "下一步：右下角托盘 → 打开设置 → 填 DNS / 勾遮罩 / 设解遮罩口令 → 保存并下发 → 导出配对文件"
                    : "下一步：到教师端「学生机」页看这台机有没有出现、已应用版本是否与教师端一致");
            }
            catch (Exception ex) { Log("异常：" + ex); }
            finally { _install.Enabled = true; _uninstall.Enabled = true; }
        }

        private void DoUninstall()
        {
            if (MessageBox.Show(this, "确定卸载 " + Installer.RoleTitle(_role) + "？\n（会删服务/计划任务/URLACL；数据目录保留）", Text,
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            Log("=== 开始卸载 ===");
            int rc = Installer.Uninstall(_role, Log);
            Log(rc == 0 ? "=== 卸载完成 ===" : ("=== 卸载失败（退出码 " + rc + "）==="));
        }

        private void Log(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            _log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + line + Environment.NewLine);
        }
    }
}
