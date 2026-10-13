using System.Runtime.Versioning;
using System.ServiceProcess;

namespace SboxServerConsole;

// Lets the Windows service manager run the agent (scripts/install-service.ps1 adds
// --windows-service to the service command line). A plain console program
// registered with sc.exe never reports "running", so Windows gives up on it after
// about 30 seconds with error 1053.
//
// The agent runs on its own thread; a service stop or system shutdown sets the same
// `done` event Ctrl-C sets, and the service waits for the server to stop. If the
// agent ends by itself (the server kept crashing and auto-restart gave up), the
// service stops with the agent's exit code so the service's recovery actions can
// restart it.
[SupportedOSPlatform("windows")]
static class WindowsServiceHost
{
    public static int Run(Func<ManualResetEventSlim, int> agent)
    {
        using var service = new AgentService(agent);
        ServiceBase.Run(service);
        return service.ExitCode;
    }

    sealed class AgentService : ServiceBase
    {
        readonly Func<ManualResetEventSlim, int> _agent;
        readonly ManualResetEventSlim _done = new(false);
        Thread? _thread;
        volatile bool _stopRequested;

        public AgentService(Func<ManualResetEventSlim, int> agent)
        {
            _agent = agent;
            ServiceName = "SboxServerConsole";
            CanStop = true;
            CanShutdown = true;
            AutoLog = false;
        }

        protected override void OnStart(string[] args)
        {
            _thread = new Thread(() =>
            {
                int rc;
                try { rc = _agent(_done); }
                catch (Exception) { rc = 1; }
                ExitCode = rc;
                // Ended by itself, not by a stop request: tell the service manager.
                if (!_stopRequested) Stop();
            })
            { Name = "agent", IsBackground = false };
            _thread.Start();
        }

        protected override void OnStop()
        {
            _stopRequested = true;
            // Stopping the server can take a while (shutdown command, then a tree kill).
            RequestAdditionalTime(30000);
            _done.Set();
            if (_thread is not null && _thread != Thread.CurrentThread) _thread.Join(TimeSpan.FromSeconds(28));
        }

        protected override void OnShutdown() => OnStop();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _done.Dispose();
            base.Dispose(disposing);
        }
    }
}
