using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using NetLock.Shared;

namespace NetLock.Teacher
{
    /// <summary>设置窗体：**两页**——① 设置（遮罩开关 + DNS + 解遮罩口令）② 学生机（谁生效了）。</summary>
    internal sealed class SettingsForm : Form
    {
        private readonly CheckBox _mask = new CheckBox { Text = "拔掉网线（或禁用有线网卡）时，学生机显示全屏遮罩", AutoSize = true };
        private readonly TextBox _dns = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 90, Dock = DockStyle.Top };
        private readonly TextBox _pwd = new TextBox { UseSystemPasswordChar = true };
        private readonly TextBox _pwd2 = new TextBox { UseSystemPasswordChar = true };
        private readonly Label _status = new Label { Dock = DockStyle.Fill, ForeColor = Color.DimGray, TextAlign = ContentAlignment.MiddleLeft };
        private readonly ListView _devices = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
        private readonly Timer _poll = new Timer { Interval = 2000 };

        public SettingsForm()
        {
            Text = AppInfo.ProductName + " 教师端 v" + AppInfo.Version + "（只做两件事：拔网线遮罩 + 下发 DNS）";
            Font = new Font("微软雅黑", 9F);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(760, 520);
            MinimumSize = new Size(700, 460);
            BackColor = Color.FromArgb(248, 249, 251);

            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(BuildSettingsTab());
            tabs.TabPages.Add(BuildDevicesTab());
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 26, BackColor = Color.White };
            bottom.Controls.Add(_status);
            Controls.Add(tabs);
            Controls.Add(bottom);

