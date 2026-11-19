using System.ComponentModel;
using System.Diagnostics;

namespace ITBees.Asterisk.Host;

public sealed class SystemCommandResult
{
    public SystemCommandResult(int exitCode, string output, string error, bool started = true)
    {
        ExitCode = exitCode;
        Output = output;
        Error = error;
        Started = started;
    }

    public int ExitCode { get; }

    public string Output { get; }

    public string Error { get; }

    /// <summary>False when the program does not exist on this host.</summary>
    public bool Started { get; }

    public bool Success => Started && ExitCode == 0;

    /// <summary>Error text for a message: stderr, else stdout, else the exit code.</summary>
    public string Describe()
    {
        if (Started == false)
            return "program not found";
        var text = string.IsNullOrWhiteSpace(Error) ? Output : Error;
        text = text.Trim();
        if (text.Length > 600)
            text = "..." + text[^600..];
        return text.Length > 0 ? text : $"exit code {ExitCode}";
    }
}

/// <summary>Runs a program on the host (seam for tests and for running the commands somewhere else).</summary>
public interface ISystemCommandRunner
{
    Task<SystemCommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? standardInput, TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed class ProcessCommandRunner : ISystemCommandRunner
{
    public async Task<SystemCommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? standardInput, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput != null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // An argument list, never a command line: values are not parsed by a shell.
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception e)
        {
            return new SystemCommandResult(-1, string.Empty, e.Message, started: false);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(cts.Token);
            var error = process.StandardError.ReadToEndAsync(cts.Token);
            if (standardInput != null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), cts.Token);
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync(cts.Token);
            return new SystemCommandResult(process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already gone.
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new SystemCommandResult(-1, string.Empty, $"timed out after {timeout.TotalSeconds:0} s");
        }
    }
}
