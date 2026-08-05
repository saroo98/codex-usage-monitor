using System.Diagnostics;

namespace CodexUsageMonitor.Updater.Install;

public interface IUpdaterHostStarter
{
    void Start(string hostPath, string requestOption, string requestPath, string nonce);
}

public sealed class ProcessUpdaterHostStarter : IUpdaterHostStarter
{
    public void Start(string hostPath, string requestOption, string requestPath, string nonce)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestOption);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);

        var start = new ProcessStartInfo
        {
            FileName = hostPath,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(hostPath)
                ?? throw new InvalidDataException("The updater host has no working directory."),
        };
        start.ArgumentList.Add(requestOption);
        start.ArgumentList.Add(requestPath);
        start.ArgumentList.Add("--nonce");
        start.ArgumentList.Add(nonce);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The updater host could not be started.");
    }
}
