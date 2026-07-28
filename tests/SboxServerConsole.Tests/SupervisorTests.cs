using System.Diagnostics;
using Xunit;

namespace SboxServerConsole.Tests;

// Regression tests for child-process teardown. Both of these failed before the
// fixes that ship alongside them:
//
//  * Disposing a host whose child was still running blocked forever, because the
//    redirected stdout stream was disposed while the output pump was parked in a
//    blocking Read. Shutting the agent down with the server still up wedged it.
//  * The POSIX process group held a Process object the caller had already
//    disposed, so its tree-kill threw on the first member access and was
//    swallowed — the cleanup never ran at all.
//
// Linux/macOS only; the Windows path uses ConPTY and a job object.
public class SupervisorTests : IDisposable
{
    readonly Scratch _scratch = new();

    public void Dispose() => _scratch.Dispose();

    Process StartSleeper()
    {
        var p = Process.Start(new ProcessStartInfo
        {
            FileName = Configs.ShellChild,
            Arguments = "-c \"sleep 120\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
        })!;
        Assert.False(p.HasExited);
        return p;
    }

    [Fact]
    public void ProcessGroupDisposeKillsTheChild()
    {
        if (!Configs.CanSpawnShellChild) return;

        using var proc = StartSleeper();
        var group = ProcessGroup.CreateForCurrentPlatform();
        Assert.NotNull(group);

        // Mirror what ServerProcess does: hand over a short-lived Process object
        // that goes out of scope immediately after the assignment.
        using (var handle = Process.GetProcessById(proc.Id)) group!.AssignProcess(handle);

        group!.Dispose();
        Assert.True(Wait.For(() => proc.HasExited), "process-group dispose should have killed the child");
    }

    [Fact]
    public void DisposingAHostWithALiveChildDoesNotBlock()
    {
        if (!Configs.CanSpawnShellChild) return;

        var stub = _scratch.File("stub.sh");
        File.WriteAllText(stub, "#!/bin/sh\nwhile IFS= read -r line; do :; done\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var cfg = Configs.Parse(_scratch.Dir, "--exe", stub, "--child-args", "", "--child-port", "27015");
        var buffer = new MessageBuffer(cfg.BufferSize);
        var server = new ServerProcess(cfg, buffer);
        server.Start();
        Assert.True(Wait.For(() => server.IsAlive), "child did not start");
        int pid = server.ChildPid;

        // The child is deliberately still running. Dispose on a worker thread so a
        // regression fails the test instead of wedging the whole run.
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try { server.Dispose(); }
            catch (Exception ex) { failure = ex; }
        })
        { IsBackground = true, Name = "dispose-under-test" };
        worker.Start();

        Assert.True(worker.Join(TimeSpan.FromSeconds(30)),
            "ServerProcess.Dispose() blocked with a live child");
        Assert.Null(failure);
        Assert.True(Wait.For(() => !IsRunning(pid)), "the child process was left running");
        buffer.Dispose();
    }

    [Fact]
    public void RestartingReplacesAStubbornChild()
    {
        if (!Configs.CanSpawnShellChild) return;

        // This stub ignores the shutdown command, so Restart() has to fall back to
        // disposing the host — the same path that used to deadlock.
        var stub = _scratch.File("stubborn.sh");
        File.WriteAllText(stub, "#!/bin/sh\nwhile IFS= read -r line; do echo \"ignored: $line\"; done\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var cfg = Configs.Parse(_scratch.Dir,
            "--exe", stub, "--child-args", "", "--child-port", "27015",
            "--shutdown-command", "quit", "--restart-backoff-sec", "1");
        using var buffer = new MessageBuffer(cfg.BufferSize);
        using var server = new ServerProcess(cfg, buffer);
        server.Start();
        Assert.True(Wait.For(() => server.IsAlive));
        int firstPid = server.ChildPid;

        var worker = new Thread(() => server.Restart(TimeSpan.FromSeconds(2))) { IsBackground = true };
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(30)), "Restart() blocked on a child that ignores the shutdown command");
        Assert.True(Wait.For(() => server.IsAlive && server.ChildPid != firstPid, timeoutMs: 30000),
            "the supervisor should have started a replacement child");
        Assert.True(Wait.For(() => !IsRunning(firstPid)), "the old child was left running");
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
    }
}
