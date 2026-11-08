using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace VarPlats.Notices;

public static class NoticeGeneratorProgram
{
    public static int Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("Usage: ThirdPartyNoticeGenerator <publish-directory> <nuget-package-root> <linux-rid>");
            return 2;
        }

        try
        {
            ThirdPartyNoticeGenerator.Generate(args[0], args[1], args[2]);
            return 0;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or JsonException or ArgumentException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}

public static partial class ThirdPartyNoticeGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly IReadOnlyDictionary<string, (string Url, string Commit)> SourceOverrides
        = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["NetTopologySuite.IO.PostGis/2.1.0"] =
                ("https://github.com/NetTopologySuite/NetTopologySuite.IO.PostGis",
                    "c604084e2e39213aaceffdcc729d26ce7160019b")
        };

    public static ThirdPartyNoticeManifest Generate(
        string publishDirectory, string packageRoot, string linuxRid)
    {
        if (!Regex.IsMatch(linuxRid, "^linux-(x64|arm64)$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Only linux-x64 and linux-arm64 inventories are supported.", nameof(linuxRid));

        var root = Path.GetFullPath(publishDirectory);
        var packages = Path.GetFullPath(packageRoot);
        var depsFile = Path.Combine(root, "VarPlats.Web.deps.json");
        if (!File.Exists(depsFile))
            throw new FileNotFoundException("Published dependency manifest was not found.", depsFile);

        using var deps = JsonDocument.Parse(File.ReadAllBytes(depsFile));
        var runtimeTarget = deps.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString()
            ?? throw new InvalidDataException("Published dependency manifest has no runtime target.");
        var target = deps.RootElement.GetProperty("targets").GetProperty(runtimeTarget);
        var libraries = deps.RootElement.GetProperty("libraries");
        var packageEntries = new List<ThirdPartyPackage>();

        foreach (var library in libraries.EnumerateObject()
                     .Where(item => item.Value.GetProperty("type").GetString() == "package")
                     .OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            var separator = library.Name.LastIndexOf('/');
            if (separator <= 0 || separator == library.Name.Length - 1)
                throw new InvalidDataException($"Invalid package identity in dependency manifest: {library.Name}");

            var id = library.Name[..separator];
            var version = library.Name[(separator + 1)..];
            if (!IsUsedByTarget(target, library.Name, linuxRid))
                continue;

            packageEntries.Add(ReadPackage(root, packages, id, version));
        }

        if (packageEntries.Count == 0)
            throw new InvalidDataException("No NuGet packages with published runtime assets were found.");

        var manifest = new ThirdPartyNoticeManifest(1, linuxRid, packageEntries);
        var noticesDirectory = Path.Combine(root, "third-party-notices");
        Directory.CreateDirectory(noticesDirectory);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        File.WriteAllBytes(Path.Combine(noticesDirectory, "nuget-packages.json"), manifestBytes);
        File.WriteAllText(Path.Combine(noticesDirectory, "nuget-packages.md"),
            RenderMarkdown(manifest), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return manifest;
    }

    private static bool IsUsedByTarget(JsonElement target, string package, string rid)
    {
        if (!target.TryGetProperty(package, out var item))
            throw new InvalidDataException($"Package is listed but missing from the publish target: {package}");

        if (HasAssets(item, "runtime") || HasAssets(item, "native") || HasAssets(item, "resources"))
            return true;

        if (!item.TryGetProperty("runtimeTargets", out var runtimeTargets))
            return false;

        var arch = rid["linux-".Length..];
        foreach (var runtimeTarget in runtimeTargets.EnumerateObject())
        {
            if (!runtimeTarget.Value.TryGetProperty("rid", out var assetRid))
                continue;

            var value = assetRid.GetString();
            if (value is "linux" or "unix" or "any" || value == rid || value == $"unix-{arch}")
                return true;
        }
        return false;
    }

    private static bool HasAssets(JsonElement item, string property)
        => item.TryGetProperty(property, out var assets) && assets.EnumerateObject().Any();

    private static ThirdPartyPackage ReadPackage(
        string publishDirectory, string packageRoot, string id, string version)
    {
        var safeId = SafeSegment(id);
        var safeVersion = SafeSegment(version);
        var identity = $"{id}/{version}";
        var directory = Path.Combine(packageRoot, safeId.ToLowerInvariant(), safeVersion.ToLowerInvariant());
        var nuspec = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.nuspec", SearchOption.TopDirectoryOnly).SingleOrDefault()
            : null;
        if (nuspec is null)
            throw new InvalidDataException($"NuGet package metadata is missing for {identity}.");

        var document = XDocument.Load(nuspec);
        var metadata = document.Descendants().SingleOrDefault(element => element.Name.LocalName == "metadata")
            ?? throw new InvalidDataException($"NuGet package metadata is invalid for {identity}.");
        var licenseElement = Child(metadata, "license");
        var license = licenseElement?.Value.Trim() ?? "";
        var licenseType = (string?)licenseElement?.Attribute("type") ?? "";
        var copyright = Value(metadata, "copyright");
        var repository = Child(metadata, "repository");
        var repositoryUrl = (string?)repository?.Attribute("url") ?? "";
        var commit = (string?)repository?.Attribute("commit") ?? "";

        if (SourceOverrides.TryGetValue(identity, out var sourceOverride))
        {
            repositoryUrl = sourceOverride.Url;
            commit = sourceOverride.Commit;
        }

        if (string.IsNullOrWhiteSpace(license) || string.IsNullOrWhiteSpace(copyright)
            || string.IsNullOrWhiteSpace(repositoryUrl)
            || !Regex.IsMatch(commit, "^[a-fA-F0-9]{40,64}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException(
                $"NuGet metadata needs a license, copyright, and immutable source commit: {identity}.");

        var packageNoticesPath = Path.Combine("third-party-notices", "nuget", safeId, safeVersion);
        var copiedFiles = new List<ThirdPartyNoticeFile>();
        File.Copy(nuspec, Path.Combine(EnsureDirectory(publishDirectory, packageNoticesPath), Path.GetFileName(nuspec)));
        copiedFiles.Add(DescribeFile(Path.Combine(packageNoticesPath, Path.GetFileName(nuspec)),
            Path.Combine(EnsureDirectory(publishDirectory, packageNoticesPath), Path.GetFileName(nuspec))));

        foreach (var sourcePath in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                     .Where(path => IsNoticeFile(Path.GetFileName(path)))
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var relativePath = Path.GetRelativePath(directory, sourcePath);
            var destinationRelativePath = Path.Combine(packageNoticesPath, relativePath);
            var destination = Path.Combine(publishDirectory, destinationRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(sourcePath, destination, overwrite: true);
            copiedFiles.Add(DescribeFile(destinationRelativePath, destination));
        }

        var licenseUrl = Value(metadata, "licenseUrl");
        if (licenseUrl.Length == 0)
            licenseUrl = licenseType == "expression"
                ? $"https://licenses.nuget.org/{Uri.EscapeDataString(license)}"
                : "";
        var hashPath = Directory.EnumerateFiles(directory, "*.nupkg.sha512", SearchOption.TopDirectoryOnly)
            .SingleOrDefault();
        var packageHash = hashPath is not null ? File.ReadAllText(hashPath).Trim() : "";

        return new ThirdPartyPackage(id, version, license, licenseType, copyright, licenseUrl,
            repositoryUrl, commit, packageHash, copiedFiles);
    }

    private static ThirdPartyNoticeFile DescribeFile(string relativePath, string path)
    {
        using var stream = File.OpenRead(path);
        return new(relativePath.Replace(Path.DirectorySeparatorChar, '/'),
            Convert.ToHexStringLower(SHA256.HashData(stream)));
    }

    private static string EnsureDirectory(string root, string relative)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string RenderMarkdown(ThirdPartyNoticeManifest manifest)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Published NuGet package inventory");
        builder.AppendLine();
        builder.AppendLine($"Target: `{manifest.TargetRid}`. Metadata and files below come from the published dependency graph and restored package archives.");
        builder.AppendLine("This inventory is not a legal review or a corresponding-source distribution.");
        builder.AppendLine();
        builder.AppendLine("| Package | License metadata | Source repository and commit | Included package notices |");
        builder.AppendLine("| --- | --- | --- | --- |");
        foreach (var package in manifest.Packages)
        {
            var notices = string.Join("<br>", package.NoticeFiles.Select(file => $"`{file.Path}`"));
            builder.Append("| `").Append(package.PackageId).Append(" ").Append(package.Version).Append("` | `")
                .Append(package.License.Replace("|", "\\|", StringComparison.Ordinal)).Append("` | `")
                .Append(package.SourceRepositoryUrl).Append("` @ `").Append(package.SourceCommit).Append("` | ")
                .Append(notices).AppendLine(" |");
        }
        return builder.ToString();
    }

    private static XElement? Child(XElement element, string name)
        => element.Elements().SingleOrDefault(child => child.Name.LocalName == name);

    private static string Value(XElement element, string name)
        => Child(element, name)?.Value.Trim() ?? "";

    private static string SafeSegment(string value)
    {
        if (value is "" or "." or ".."
            || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
            throw new InvalidDataException("Package identity contains an unsafe path segment.");
        return value;
    }

    private static bool IsNoticeFile(string fileName)
        => Regex.IsMatch(fileName,
            "^(?:LICENSE|LICENCE|COPYING|NOTICE|COPYRIGHT|THIRD-PARTY-NOTICES)(?:[._-].*)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}

public sealed record ThirdPartyNoticeManifest(
    int SchemaVersion,
    string TargetRid,
    IReadOnlyList<ThirdPartyPackage> Packages);

public sealed record ThirdPartyPackage(
    string PackageId,
    string Version,
    string License,
    string LicenseType,
    string Copyright,
    string LicenseUrl,
    string SourceRepositoryUrl,
    string SourceCommit,
    string NuGetPackageHash,
    IReadOnlyList<ThirdPartyNoticeFile> NoticeFiles);

public sealed record ThirdPartyNoticeFile(string Path, string Sha256);
