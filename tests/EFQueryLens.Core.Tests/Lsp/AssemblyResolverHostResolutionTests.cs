using EFQueryLens.Lsp.Parsing;

namespace EFQueryLens.Core.Tests.Lsp;

public class AssemblyResolverHostResolutionTests
{
    [Fact]
    public void TryGetProjectDirectory_FindsNearestCsprojDirectory()
    {
        using var fs = new TempFs();
        var projectDir = fs.CreateDirectory("src/App");
        fs.WriteFile("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var sourceFile = fs.WriteFile("src/App/Sub/Feature/MyFile.cs", "class C {} ");

        var result = AssemblyResolver.TryGetProjectDirectory(sourceFile);

        Assert.Equal(projectDir, result);
    }

    [Fact]
    public void TryGetProjectReferenceDirs_ParsesDirectReferences()
    {
        using var fs = new TempFs();
        fs.CreateDirectory("src/App");
        fs.CreateDirectory("src/Lib1");
        fs.CreateDirectory("src/Lib2");

        fs.WriteFile("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="../Lib1/Lib1.csproj" />
                <ProjectReference Include="../Lib2/Lib2.csproj" />
              </ItemGroup>
            </Project>
            """);

        fs.WriteFile("src/Lib1/Lib1.csproj", "<Project />");
        fs.WriteFile("src/Lib2/Lib2.csproj", "<Project />");

        var refs = AssemblyResolver.TryGetProjectReferenceDirs(Path.Combine(fs.Root, "src", "App"));

        Assert.Contains(Path.Combine(fs.Root, "src", "Lib1"), refs, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine(fs.Root, "src", "Lib2"), refs, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryGetTargetAssembly_ForExecutableProject_UsesOwnOutputDll()
    {
        using var fs = new TempFs();
        fs.CreateDirectory("App/bin/Debug/net10.0");
        fs.WriteFile("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup><AssemblyName>App.Custom</AssemblyName></PropertyGroup>
            </Project>
            """);

        var source = fs.WriteFile("App/Program.cs", "class Program {} ");
        var dll = fs.WriteFile("App/bin/Debug/net10.0/App.Custom.dll", "");

        var resolved = AssemblyResolver.TryGetTargetAssembly(source);

        Assert.Equal(dll, resolved);
    }

    [Fact]
    public void TryGetTargetAssembly_ForLibraryProject_ResolvesHostExecutableFromSolution()
    {
        using var fs = new TempFs();
        fs.CreateDirectory("Lib");
        fs.CreateDirectory("Host/bin/Debug/net10.0");

        fs.WriteFile("Lib/Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><AssemblyName>Lib</AssemblyName></PropertyGroup>
            </Project>
            """);
        var source = fs.WriteFile("Lib/Repo.cs", "class Repo {} ");

        fs.WriteFile("Host/Host.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>Exe</OutputType><AssemblyName>Host</AssemblyName></PropertyGroup>
              <ItemGroup><ProjectReference Include="../Lib/Lib.csproj" /></ItemGroup>
            </Project>
            """);

        var hostDll = fs.WriteFile("Host/bin/Debug/net10.0/Host.dll", "");
        fs.WriteFile("Host/bin/Debug/net10.0/Lib.dll", "");

        fs.WriteFile("Repo.sln", """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAKE}") = "Lib", "Lib\Lib.csproj", "{1}"
            EndProject
            Project("{FAKE}") = "Host", "Host\Host.csproj", "{2}"
            EndProject
            Global
            EndGlobal
            """);

        var resolved = AssemblyResolver.TryGetTargetAssembly(source);

        Assert.Equal(hostDll, resolved);
    }

    [Fact]
    public void TryExtractDbContextTypeNamesFromFactories_ReadsGenericTypeNames()
    {
        using var fs = new TempFs();
        fs.CreateDirectory("Host/bin/Debug/net10.0");
        var dll = fs.WriteFile("Host/bin/Debug/net10.0/Host.dll", "");
        fs.WriteFile("Host/Host.csproj", "<Project />");
        fs.WriteFile("Host/MyFactory.cs", "public class F : IQueryLensDbContextFactory<My.App.MyDbContext> {} ");
        fs.WriteFile("Host/AnotherFactory.cs", "public class F2 : IQueryLensDbContextFactory<Other.DbCtx> {} ");

        var types = AssemblyResolver.TryExtractDbContextTypeNamesFromFactories(dll);

        Assert.Equal(2, types.Count);
        Assert.Contains("My.App.MyDbContext", types, StringComparer.Ordinal);
        Assert.Contains("Other.DbCtx", types, StringComparer.Ordinal);
    }

    [Fact]
    public void TryGetAssemblyFingerprint_ReturnsNull_WhenNoProjectFound()
    {
        using var fs = new TempFs();
        var source = fs.WriteFile("NoProject/File.cs", "class X {} ");

        var fingerprint = AssemblyResolver.TryGetAssemblyFingerprint(source);

        Assert.Null(fingerprint);
    }

    private sealed class TempFs : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"ql-lsp-test-{Guid.NewGuid():N}");

        public TempFs() => Directory.CreateDirectory(Root);

        public string CreateDirectory(string relative)
        {
            var full = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(full);
            return full;
        }

        public string WriteFile(string relative, string content)
        {
            var full = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            return full;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
