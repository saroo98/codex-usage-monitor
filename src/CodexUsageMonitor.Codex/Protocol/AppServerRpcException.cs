namespace CodexUsageMonitor.Codex.Protocol;

public sealed class AppServerRpcException : Exception
{
    public AppServerRpcException(string safeCode, int? rpcCode = null)
        : base($"Codex App Server request failed: {safeCode}")
    {
        SafeCode = safeCode;
        RpcCode = rpcCode;
    }

    public string SafeCode { get; }

    public int? RpcCode { get; }
}
