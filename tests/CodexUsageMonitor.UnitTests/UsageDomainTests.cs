using CodexUsageMonitor.Core.Accounts;
using CodexUsageMonitor.Core.Usage;

namespace CodexUsageMonitor.UnitTests;

[TestClass]
public sealed class UsageDomainTests
{
    [TestMethod]
    [DataRow(-1, 0)]
    [DataRow(0, 0)]
    [DataRow(1, 1)]
    [DataRow(99, 99)]
    [DataRow(100, 100)]
    [DataRow(101, 100)]
    public void PercentageMathClampsAllReleaseBoundaries(int input, int expected) =>
        Assert.AreEqual((decimal)expected, UsageMath.ClampPercentage(input));

    [TestMethod]
    [DataRow(0.49, 0)]
    [DataRow(0.5, 1)]
    [DataRow(99.5, 100)]
    [DataRow(100.5, 100)]
    public void RoundedRemainingUsesAwayFromZeroAfterClamping(double input, int expected) =>
        Assert.AreEqual(expected, UsageMath.RoundedRemaining((decimal)input));

    [TestMethod]
    public void NormalizeUsedPercentPrefersUsedAndFallsBackToRemaining()
    {
        Assert.AreEqual(35m, UsageMath.NormalizeUsedPercent(35m, 99m));
        Assert.AreEqual(35m, UsageMath.NormalizeUsedPercent(null, 65m));
        Assert.ThrowsExactly<InvalidDataException>(() => UsageMath.NormalizeUsedPercent(null, null));
    }

    [TestMethod]
    public void DerivedIdentityIsStableAndSensitiveToSemanticInputs()
    {
        var first = LimitIdentityFactory.Create(new LimitIdentityInput(null, LimitKind.Dynamic, " GPT-5 ", 3600, " Team "));
        var normalized = LimitIdentityFactory.Create(new LimitIdentityInput(null, LimitKind.Dynamic, "gpt-5", 3600, "team"));
        var changed = LimitIdentityFactory.Create(new LimitIdentityInput(null, LimitKind.Dynamic, "gpt-5", 7200, "team"));

        Assert.AreEqual(first, normalized);
        Assert.AreNotEqual(first, changed);
        StringAssert.StartsWith(first, "derived:");
        Assert.AreEqual(40, first.Length);
    }

    [TestMethod]
    public void ServerIdentityIsNormalizedWithoutExposingOtherMetadata()
    {
        var identity = LimitIdentityFactory.Create(new LimitIdentityInput("  LIMIT-A  ", LimitKind.Weekly, "private-model", 60, "private"));

        Assert.AreEqual("server:limit-a", identity);
    }

    [TestMethod]
    [DataRow(LimitKind.FiveHour, null, null, "5 hour")]
    [DataRow(LimitKind.Weekly, null, null, "Weekly")]
    [DataRow(LimitKind.ModelSpecific, "gpt-5", null, "gpt-5")]
    [DataRow(LimitKind.Dynamic, null, 60L, "1 minute")]
    [DataRow(LimitKind.Dynamic, null, 3600L, "1 hour")]
    [DataRow(LimitKind.Dynamic, null, 86400L, "1 day")]
    public void LabelResolverUsesStableDomainLabels(LimitKind kind, string? model, long? windowSeconds, string expected) =>
        Assert.AreEqual(expected, LimitLabelResolver.Resolve(kind, model, windowSeconds, null));

    [TestMethod]
    public void LabelResolverTrimsAndBoundsServerLabel()
    {
        var value = "  " + new string('x', 120) + "  ";
        var label = LimitLabelResolver.Resolve(LimitKind.Unknown, null, null, value);

        Assert.AreEqual(96, label.Length);
        Assert.IsTrue(label.All(static character => character == 'x'));
    }

    [TestMethod]
    public void AutoSelectionChoosesLowestRemainingWithStablePriorityTieBreak()
    {
        var reset = new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero);
        var weekly = Limit("weekly", LimitKind.Weekly, 80m, reset);
        var fiveHour = Limit("five", LimitKind.FiveHour, 80m, reset);
        var ignored = new UsageLimit("ignored", LimitKind.Dynamic, "Ignored", 99m, reset, isAuthoritative: false);

        var result = LimitSelectionEngine.Select(
            [weekly, ignored, fiveHour],
            new LimitSelectionRequest(LimitSelectionMode.AutoLowest));

        Assert.AreSame(fiveHour, result.Primary);
        Assert.IsNull(result.Secondary);
        Assert.AreEqual("selection.auto", result.Code);
    }

    [TestMethod]
    public void MissingExplicitSelectionFallsBackAndDualMeterNeverDuplicatesPrimary()
    {
        var weekly = Limit("weekly", LimitKind.Weekly, 20m, null);
        var fiveHour = Limit("five", LimitKind.FiveHour, 70m, null);
        var credits = Limit("credits", LimitKind.Credits, 50m, null);

        var result = LimitSelectionEngine.Select(
            [weekly, fiveHour, credits],
            new LimitSelectionRequest(
                LimitSelectionMode.Explicit,
                ExplicitIdentity: "missing",
                DualMeter: true));

        Assert.AreSame(fiveHour, result.Primary);
        Assert.AreSame(credits, result.Secondary);
        Assert.AreNotEqual(result.Primary!.Identity, result.Secondary!.Identity);
        Assert.AreEqual("selection.selected", result.Code);
    }

    [TestMethod]
    public void NoAuthoritativeLimitsProducesExplicitNoDataResult()
    {
        var result = LimitSelectionEngine.Select(
            [new UsageLimit("x", LimitKind.Unknown, "X", 10m, null, isAuthoritative: false)],
            new LimitSelectionRequest(LimitSelectionMode.AutoLowest, DualMeter: true));

        Assert.IsFalse(result.HasData);
        Assert.IsNull(result.Primary);
        Assert.IsNull(result.Secondary);
        Assert.AreEqual("selection.no_limits", result.Code);
    }

    [TestMethod]
    public void SnapshotNormalizesTimeWorkspaceAndSequenceWithoutMutatingLimits()
    {
        var profileId = Guid.NewGuid();
        var account = new AccountIdentity("account", "person@example.com", null, null);
        var limit = Limit("weekly", LimitKind.Weekly, 25m, null);
        var observed = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.FromHours(2));

        var snapshot = new UsageSnapshot(profileId, account, observed, [limit], workspace: "  workspace  ", sequence: -1);

        Assert.AreEqual(observed.ToUniversalTime(), snapshot.ObservedAtUtc);
        Assert.AreEqual("workspace", snapshot.Workspace);
        Assert.AreEqual(0, snapshot.Sequence);
        Assert.AreSame(limit, snapshot.Find("weekly"));
        Assert.IsNull(snapshot.Find("WEEKLY"));
    }

    private static UsageLimit Limit(string id, LimitKind kind, decimal used, DateTimeOffset? reset) =>
        new(id, kind, id, used, reset);
}
