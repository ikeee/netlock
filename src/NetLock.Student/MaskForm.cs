using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using NetLock.Shared;

namespace NetLock.Student
{
    /// <summary>
    /// 全屏遮罩（**拔网线时显示**）：盖住所有显示器、始终置顶、阻止 Alt+F4/Esc 关掉；
    /// 老师输入口令即可解除（本地校验口令散列 → 写一条带 HMAC 的"已解除"标记 → 本次断网不再弹）。
    ///
    /// 精减取舍（诚实说明）：**不锁键鼠**（老项目的 BlockInput 需要更高权限且容易被杀）。
    /// 遮罩靠"全屏 + 每 500ms 置顶"占住屏幕；学生按 Win 键仍能切走 —— 若需要彻底锁死，
    /// 要么用完整版 LabGuard，要么给这台机开"学生账号不能切桌面"的系统策略。
    /// </summary>
    internal sealed class MaskForm : Form
    {
        private readonly byte[] _key;
        private readonly string _passwordHash;
        private readonly TextBox _pwd = new TextBox { UseSystemPasswordChar = true, Width = 220 };
        private readonly Label _hint = new Label { AutoSize = true };
        private readonly Label _elapsed = new Label { AutoSize = true };
        private readonly DateTime _since = DateTime.Now;
        private readonly System.Windows.Forms.Timer _tick = new System.Windows.Forms.Timer { Interval = 500 };

        public MaskForm(byte[] key, string passwordHash)
        {
            _key = key;
            _passwordHash = passwordHash ?? "";
            FormBorderStyle = FormBorderStyle.None;
            Icon = AppIcon.Get();
            StartPosition = FormStartPosition.Manual;
            Bounds = SystemInformation.VirtualScreen;      // 一个窗体盖住所有显示器
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(12, 14, 18);
            ForeColor = Color.White;
            KeyPreview = true;
            Font = new Font("微软雅黑", 12F);

            var title = new Label
            {
                Text = "网络已断开", AutoSize = true, ForeColor = Color.White,
                Font = new Font("微软雅黑", 48F, FontStyle.Bold), BackColor = Color.Transparent,
            };
            var sub = new Label
            {
                Text = "请把网线插回（或让网卡恢复）\n网络恢复后本提示会自动消失",
                AutoSize = true, ForeColor = Color.FromArgb(200, 205, 215),
                Font = new Font("微软雅黑", 18F), BackColor = Color.Transparent,
            };
            var who = new Label
            {
                Text = "机位：" + Environment.MachineName,
                AutoSize = true, ForeColor = Color.FromArgb(140, 145, 155),
                Font = new Font("微软雅黑", 12F), BackColor = Color.Transparent,
            };
            _elapsed.Text = "已断开 0 分 0 秒";
            _elapsed.ForeColor = Color.FromArgb(140, 145, 155);
            _elapsed.Font = new Font("微软雅黑", 12F);
            _elapsed.BackColor = Color.Transparent;

            var teacherPanel = new Panel
            {
                Width = 470, Height = 96, BackColor = Color.FromArgb(24, 27, 33),
            };
            var tTitle = new Label
            {
                Text = "老师解除", AutoSize = true, Left = 18, Top = 10,
                ForeColor = Color.FromArgb(210, 215, 225), Font = new Font("微软雅黑", 10.5F, FontStyle.Bold),
            };
            var tHint = new Label
            {
                Text = "输入口令后点「解除」", AutoSize = true, Left = 250, Top = 12,
                ForeColor = Color.FromArgb(140, 145, 155), Font = new Font("微软雅黑", 9F),
            };
            _pwd.SetBounds(116, 8, 220, 26);
            var ok = new Button
            {
                Text = "解除", Left = 348, Top = 6, Width = 100, Height = 30,
                FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 120, 215), ForeColor = Color.White,
            };
            ok.Click += (s, e) => TryUnlock();
            _hint.SetBounds(18, 48, 430, 22);
            _hint.ForeColor = Color.FromArgb(150, 155, 165);
            _hint.Font = new Font("微软雅黑", 9F);
            teacherPanel.Controls.AddRange(new Control[] { tTitle, _pwd, ok, tHint, _hint });

            var stack = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
                WrapContents = false, AutoScroll = false,
                Padding = new Padding(60, 80, 60, 40), BackColor = Color.Transparent,
            };
            stack.Controls.Add(title);
            stack.Controls.Add(sub);
            stack.Controls.Add(who);
            stack.Controls.Add(_elapsed);
            stack.Controls.Add(teacherPanel);
            Controls.Add(stack);

            _tick.Tick += (s, e) =>
            {
                ReassertTopMost();
                TimeSpan span = DateTime.Now - _since;
                _elapsed.Text = "已断开 " + (int)span.TotalMinutes + " 分 " + span.Seconds + " 秒";
            };
            _tick.Start();
            ThreadPool.QueueUserWorkItem(_ => WatchExitSignal());
        }

        /// <summary>每 500ms 把窗口重新顶到最前（不锁键鼠的情况下，这是"占住屏幕"最实在的做法）。</summary>
        private void ReassertTopMost()
        {
            try
            {
                TopMost = false;
                TopMost = true;
                if (!Focused && !_pwd.Focused) Activate();
            }
            catch { }
        }

        /// <summary>服务通过命名事件通知"网络恢复了/老师已解除" → 关闭。</summary>
        private void WatchExitSignal()
        {
            try
            {
                using (var exit = EventWaitHandle.OpenExisting(StudentEngine.MaskExitEventName))
                {
                    exit.WaitOne();
                }
                BeginInvoke(new Action(() => { _allowClose = true; Close(); }));
            }
            catch { /* 打不开事件（例如手工启动遮罩预览）→ 只能靠口令解除 */ }
        }

        private bool _allowClose;

        private void TryUnlock()
        {
            string pwd = _pwd.Text ?? "";
            if (string.IsNullOrEmpty(_passwordHash))
            {
                _hint.Text = "本机还没有收到老师设置的口令 —— 请让老师用 cleanup 脚本或安全模式解除。";
                _hint.ForeColor = Color.FromArgb(240, 180, 120);
                return;
            }
            if (!PasswordHash.Verify(pwd, _passwordHash))
            {
                _hint.Text = "口令不正确。";
                _hint.ForeColor = Color.FromArgb(240, 120, 120);
                _pwd.Text = "";
                _pwd.Focus();
                return;
            }
            try { StudentEngine.WriteAck(_key); } catch { }
            _allowClose = true;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 挡住 Alt+F4 / Esc / 系统菜单关闭：只允许我们自己关
            if (!_allowClose) e.Cancel = true;
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _tick.Stop();
            base.OnFormClosed(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) return true;      // Esc 不放行
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
