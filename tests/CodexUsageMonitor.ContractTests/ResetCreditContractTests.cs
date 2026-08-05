using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using CodexUsageMonitor.Codex;
using CodexUsageMonitor.Codex.Contracts;
using CodexUsageMonitor.Codex.Protocol;
using CodexUsageMonitor.Codex.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexUsageMonitor.ContractTests;

[TestClass]
public sealed class ResetCreditContractTests
{
    [TestMethod]
    [DataRow("reset", ResetCreditConsumeOutcome.Reset, true, false, "reset_credit.reset")]
    [DataRow("alreadyRedeemed", ResetCreditConsumeOutcome.AlreadyRedeemed, true, true, "reset_credit.already_redeemed")]
    [DataRow("nothingToReset", ResetCreditConsumeOutcome.NothingToReset, false, false, "reset_credit.nothing_to_reset")]
    [DataRow("noCredit", ResetCreditConsumeOutcome.NoCredit, false, false, "reset_credit.no_credit")]
    [DataRow("futureOutcome", ResetCreditConsumeOutcome.Unsupported, false, false, "reset_credit.unsupported_outcome")]
    public void MapsDocumentedOutcome(
        string wireValue,
        ResetCreditConsumeOutcome expected,
        bool succeeded,
        bool alreadyRedeemed,
        string code)
    {
        using var document = JsonDocument.Parse($$"""{"outcome":"{{wireValue}}"}""");
        var result = ResetCreditConsumeResult.FromRaw(document.RootElement);

        Assert.AreEqual(expected, result.Outcome);
        Assert.AreEqual(succeeded, result.Succeeded);
        Assert.AreEqual(alreadyRedeemed, result.AlreadyRedeemed);
        Assert.AreEqual(code, result.Code);
    }

    [TestMethod]
    public async Task SendsCreditIdAndIdempotencyKeyUsingDocumentedNames()
    {
        await using var transport = new ScriptedTransport("alreadyRedeemed");
        await using var connection = new JsonRpcConnection(transport, NullLogger<JsonRpcConnection>.Instance);
        await using var client = new AppServerClient(connection, NullLogger<AppServerClient>.Instance);

        await client.InitializeAsync(CancellationToken.None);
        var idempotencyKey = Guid.Parse("6ad896ca-cc9b-489c-919f-f417388d67d4");
        var result = await client.ConsumeResetCreditAsync("credit-123", idempotencyKey, CancellationToken.None);

        Assert.IsTrue(result.AlreadyRedeemed);
        using var request = JsonDocument.Parse(transport.Writes.Single(line => line.Contains("rateLimitResetCredit", StringComparison.Ordinal)));
        var parameters = request.RootElement.GetProperty("params");
        Assert.AreEqual("credit-123", parameters.GetProperty("creditId").GetString());
        Assert.AreEqual(idempotencyKey.ToString("D"), parameters.GetProperty("idempotencyKey").GetString());
        Assert.IsFalse(parameters.TryGetProperty("resetCreditId", out _));
    }

    [TestMethod]
    public async Task OmitsOptionalCreditIdWhenBackendShouldChoose()
    {
        await using var transport = new ScriptedTransport("reset");
        await using var connection = new JsonRpcConnection(transport, NullLogger<JsonRpcConnection>.Instance);
        await using var client = new AppServerClient(connection, NullLogger<AppServerClient>.Instance);

        await client.InitializeAsync(CancellationToken.None);
        await client.ConsumeResetCreditAsync(null, Guid.NewGuid(), CancellationToken.None);

        using var request = JsonDocument.Parse(transport.Writes.Single(line => line.Contains("rateLimitResetCredit", StringComparison.Ordinal)));
        Assert.IsFalse(request.RootElement.GetProperty("params").TryGetProperty("creditId", out _));
    }

    private sealed class ScriptedTransport : IJsonLineTransport
    {
        private readonly string _outcome;
        private readonly Channel<string> _responses = Channel.CreateUnbounded<string>();

        public ScriptedTransport(string outcome) => _outcome = outcome;

        public bool IsConnected => true;

        public List<string> Writes { get; } = [];

        public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var response in _responses.Reader.ReadAllAsync(cancellationToken))
            {
                yield return response;
            }
        }

        public ValueTask WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            Writes.Add(line);
            using var document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("id", out var id))
            {
                return ValueTask.CompletedTask;
            }

            var method = document.RootElement.GetProperty("method").GetString();
            var result = method switch
            {
                "initialize" => "{}",
                "account/rateLimitResetCredit/consume" => $$"""{"outcome":"{{_outcome}}"}""",
                _ => "{}",
            };
            _responses.Writer.TryWrite($$"""{"jsonrpc":"2.0","id":{{id.GetInt64()}},"result":{{result}}}""");
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _responses.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
