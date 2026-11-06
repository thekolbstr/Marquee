using System.Diagnostics;
using System.Text;

namespace MediaServer.Api.Processes;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

public interface IProcessRunner
{
    /// <summary>Runs a tool with an explicit argument list (never a shell). Kills the process tree on cancellation.</summary>
    Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> args, CancellationToken ct);
}

/// <summary>Bounded-concurrency subprocess runner with guaranteed teardown on cancellation.</summary>
public sealed class ProcessRunner(int maxConcurrency = 4) : IProcessRunner
{
    private readonly SemaphoreSlim _gate = new(maxConcurrency, maxConcurrency);

    public async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> args, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var psi = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {executable}");
            proc.StandardInput.Close();
            try
            {
                var outTask = proc.StandardOutput.ReadToEndAsync(ct);
                var errTask = proc.StandardError.ReadToEndAsync(ct);
                await proc.WaitForExitAsync(ct);
                return new ProcessResult(proc.ExitCode, await outTask, await errTask);
            }
            catch (OperationCanceledException)
            {
                Kill(proc);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    internal static void Kill(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }
}
