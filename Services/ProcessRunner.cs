using System.Diagnostics;

namespace HyperVManagerTray.Services;

/// <summary>Outcome of running an external process.</summary>
/// <param name="Success">True when the process exited with code 0.</param>
/// <param name="ExitCode">The process exit code, or -1 if it failed to start or timed out.</param>
/// <param name="StdOut">Trimmed standard output.</param>
/// <param name="StdErr">Trimmed standard error (or a diagnostic message on start failure/timeout).</param>
internal sealed record ProcessResult(bool Success, int ExitCode, string StdOut, string StdErr)
{
    /// <summary>The most useful single message: stdout on success, otherwise stderr (falling back to stdout).</summary>
    public string Output => Success ? StdOut : (StdErr.Length > 0 ? StdErr : StdOut);
}

/// <summary>
/// Runs a console process to completion, capturing both output streams without buffering
/// deadlocks and killing the process tree if it exceeds a timeout.  All work is off the UI
/// thread (<c>ConfigureAwait(false)</c>), so the synchronous <see cref="Run"/> wrapper can be
/// called from the UI thread without a sync-over-async deadlock.
/// </summary>
internal static class ProcessRunner
{
    /// <summary>Runs <paramref name="fileName"/> with a raw argument string (already escaped/encoded).</summary>
    public static Task<ProcessResult> RunAsync(string fileName, string arguments, TimeSpan timeout)
        => RunAsync(NewStartInfo(fileName, psi => psi.Arguments = arguments), timeout);

    /// <summary>Runs <paramref name="fileName"/> with discrete arguments (quoted automatically by the runtime).</summary>
    public static Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> args, TimeSpan timeout)
        => RunAsync(NewStartInfo(fileName, psi => { foreach (var a in args) psi.ArgumentList.Add(a); }), timeout);

    /// <summary>Synchronous convenience wrapper around <see cref="RunAsync(string, IEnumerable{string}, TimeSpan)"/>.</summary>
    public static ProcessResult Run(string fileName, params string[] args)
        => RunAsync(fileName, args, TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();

    private static ProcessStartInfo NewStartInfo(string fileName, Action<ProcessStartInfo> configure)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = fileName,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        configure(psi);
        return psi;
    }

    private static async Task<ProcessResult> RunAsync(ProcessStartInfo psi, TimeSpan timeout)
    {
        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            return new ProcessResult(false, -1, "", ex.Message);
        }

        // Read both streams concurrently to avoid a full-pipe deadlock.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return new ProcessResult(false, -1, "", $"Timed out after {timeout.TotalSeconds:0} s");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new ProcessResult(proc.ExitCode == 0, proc.ExitCode, stdout.Trim(), stderr.Trim());
    }
}
