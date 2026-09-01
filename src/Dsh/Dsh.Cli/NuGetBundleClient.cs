using System.IO.Compression;
using System.Text.Json;

namespace Harness.Cli;

/// <summary>
/// Minimal NuGet flat-container client for <c>dsh plugin add --source</c>: resolves the latest
/// version of a package from a flat-container base (a local directory in the hierarchical feed
/// layout or an HTTP <c>v3-flatcontainer</c> root), downloads the nupkg, and extracts a bundle
/// (a <c>profile.json</c> + <c>cordis.patch.yml</c> pair at the package root) into the profile's
/// <c>bundles/&lt;packageId&gt;/</c> directory. The bundle format is the honest .NET equivalent
/// of the TS npm-bundle install; a package without the bundle pair is refused.
/// </summary>
public static class NuGetBundleClient
{
    /// <summary>Default feed base (the public NuGet flat-container root).</summary>
    public const string DefaultSource = "https://api.nuget.org/v3-flatcontainer";

    /// <summary>
    /// Install one bundle package: resolve, download, extract, and return the installed bundle
    /// directory (already created and populated).
    /// </summary>
    public static async Task<string> InstallAsync(string packageId, string source, string profileDir, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var id = packageId.ToLowerInvariant();
        var version = await ResolveLatestVersionAsync(id, source, ct).ConfigureAwait(false);
        var nupkg = await DownloadAsync(id, version, source, ct).ConfigureAwait(false);
        var bundleDir = Path.Combine(profileDir, "bundles", packageId);
        try
        {
            ExtractBundle(nupkg, bundleDir);
        }
        finally
        {
            nupkg.Dispose();
        }
        return bundleDir;
    }

    /// <summary>Resolve the newest version of a package from a flat-container base.</summary>
    private static async Task<string> ResolveLatestVersionAsync(string id, string source, CancellationToken ct)
    {
        if (IsLocalDirectory(source))
        {
            var dir = Path.Combine(source, id);
            if (!Directory.Exists(dir)) throw new InvalidOperationException($"no package \"{id}\" in feed \"{source}\"");
            var versions = Directory.EnumerateDirectories(dir)
                .Select(Path.GetFileName)!
                .Where(name => name is not null && File.Exists(Path.Combine(dir, name!, $"{id}.{name}.nupkg")))
                .Select(name => name!)
                .OrderBy(name => name, Comparer<string>.Create(VersionComparer))
                .ToArray();
            if (versions.Length == 0) throw new InvalidOperationException($"no package \"{id}\" in feed \"{source}\"");
            return versions[^1];
        }
        using var index = await FetchAsync($"{source.TrimEnd('/')}/{id}/index.json", ct).ConfigureAwait(false);
        using var document = JsonDocument.Parse(index);
        var versionList = document.RootElement.TryGetProperty("versions", out var list)
            ? list.EnumerateArray().Select(entry => entry.GetString()).Where(entry => entry is not null).Select(entry => entry!).ToArray()
            : Array.Empty<string>();
        if (versionList.Length == 0) throw new InvalidOperationException($"no package \"{id}\" in feed \"{source}\"");
        Array.Sort(versionList, VersionComparer);
        return versionList[^1];
    }

    /// <summary>Download the nupkg bytes from a flat-container base.</summary>
    private static async Task<MemoryStream> DownloadAsync(string id, string version, string source, CancellationToken ct)
    {
        var relative = $"{id}/{version}/{id}.{version}.nupkg";
        if (IsLocalDirectory(source))
        {
            var path = Path.Combine(source, relative);
            if (!File.Exists(path)) throw new InvalidOperationException($"package \"{id}\" {version} is missing from feed \"{source}\"");
            var local = new MemoryStream();
            await using (var file = File.OpenRead(path)) await file.CopyToAsync(local, ct).ConfigureAwait(false);
            local.Position = 0;
            return local;
        }
        return await FetchAsync($"{source.TrimEnd('/')}/{relative}", ct).ConfigureAwait(false);
    }

    /// <summary>Extract the bundle pair from the nupkg into the target directory (created fresh).</summary>
    private static void ExtractBundle(Stream nupkg, string bundleDir)
    {
        using var zip = new ZipArchive(nupkg, ZipArchiveMode.Read, leaveOpen: true);
        var manifest = FindEntry(zip, "profile.json");
        var patch = FindEntry(zip, "cordis.patch.yml");
        if (manifest is null || patch is null)
        {
            throw new InvalidOperationException(
                "the package is not a Harness bundle: it must carry profile.json and cordis.patch.yml at its root");
        }
        Directory.CreateDirectory(bundleDir);
        using (var source = manifest.Open())
        using (var target = File.Create(Path.Combine(bundleDir, "profile.json")))
        {
            source.CopyTo(target);
        }
        using (var source = patch.Open())
        using (var target = File.Create(Path.Combine(bundleDir, "cordis.patch.yml")))
        {
            source.CopyTo(target);
        }
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string name)
        => zip.Entries.FirstOrDefault(entry => entry.FullName == name || entry.FullName.EndsWith("/" + name, StringComparison.Ordinal));

    private static bool IsLocalDirectory(string source)
        => !source.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !source.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static async Task<MemoryStream> FetchAsync(string url, CancellationToken ct)
    {
        using var client = new HttpClient();
        using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"feed request failed: {response.StatusCode} for {url}");
        }
        var stream = new MemoryStream();
        await response.Content.CopyToAsync(stream).ConfigureAwait(false);
        stream.Position = 0;
        return stream;
    }

    /// <summary>Numeric version ordering (the flat-container versions are plain semver strings).</summary>
    private static int VersionComparer(string left, string right)
    {
        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        var count = Math.Max(leftParts.Length, rightParts.Length);
        for (var index = 0; index < count; index++)
        {
            var l = index < leftParts.Length && int.TryParse(leftParts[index], out var parsedL) ? parsedL : 0;
            var r = index < rightParts.Length && int.TryParse(rightParts[index], out var parsedR) ? parsedR : 0;
            if (l != r) return l.CompareTo(r);
        }
        return StringComparer.Ordinal.Compare(left, right);
    }
}