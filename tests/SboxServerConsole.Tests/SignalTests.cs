using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace SboxServerConsole.Tests;

// Regression tests for process-signal shutdown. All three failed before the
// fix that ships alongside them:
//
//  * A background job started by a non-interactive shell inherits SIGINT as
//    SIG_IGN, and the runtime honors that by never hooking an originally
//    ignored signal — so the agent ignored SIGINT entirely in exactly the
//    contexts init scripts and wrappers use: /health kept answering 200
//    forever and shutdown never began. The agent now resets the inherited
//    disposition to default before the runtime captures it.
//  * SIGTERM did begin a shutdown, but through the runtime's ProcessExit path,
//    which hard-exits on a ~2 s budget — the agent died before the child was
//    stopped and the game server was left orphaned to init.
//
// The agent now registers PosixSignalRegistration handlers that cancel the
// default runtime handling and run the same graceful stop for both signals:
// ask the child to shut down, kill the tree if it will not, exit 0.
//
// These tests drive the REAL agent binary out of process (signal dispositions
// cannot be simulated in-proc) against a child that ignores the shutdown
// command AND survives stdin EOF — the strongest shape, because the agent must
// escalate to a tree kill, and a hard-exited agent cannot hide the orphan
// behind the child noticing its pipe closed. Every wait is bounded so a
// regression fails the test instead of wedging the run. Linux/macOS only.
public class SignalTests : IDisposable
{
    readonly Scratch _scratch = new();
    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(5) };

    public void Dispose() => _scratch.Dispose();

    [Fact]
    public void SigintShutsDownGracefullyAndLeavesNoOrphan() => AssertSignalShutsDown("INT");

    [Fact]
    public void SigtermShutsDownGracefullyAndLeavesNoOrphan() => AssertSignalShutsDown("TERM");

    // The field failure: a SIGINT delivered to an agent whose parent context set
    // SIG_IGN (any non-interactive shell background job). Before the fix the
    // signal was ignored forever.
    [Fact]
    public void SigintShutsDownEvenWhenInheritedIgnored() => AssertSignalShutsDown("INT", inheritSigIgn: true);

    void AssertSignalShutsDown(string signal, bool inheritSigIgn = false)
    {
        if (!Configs.CanSpawnShellChild) return;

        string agentBin = Path.Combine(AppContext.BaseDirectory, "SboxServerConsole");
        Assert.True(File.Exists(agentBin), $"agent binary not found at {agentBin}");

        // A child that ignores the shutdown command AND keeps running after its
        // stdin hits EOF, so the graceful stop has to time out, the dispose path
        // has to kill the tree, and an orphan cannot exit by itself.
        var stub = _scratch.File("stubborn.sh");
        File.WriteAllText(stub, "#!/bin/sh\nwhile :; do IFS= read -r line || sleep 1; done\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        int httpPort = Ports.Free();
        var agentArgs = new List<string>
        {
            "--exe", stub,
            "--game-dir", _scratch.Dir,
            "--child-args", "",
            "--child-port", "27015",
            "--listen-port", httpPort.ToString(),
            "--query-poll-sec", "0",
            "--shutdown-command", "quit",
        };

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        if (inheritSigIgn)
        {
            // Reproduce the non-interactive-shell background-job launch: the
            // shell sets SIGINT to SIG_IGN and exec preserves it into the agent.
            psi.FileName = "/bin/sh";
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("trap '' INT; exec \"$@\"");
            psi.ArgumentList.Add("sh");
            psi.ArgumentList.Add(agentBin);
            foreach (var a in agentArgs) psi.ArgumentList.Add(a);
        }
        else
        {
            psi.FileName = agentBin;
            foreach (var a in agentArgs) psi.ArgumentList.Add(a);
        }
        var agent = Process.Start(psi)!;

        try
        {
            // Drain output so the agent can never block on a full pipe.
            agent.OutputDataReceived += (_, _) => { };
            agent.ErrorDataReceived += (_, _) => { };
            agent.BeginOutputReadLine();
            agent.BeginErrorReadLine();

            int childPid = 0;
            Assert.True(Wait.For(() =>
            {
                try
                {
                    var body = Client.GetStringAsync($"http://127.0.0.1:{httpPort}/health").GetAwaiter().GetResult();
                    childPid = JsonDocument.Parse(body).RootElement.GetProperty("child_pid").GetInt32();
                    return childPid > 0;
                }
                catch { return false; }
            }, timeoutMs: 20000), "agent did not come up serving /health");

            var kill = Process.Start(new ProcessStartInfo
            {
                FileName = "kill",
                ArgumentList = { "-s", signal, agent.Id.ToString() },
                UseShellExecute = false,
            })!;
            Assert.True(kill.WaitForExit(5000), "kill(1) did not return");
            Assert.Equal(0, kill.ExitCode);

            // Graceful stop budget: the 10 s shutdown-command wait plus the
            // bounded tree kill. 30 s is generous; a signal-ignoring regression
            // parks here forever, so the bounded wait is what fails it.
            Assert.True(agent.WaitForExit(30000),
                $"agent did not exit within 30s of SIG{signal} — the signal never started a shutdown");
            Assert.Equal(0, agent.ExitCode);

            Assert.True(Wait.For(() => !IsRunning(childPid)),
                $"the child was left orphaned after SIG{signal} shutdown");
        }
        finally
        {
            try { if (!agent.HasExited) agent.Kill(entireProcessTree: true); } catch { }
            agent.Dispose();
        }
    }

    static bool IsRunning(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
