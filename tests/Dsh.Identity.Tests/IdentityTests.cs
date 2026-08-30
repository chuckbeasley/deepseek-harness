namespace Dsh.Identity.Tests;

/// <summary>
/// One test context plus its temp harness home. A fresh home per test keeps the persisted id
/// file isolated and exercises the create-then-read paths without touching a real home.
/// </summary>
public sealed class Fixture : IDisposable
{
    public Context Ctx { get; } = new();

    public string Home { get; }

    private Fixture(string home)
    {
        Home = home;
    }

    public static Fixture WithHome(out string home)
    {
        var dir = Path.Combine(Path.GetTempPath(), "dsh-identity-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        home = dir;
        return new Fixture(dir);
    }

    public void Dispose()
    {
        Ctx.Dispose();
        if (Directory.Exists(Home))
        {
            Directory.Delete(Home, recursive: true);
        }
    }
}

public static class IdentityTests
{
    public static void FirstUse_CreatesAndPersistsId()
    {
        using var fixture = Fixture.WithHome(out var home);
        var provider = AnonymousIdentityProvider.Create(fixture.Ctx, new AnonymousIdentityOptions { Home = home });

        var id = provider.UserId.Value;
        Assert.False(string.IsNullOrEmpty(id), "first use must report a non-empty id");
        var file = Path.Combine(home, AnonymousIdentityProvider.AnonymousUserIdFileName);
        Assert.True(File.Exists(file), "first use must persist the id file under the harness home");
        var persisted = File.ReadAllText(file).Trim();
        Assert.Equal(id, persisted);
        Assert.True(Guid.TryParseExact(persisted, "D", out _), "the persisted id must be a UUID");
        Assert.Equal(provider.AnonymousUserId.Value, id);
    }

    public static void SecondProviderInstance_ReadsTheSameId()
    {
        using var fixture = Fixture.WithHome(out var home);
        var first = AnonymousIdentityProvider.Create(fixture.Ctx, new AnonymousIdentityOptions { Home = home });

        using var secondCtx = new Context();
        var second = AnonymousIdentityProvider.Create(secondCtx, new AnonymousIdentityOptions { Home = home });

        Assert.Equal(first.UserId, second.UserId);
        Assert.Equal(first.AnonymousUserId, second.AnonymousUserId);
        // The second instance read the persisted file; the id is unchanged by the reread.
        Assert.Equal(first.UserId.Value,
            File.ReadAllText(Path.Combine(home, AnonymousIdentityProvider.AnonymousUserIdFileName)).Trim());
    }

    public static void CorruptIdFile_FailsLoud()
    {
        using var fixture = Fixture.WithHome(out var home);
        File.WriteAllText(Path.Combine(home, AnonymousIdentityProvider.AnonymousUserIdFileName), "not-a-uuid\n");

        Assert.Throws<InvalidOperationException>(() =>
            AnonymousIdentityProvider.Create(fixture.Ctx, new AnonymousIdentityOptions { Home = home }),
            "a corrupt id file must fail loud at composition");
    }

    public static void DshHomeEnv_IsRespected()
    {
        using var fixture = Fixture.WithHome(out var home);
        var env = new Dictionary<string, string?> { [HomePaths.DshHomeEnv] = home };

        var provider = AnonymousIdentityProvider.Create(fixture.Ctx, new AnonymousIdentityOptions { Env = env });

        Assert.Equal(home, provider.Home);
        Assert.True(File.Exists(Path.Combine(home, AnonymousIdentityProvider.AnonymousUserIdFileName)),
            "$DSH_HOME must locate the id file");
    }

    public static void DeletedFile_MintsAFreshIdentity()
    {
        using var fixture = Fixture.WithHome(out var home);
        var first = AnonymousIdentityProvider.Create(fixture.Ctx, new AnonymousIdentityOptions { Home = home });
        File.Delete(Path.Combine(home, AnonymousIdentityProvider.AnonymousUserIdFileName));

        using var secondCtx = new Context();
        var second = AnonymousIdentityProvider.Create(secondCtx, new AnonymousIdentityOptions { Home = home });

        Assert.NotEqual(first.UserId, second.UserId, "deleting the file must mint a fresh identity");
    }

    public static void RegistersAsTheIdentityService()
    {
        using var fixture = Fixture.WithHome(out var home);
        var provider = AnonymousIdentityProvider.Create(fixture.Ctx, new AnonymousIdentityOptions { Home = home });

        Assert.Same(provider, fixture.Ctx.Get<IIdentityService>("identity"));
        Assert.Same(provider, fixture.Ctx.Get<AnonymousIdentityProvider>("identity"));
    }
}
