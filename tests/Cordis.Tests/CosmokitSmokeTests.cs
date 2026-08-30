using Cordis.Cosmokit;
using Timeout = Cordis.Cosmokit.Timeout;

namespace Cordis.Tests;

/// <summary>Sanity checks for the minimal Cosmokit utility port.</summary>
public static class CosmokitSmokeTests
{
    public static void ParseTimeParsesCompactDurations()
    {
        Assert.Equal(Time.Week + Time.Day, Time.ParseTime("1w1d"));
        Assert.Equal(Time.Hour + Time.Minute + Time.Second, Time.ParseTime("1h1m1s"));
        Assert.Equal(Time.Hour, Time.ParseTime("1hour"));
        Assert.Equal(0, Time.ParseTime("nonsense"));
    }

    public static void FormatRendersCompactDurations()
    {
        Assert.Equal("1d", Time.Format(Time.Day));
        Assert.Equal("500ms", Time.Format(500));
        Assert.Equal("2h", Time.Format(Time.Hour * 2));
    }

    public static void ClampTimeoutAppliesDefaultAndCap()
    {
        Assert.Equal(30_000, Timeout.ClampTimeout(null, 30_000, 120_000));
        Assert.Equal(120_000, Timeout.ClampTimeout(999_999, 30_000, 120_000));
        Assert.Equal(500, Timeout.ClampTimeout(500, 30_000, 120_000));
        Assert.Throws<ArgumentException>(() => Timeout.ClampTimeout(0, 30_000, 120_000));
    }

    public static void DeadlineFiresIdentifiableTimeoutReason()
    {
        using var deadline = Deadline.Create(null, 10, "TEST_TIMEOUT");
        Assert.True(deadline.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)));
        Assert.True(deadline.Token.IsCancellationRequested);
        var reason = Timeout.TimeoutOf(deadline, "TEST_TIMEOUT");
        Assert.NotNull(reason);
        Assert.Equal("TEST_TIMEOUT", reason!.Code);
        Assert.Equal(10, reason.TimeoutMs);
        Assert.Null(Timeout.TimeoutOf(deadline, "OTHER_CODE"));
    }

    public static void ItemRetainerKeepsHeadAndCountsOmissions()
    {
        var retainer = new ItemRetainer<string>(new ItemRetentionStrategy(2));
        Assert.True(retainer.Push("a").Kept);
        Assert.True(retainer.Push("b").Kept);
        var decision = retainer.Push("c");
        Assert.False(decision.Kept);
        Assert.True(decision.Truncated);

        var result = retainer.Finish();
        Assert.Equal(new[] { "a", "b" }, result.Items);
        Assert.Equal(3, result.Seen);
        Assert.Equal(2, result.Kept);
        Assert.Equal(new Omitted.Exact(1), result.Omitted);
    }

    public static void TextRetainerHeadTailPreservesUtf8Boundaries()
    {
        var retainer = new TextRetainer(new TextRetentionStrategy.HeadTail(2, 2));
        retainer.Push("héllo"); // h + é(2 bytes) + llo = 6 bytes
        var result = retainer.Finish();
        Assert.Equal("hlo", result.Text); // the partial é at the prefix cut is trimmed
        Assert.True(result.Truncated);
        Assert.Equal(new Omitted.Exact(3), result.OmittedBytes);
    }

    public static void ResolveDshHomeHonorsEnvironmentOverride()
    {
        var env = new Dictionary<string, string?> { [HomePaths.DshHomeEnv] = "~/custom-dsh" };
        var home = HomePaths.ResolveDshHome(null, env);
        Assert.EndsWith("custom-dsh", home);
        Assert.Equal("$DSH_HOME", HomePaths.DshHomeDisplay(home));
    }

    public static void BrandHelpersArraysAndStringsWork()
    {
        Assert.Equal(new List<object?> { "a" }, Arrays.MakeArray("a"));
        Assert.Equal(new List<object?>(), Arrays.MakeArray(null));
        Assert.Equal("fooBar", Strings.CamelCase("foo-bar"));
        Assert.Equal("foo-bar", Strings.ParamCase("fooBar"));
        Assert.Equal("/a-b", Strings.Sanitize("a-b"));
    }
}


