using System.ServiceProcess;

namespace NetLock.Student
{
    /// <summary>Windows 服务外壳（SYSTEM 运行）：只负责起停引擎。</summary>
    public sealed class StudentService : ServiceBase
    {
        private readonly StudentConfig _cfg;
        private StudentEngine _engine;

        public StudentEngine Engine { get { return _engine; } }

        public StudentService(StudentConfig cfg)
        {
            _cfg = cfg;
            ServiceName = "NetLockSvc";
            CanStop = true;
            CanShutdown = true;
            AutoLog = false;
        }

        protected override void OnStart(string[] args)
        {
            Log.Write("服务启动");
            if (!Program.ShouldRunInThisEnvironment(out string why))
            {
                Log.Write("不加载管控：" + why);
                return;
            }
            _engine = new StudentEngine(_cfg, Log.Write);
            _engine.Start();
        }

        protected override void OnStop()
        {
            Log.Write("服务停止");
            _engine?.Stop();
            _engine = null;
        }

        protected override void OnShutdown() { OnStop(); }
    }
}
