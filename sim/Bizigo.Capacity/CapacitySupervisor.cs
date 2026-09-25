using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace Bizigo.Capacity;

internal sealed record CapacitySupervisorRequest(CapacityCommand Command, string? Input);
internal sealed record CapacitySupervisorStatus(int? ProcessId = null, int? ExitCode = null, string? Error = null);

internal static class CapacitySupervisor
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "--supervise") return 64;
        using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(2000);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        CapacityProcessOwnership.EstablishSession();
        await writer.WriteLineAsync("ready");
        var request = JsonSerializer.Deserialize<CapacitySupervisorRequest>(
            await reader.ReadLineAsync() ?? throw new IOException("Parent disconnected before launch."))!;
        Process? payload = null;
        try
        {
            var start = new ProcessStartInfo(request.Command.FileName)
            { UseShellExecute = false, RedirectStandardInput = true };
            foreach (var argument in request.Command.Arguments) start.ArgumentList.Add(argument);
            payload = Process.Start(start) ?? throw new IOException("Command failed to start.");
            await writer.WriteLineAsync(JsonSerializer.Serialize(new CapacitySupervisorStatus(ProcessId: payload.Id)));
            if (request.Input is not null) await payload.StandardInput.WriteAsync(request.Input);
            payload.StandardInput.Close();
            var disconnected = reader.ReadLineAsync();
            await Task.WhenAny(payload.WaitForExitAsync(), disconnected);
            if (payload.HasExited)
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(new CapacitySupervisorStatus(ExitCode: payload.ExitCode)));
                // Remain session leader until the owner kills the complete group.
                // This also prevents PID reuse between exit notification and cleanup.
                await disconnected;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            try
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(new CapacitySupervisorStatus(Error: ex.Message)));
                await reader.ReadLineAsync();
            }
            catch (IOException) { }
        }
        finally
        {
            // Parent death closes the control pipe. On Unix this path kills the
            // whole session, including this supervisor. Windows uses kill-on-close.
            CapacityProcessOwnership.TerminateOwnSession();
            payload?.Dispose();
        }
        return 0;
    }
}
