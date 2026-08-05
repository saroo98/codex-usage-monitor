namespace CodexUsageMonitor.Core.Diagnostics;

public readonly record struct Result(bool Succeeded, string Code, string? Detail = null)
{
    public static Result Success(string code = "ok") => new(true, code);

    public static Result Failure(string code, string? detail = null) => new(false, code, detail);
}
