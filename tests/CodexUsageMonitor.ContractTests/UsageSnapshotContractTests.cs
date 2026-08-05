using System.Text.Json;
using CodexUsageMonitor.Codex.Mapping;
using CodexUsageMonitor.Core.Diagnostics;

namespace CodexUsageMonitor.ContractTests;

[TestClass]
public sealed class UsageSnapshotContractTests
{
    [TestMethod]
    public void SparseForwardCompatibleResponseMapsKnownUsageFields()
    {
        using var account = JsonDocument.Parse("""{"account":{"id":"synthetic-account"}}""");
        using var limits = JsonDocument.Parse(
            """{"rateLimits":{"primary":{"remainingPercent":"72.5","windowDurationMins":300},"futureMetadata":{"anything":true}}}""");

        var mapped = new CodexSnapshotMapper(NullProtocolAnomalySink.Instance).Map(account.RootElement, limits.RootElement);

        Assert.AreEqual(1, mapped.Limits.Count);
        Assert.AreEqual(72.5m, mapped.Limits[0].RemainingPercent);
    }

    [TestMethod]
    public void MalformedTopLevelResponseIsRejectedWithoutInventingUsage()
    {
        using var account = JsonDocument.Parse("""{"account":{"id":"synthetic-account"}}""");
        using var malformed = JsonDocument.Parse("[]");

        Assert.Throws<InvalidDataException>(() =>
            new CodexSnapshotMapper(NullProtocolAnomalySink.Instance).Map(account.RootElement, malformed.RootElement));
    }

    [TestMethod]
    public void MissingStableAccountIdentityIsRejected()
    {
        using var account = JsonDocument.Parse("""{"account":{"name":"No stable identity"}}""");
        using var limits = JsonDocument.Parse("""{"rateLimits":{}}""");

        Assert.Throws<InvalidDataException>(() =>
            new CodexSnapshotMapper(NullProtocolAnomalySink.Instance).Map(account.RootElement, limits.RootElement));
    }
}
