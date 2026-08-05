using System.Diagnostics;

namespace CodexUsageMonitor.Codex.Transport;

public sealed class CodexExecutableResolver
{
    private static readonly string[] CandidateNames = ["codex.exe", "codex.cmd", "codex"];

    public ResolvedCodexCommand? Resolve(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var candidate = Path.GetFullPath(Environment.ExpandEnvironmentVariables(explicitPath.Trim()));
            if (File.Exists(candidate))
            {
                return FromPath(candidate, "explicit");
            }
        }

        foreach (var directory in CandidateDirectories())
        {
            foreach (var name in CandidateNames)
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return FromPath(candidate, "path");
                }
            }
        }

        return null;
    }

    public async Task<string?> ProbeVersionAsync(
        ResolvedCodexCommand command,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        using var process = new Process
        {
            StartInfo = CreateStartInfo(command, ["--version"]),
            EnableRaisingEvents = true,
        };
        if (!process.Start())
        {
            return null;
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(linked.Token);
        var errorTask = process.StandardError.ReadToEndAsync(linked.Token);
        await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        var output = (await outputTask.ConfigureAwait(false)).Trim();
        _ = await errorTask.ConfigureAwait(false);
        return process.ExitCode == 0 && output.Length is > 0 and <= 256 ? output : null;
    }

    internal static ProcessStartInfo CreateStartInfo(
        ResolvedCodexCommand command,
        IReadOnlyList<string> arguments)
    {
        var isCommandScript = command.ExecutablePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
        var info = new ProcessStartInfo
        {
            FileName = isCommandScript
                ? Path.Combine(Environment.SystemDirectory, "cmd.exe")
                : command.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        if (isCommandScript)
        {
            info.ArgumentList.Add("/d");
            info.ArgumentList.Add("/s");
            info.ArgumentList.Add("/c");
            info.ArgumentList.Add(command.ExecutablePath);
        }

        foreach (var prefix in command.PrefixArguments)
        {
            info.ArgumentList.Add(prefix);
        }

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }

    private static ResolvedCodexCommand FromPath(string path, string source) =>
        new(Path.GetFullPath(path), Array.Empty<string>(), source);

    private static IEnumerable<string> CandidateDirectories()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (var item in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Directory.Exists(item))
                {
                    yield return item;
                }
            }
        }

        var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var candidate in new[]
        {
            Path.Combine(applicationData, "npm"),
            Path.Combine(localApplicationData, "Programs", "codex"),
            Path.Combine(profile, ".local", "bin"),
        })
        {
            if (Directory.Exists(candidate))
            {
                yield return candidate;
            }
        }
    }
}
