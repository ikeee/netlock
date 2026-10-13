using System.Diagnostics;
using System.Drawing;

namespace NetLock.Shared
{
    /// <summary>
    /// 程序图标：直接取 **exe 自己**的资源（csproj 里用 ApplicationIcon 设的）。
    /// 所以教师端是黑底、学生端是红底，托盘/窗体/遮罩都自动跟着走，不用在两处各写一份。
    /// </summary>
    public static class AppIcon
    {
        public static Icon Get()
        {
            try
            {
                string exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exe))
                {
                    Icon ic = Icon.ExtractAssociatedIcon(exe);
                    if (ic != null) return ic;
                }
            }
            catch { }
            return SystemIcons.Application;
        }
    }
}
