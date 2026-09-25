using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace Bizigo.Capacity;

/// <summary>
/// The supervisor holds a session/job until cleanup. Ownership does not depend
/// on the lifetime of the actual command's parent PID or its redirected pipes.
/// </summary>
internal sealed class CapacityChild : IAsyncDisposable
{
    private readonly Process supervisor;
    private readonly NamedPipeServerStream control;
    private readonly StreamReader reader;
    private readonly Task<string> stdout;
    private readonly Task<string> stderr;
    private readonly TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim cleanupLock = new(1, 1);
    private CapacityProcessOwnership? ownership;
    private Task? monitor;
    private bool stopped;

    private CapacityChild(Process supervisor, NamedPipeServerStream control)
    {
        this.supervisor = supervisor;
        this.control = control;
        reader = new StreamReader(control, leaveOpen: true);
        stdout = supervisor.StandardOutput.ReadToEndAsync();
        stderr = supervisor.StandardError.ReadToEndAsync();
    }

    public int Id { get; private set; }
    public bool HasExited => completion.Task.IsCompleted;
    public int ExitCode => completion.Task.GetAwaiter().GetResult();
    public Task WaitAsync(CancellationToken token) => completion.Task.WaitAsync(token);

    public static async Task<CapacityChild> StartAsync(CapacityCommand command, string? input, CancellationToken token)
    {
        // Keep below macOS's 104-byte socket-path limit with its per-user TMPDIR.
        var pipeName = Guid.NewGuid().ToString("N");
        var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var host = Environment.ProcessPath;
        if (!string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase))
            host = "dotnet";
        var start = new ProcessStartInfo(host!)
        {
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true,
        };
        start.ArgumentList.Add(typeof(CapacityChild).Assembly.Location);
        start.ArgumentList.Add("--supervise");
        start.ArgumentList.Add(pipeName);
        CapacityChild? child = null;
        try
        {
            var process = Process.Start(start) ?? throw new IOException("Supervisor failed to start.");
            child = new(process, pipe);
            // A supervisor that cannot connect must not hang a generator startup.
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
            startup.CancelAfter(TimeSpan.FromSeconds(2));
            await pipe.WaitForConnectionAsync(startup.Token);
            if (await child.reader.ReadLineAsync(startup.Token) != "ready")
                throw new IOException("Supervisor did not establish ownership.");
            // No payload is launched until session/job ownership is established.
            child.ownership = new CapacityProcessOwnership(process);
            using (var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true })
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(new CapacitySupervisorRequest(command, input)).AsMemory(), startup.Token);
            }
            var started = JsonSerializer.Deserialize<CapacitySupervisorStatus>(
                await child.reader.ReadLineAsync(startup.Token) ?? throw new IOException("Supervisor closed before launch."))!;
            if (started.ProcessId is null) throw new IOException(started.Error ?? "Command failed to start.");
            child.Id = started.ProcessId.Value;
            child.monitor = child.MonitorAsync();
            return child;
        }
        catch
        {
            if (child is not null) await child.DisposeAsync();
            else pipe.Dispose();
            throw;
        }
    }

    private async Task MonitorAsync()
    {
        try
        {
            var status = JsonSerializer.Deserialize<CapacitySupervisorStatus>(
                await reader.ReadLineAsync() ?? throw new IOException("Supervisor exited without command status."))!;
            if (status.ExitCode is null) throw new IOException(status.Error ?? "Missing command exit status.");
            // Even exit 0 can leave descendants. Kill the owned session/job
            // before announcing completion or waiting for inherited stdout EOF.
            await StopAsync();
            completion.TrySetResult(status.ExitCode.Value);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            try { await StopAsync(); }
            catch (Exception cleanup) when (cleanup is not OutOfMemoryException)
            { ex = new AggregateException(ex, cleanup); }
            completion.TrySetException(ex);
        }
    }

    private async Task StopAsync()
    {
        await cleanupLock.WaitAsync();
        try
        {
            if (stopped) return;
            if (ownership is not null) ownership.Terminate();
            else if (!supervisor.HasExited) supervisor.Kill(entireProcessTree: true);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await supervisor.WaitForExitAsync(deadline.Token);
            stopped = true;
        }
        finally { cleanupLock.Release(); }
    }

    public async Task<string> OutputAsync(CancellationToken token)
    {
        await WaitAsync(token);
        var output = await stdout.WaitAsync(token);
        var error = await stderr.WaitAsync(token);
        if (ExitCode != 0) throw new IOException($"Child exited {ExitCode}: {error[..Math.Min(error.Length, 1000)]}");
        return output;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync();
            control.Dispose();
            if (monitor is not null) await monitor.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            ownership?.Dispose();
            reader.Dispose();
            control.Dispose();
            supervisor.Dispose();
            cleanupLock.Dispose();
        }
    }
}
