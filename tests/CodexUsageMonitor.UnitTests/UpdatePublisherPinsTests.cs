using CodexUsageMonitor.Updater.Install;
using System.Globalization;

namespace CodexUsageMonitor.UnitTests;

[TestClass]
public sealed class UpdatePublisherPinsTests
{
    [TestMethod]
    public void NormalizeRemovesSeparatorsDeduplicatesAndSorts()
    {
        var first = string.Join(' ', Enumerable.Repeat("aa", 20));
        var second = string.Join(':', Enumerable.Repeat("BB", 20));

        var result = UpdatePublisherPins.Normalize([second, first, first]);

        CollectionAssert.AreEqual(
            new[] { new string('A', 40), new string('B', 40) },
            result.ToArray());
    }

    [TestMethod]
    public void NormalizeRejectsEmptyAndOversizedSets()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => UpdatePublisherPins.Normalize([]));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            UpdatePublisherPins.Normalize(Enumerable.Range(0, UpdatePublisherPins.MaximumCount + 1)
                .Select(index => index.ToString("X40", CultureInfo.InvariantCulture))));
    }

    [TestMethod]
    public void ValidateCanonicalRejectsDuplicatesAndLowercase()
    {
        var upper = new string('A', 40);
        Assert.ThrowsExactly<InvalidDataException>(() => UpdatePublisherPins.ValidateCanonical([upper, upper]));
        Assert.ThrowsExactly<InvalidDataException>(() => UpdatePublisherPins.ValidateCanonical([upper.ToLowerInvariant()]));
    }
}