            var s = Program.State;
            _mask.Checked = s.MaskOn;
            _dns.Text = string.Join(Environment.NewLine, s.Dns);
            _poll.Tick += (s2, e) => RefreshDevices();
            _poll.Start();
            RefreshDevices();
            RefreshStatus();
        }

        private TabPage BuildSettingsTab()
        {
            var page = new TabPage("设置") { BackColor = Color.White, Padding = new Padding(14) };
            var title1 = new Label
            {
                Text = "① 断网遮罩", Dock = DockStyle.Top, Height = 24,
                Font = new Font("微软雅黑", 10F, FontStyle.Bold),
            };
            var hint1 = new Label
            {
                Text = "只把**有线网卡**当判据（无线/热点时断时续，不算）：网卡被禁用、没拿到 IP、或只有 169.254.* 时\n" +
                       "判定为断网，学生机立刻弹全屏遮罩；插回网线自动消失。老师可用下面设置的口令在遮罩上解除。",
                Dock = DockStyle.Top, Height = 40, ForeColor = Color.FromArgb(110, 114, 120),
            };
            var title2 = new Label
            {
                Text = "② 下发 DNS（学生机收到后立刻更新）", Dock = DockStyle.Top, Height = 30,
                Font = new Font("微软雅黑", 10F, FontStyle.Bold),
            };
            var hint2 = new Label
            {
                Text = "一行一个（或逗号分隔）。**只改有线网卡的 DNS**，不动无线/热点网卡；留空 = 不下发、不强改。",
                Dock = DockStyle.Top, Height = 22, ForeColor = Color.FromArgb(110, 114, 120),
            };
            var title3 = new Label
            {
                Text = "③ 老师解除遮罩的口令（只存散列，学生机本地校验）", Dock = DockStyle.Top, Height = 30,
                Font = new Font("微软雅黑", 10F, FontStyle.Bold),
            };
            var pwdPanel = new Panel { Dock = DockStyle.Top, Height = 34 };
            pwdPanel.Controls.Add(new Label { Text = "口令：", Left = 0, Top = 8, AutoSize = true });
            _pwd.SetBounds(52, 4, 150, 24); pwdPanel.Controls.Add(_pwd);
            pwdPanel.Controls.Add(new Label { Text = "再输一次：", Left = 214, Top = 8, AutoSize = true });
            _pwd2.SetBounds(290, 4, 150, 24); pwdPanel.Controls.Add(_pwd2);
            var pwdHint = new Label
            {
                Text = "留空 = 不改动现有口令（首次请务必设置：否则遮罩只能用 cleanup 脚本或安全模式解除）",
                Left = 450, Top = 8, Width = 290, ForeColor = Color.FromArgb(150, 150, 150),
            };
            pwdPanel.Controls.Add(pwdHint);

            var actions = new Panel { Dock = DockStyle.Bottom, Height = 76 };
            var save = new Button
            {
                Text = "保存并下发（学生机 2 秒内生效）", Width = 260, Height = 34, Left = 0, Top = 6,
                BackColor = Color.FromArgb(0, 120, 215), ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
            };
            save.Click += (s, e) => SaveClicked();
            var export = new Button { Text = "导出配对文件…", Width = 150, Height = 34, Left = 270, Top = 6, FlatStyle = FlatStyle.Flat };
            export.Click += (s, e) => ExportPairing();
            var open = new Button { Text = "打开数据目录", Width = 130, Height = 34, Left = 430, Top = 6, FlatStyle = FlatStyle.Flat };
            open.Click += (s, e) => { try { System.Diagnostics.Process.Start("explorer.exe", TeacherState.Dir); } catch { } };
            actions.Controls.AddRange(new Control[] { save, export, open });

            page.Controls.Add(actions);
            page.Controls.Add(pwdPanel);
            page.Controls.Add(title3);
            page.Controls.Add(_dns);
            page.Controls.Add(hint2);
            page.Controls.Add(title2);
            page.Controls.Add(_mask);
            page.Controls.Add(hint1);
            page.Controls.Add(title1);
            return page;
        }

        private TabPage BuildDevicesTab()
        {
            _devices.Columns.Add("机器名", 140);
            _devices.Columns.Add("IP", 110);
            _devices.Columns.Add("已应用版本", 90);
            _devices.Columns.Add("状态", 80);
            _devices.Columns.Add("当前 DNS", 170);
            _devices.Columns.Add("判据网卡", 110);
            _devices.Columns.Add("遮罩", 50);
            _devices.Columns.Add("备注", 120);
            var page = new TabPage("学生机") { BackColor = Color.White, Padding = new Padding(8) };
            page.Controls.Add(_devices);
            return page;
        }

        private void SaveClicked()
        {
            var dns = DnsList.Parse(_dns.Text);
            string raw = (_dns.Text ?? "").Trim();
            if (raw.Length > 0 && dns.Count == 0)
            {
                MessageBox.Show(this, "DNS 里没有合法的 IPv4 地址，请检查。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string p1 = _pwd.Text ?? "", p2 = _pwd2.Text ?? "";
            if (p1.Length > 0 || p2.Length > 0)
            {
                if (p1 != p2) { MessageBox.Show(this, "两次输入的口令不一致。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                if (p1.Length < 4) { MessageBox.Show(this, "口令太短（至少 4 位）。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                Program.State.MaskPasswordHash = PasswordHash.Create(p1);
            }
            Program.SaveState(_mask.Checked, dns);
            _pwd.Text = ""; _pwd2.Text = "";
            RefreshStatus();
            MessageBox.Show(this,
                "已保存并下发：version=" + Program.State.Version + "\n" +
                "遮罩：" + (Program.State.MaskOn ? "启用" : "停用") + "\n" +
                "DNS：" + (dns.Count == 0 ? "（未设置，不下发）" : string.Join(", ", dns)) + "\n\n" +
                "学生机每 2 秒轮询一次，收到后**立刻**更新网卡 DNS。", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ExportPairing()
        {
            using (var dlg = new SaveFileDialog
            {
                Title = "导出配对文件（学生机 --pair 用；等于钥匙，别乱放）",
                Filter = "配对文件|*.json",
                FileName = "netlock-pairing.json",
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    Program.State.ExportPairing(dlg.FileName);
                    MessageBox.Show(this, "已导出：" + dlg.FileName + "\n密钥指纹：" + ConnKey.Fingerprint(Program.State.Key),
                        Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex) { MessageBox.Show(this, "导出失败：" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        private void RefreshDevices()
        {
            DeviceReport[] list = Program.Api?.Snapshot() ?? new DeviceReport[0];
            _devices.BeginUpdate();
            _devices.Items.Clear();
            foreach (DeviceReport d in list)
            {
                bool fresh = (DateTime.UtcNow - d.LastSeenUtc).TotalSeconds <= 30;
                string state = !fresh ? "离线"
                    : (Program.State.Version > 0 && d.AppliedVersion >= Program.State.Version ? "已生效" : "待更新");
                var it = new ListViewItem(new[]
                {
                    d.Machine, d.Ip, d.AppliedVersion.ToString(), state,
                    string.IsNullOrEmpty(d.Dns) ? "-" : d.Dns,
                    string.IsNullOrEmpty(d.Nic) ? "-" : d.Nic,
                    d.MaskShown ? "是" : "",
                    d.Error ?? "",
                });
                if (!fresh) it.ForeColor = Color.Gray;
                else if (state == "待更新") it.ForeColor = Color.FromArgb(200, 110, 0);
                _devices.Items.Add(it);
            }
            _devices.EndUpdate();
            RefreshStatus();
        }

        private void RefreshStatus()
        {
            var s = Program.State;
            int online = (Program.Api?.Snapshot() ?? new DeviceReport[0])
                .Count(x => (DateTime.UtcNow - x.LastSeenUtc).TotalSeconds <= 30);
            _status.Text = "  " + (s.Version == 0 ? "尚未保存过设置（学生机收到 503，什么都不做）"
                                                  : "当前 version=" + s.Version) +
                           "    |    在线学生机 " + online + " 台    |    监听 " + (Program.Api?.BoundPrefix ?? "—") +
                           "    |    密钥指纹 " + ConnKey.Fingerprint(s.Key) +
                           (string.IsNullOrEmpty(s.MaskPasswordHash) ? "    |    ⚠ 还没设解遮罩口令" : "");
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _poll.Stop();
            base.OnFormClosed(e);
        }
    }
}
