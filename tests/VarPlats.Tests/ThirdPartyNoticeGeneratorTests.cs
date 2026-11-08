using System.Text.Json;
using VarPlats.Notices;
using Xunit;

namespace VarPlats.Tests;

public sealed class ThirdPartyNoticeGeneratorTests
{
    [Fact]
    public void RuntimePackageInventoryCopiesNoticesAndPinsSources()
    {
        var root = Path.Combine(Path.GetTempPath(), $"synthetic-notice-{Guid.NewGuid():N}");
        var publish = Path.Combine(root, "publish");
        var packages = Path.Combine(root, "packages");
        try
        {
            Directory.CreateDirectory(publish);
            AddPackage(packages, "Synthetic", "1.2.3",
                """<license type="expression">MIT</license>""",
                """<repository type="git" url="https://github.com/example/synthetic" commit="0123456789012345678901234567890123456789" />""",
                ["LICENSE.txt", "nested/THIRD-PARTY-NOTICES.txt"]);
            File.WriteAllText(Path.Combine(publish, "VarPlats.Web.deps.json"), """
                {
                  "runtimeTarget": { "name": ".NETCoreApp,Version=v11.0" },
                  "targets": {
                    ".NETCoreApp,Version=v11.0": {
                      "Synthetic/1.2.3": {
                        "runtime": { "lib/net11.0/Synthetic.dll": {} },
                        "runtimeTargets": {
                          "runtimes/linux-arm64/native/synthetic.so": {
                            "rid": "linux-arm64", "assetType": "native"
                          },
                          "runtimes/win-x64/native/synthetic.dll": {
                            "rid": "win-x64", "assetType": "native"
                          }
                        }
                      },
                      "WindowsOnly/1.0.0": {
                        "runtimeTargets": {
                          "runtimes/win-x64/native/windows.dll": {
                            "rid": "win-x64", "assetType": "native"
                          }
                        }
                      }
                    }
                  },
                  "libraries": {
                    "Synthetic/1.2.3": { "type": "package" },
                    "WindowsOnly/1.0.0": { "type": "package" }
                  }
                }
                """);

            var inventory = ThirdPartyNoticeGenerator.Generate(publish, packages, "linux-arm64");

            var package = Assert.Single(inventory.Packages);
            Assert.Equal("Synthetic", package.PackageId);
            Assert.Equal("0123456789012345678901234567890123456789", package.SourceCommit);
            Assert.Equal(3, package.NoticeFiles.Count);
            Assert.True(File.Exists(Path.Combine(publish, "third-party-notices", "nuget-packages.json")));
            Assert.True(File.Exists(Path.Combine(publish, "third-party-notices", "nuget-packages.md")));
            Assert.Equal("Synthetic license notice",
                File.ReadAllText(Path.Combine(publish, "third-party-notices", "nuget", "Synthetic", "1.2.3", "LICENSE.txt")));
            Assert.Contains("not a legal review", File.ReadAllText(
                Path.Combine(publish, "third-party-notices", "nuget-packages.md")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RuntimePackageWithoutPinnedSourceCommitIsRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), $"synthetic-notice-{Guid.NewGuid():N}");
        var publish = Path.Combine(root, "publish");
        var packages = Path.Combine(root, "packages");
        try
        {
            Directory.CreateDirectory(publish);
            AddPackage(packages, "Unpinned", "1.0.0",
                """<license type="expression">MIT</license>""",
                """<repository type="git" url="https://github.com/example/unpinned" />""",
                ["LICENSE.txt"]);
            File.WriteAllText(Path.Combine(publish, "VarPlats.Web.deps.json"), """
                {
                  "runtimeTarget": { "name": ".NETCoreApp,Version=v11.0" },
                  "targets": {
                    ".NETCoreApp,Version=v11.0": {
                      "Unpinned/1.0.0": { "runtime": { "lib/Unpinned.dll": {} } }
                    }
                  },
                  "libraries": {
                    "Unpinned/1.0.0": { "type": "package" }
                  }
                }
                """);

            var exception = Assert.Throws<InvalidDataException>(
                () => ThirdPartyNoticeGenerator.Generate(publish, packages, "linux-x64"));

            Assert.Contains("immutable source commit", exception.Message);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PackageIdentityCannotEscapeNoticeDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"synthetic-notice-{Guid.NewGuid():N}");
        var publish = Path.Combine(root, "publish");
        var packages = Path.Combine(root, "packages");
        try
        {
            Directory.CreateDirectory(publish);
            File.WriteAllText(Path.Combine(publish, "VarPlats.Web.deps.json"), """
                {
                  "runtimeTarget": { "name": ".NETCoreApp,Version=v11.0" },
                  "targets": {
                    ".NETCoreApp,Version=v11.0": {
                      "../1.0.0": { "runtime": { "lib/Unsafe.dll": {} } }
                    }
                  },
                  "libraries": {
                    "../1.0.0": { "type": "package" }
                  }
                }
                """);

            var exception = Assert.Throws<InvalidDataException>(
                () => ThirdPartyNoticeGenerator.Generate(publish, packages, "linux-x64"));

            Assert.Contains("unsafe path segment", exception.Message);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void AddPackage(
        string packages, string id, string version, string license, string repository,
        IReadOnlyList<string> noticeFiles)
    {
        var directory = Path.Combine(packages, id.ToLowerInvariant(), version);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"{id}.nuspec"), $"""
            <package><metadata>
              <id>{id}</id><version>{version}</version>
              {license}
              <copyright>Copyright Example</copyright>
              {repository}
            </metadata></package>
            """);
        foreach (var file in noticeFiles)
        {
            var path = Path.Combine(directory, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "Synthetic license notice");
        }
    }
}
