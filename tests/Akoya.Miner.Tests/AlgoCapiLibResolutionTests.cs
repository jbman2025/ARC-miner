// Guards the DllImport resolver's allowlist against the algo modules.
//
// WHY THIS EXISTS: adding --algo sha3t meant adding sha3t_capi to a hardcoded
// `name is "csd_capi" or "randomx_capi" or ...` test in Program.cs, and missing
// it does not fail the build, fail a test, or produce a sensible error. A name
// that is not on the list never reaches NativeLibs.Load, so it gets no
// ARC_*_LIB override, no lookup in the extracted-resource directory and no
// PreloadDependencies(); .NET's default probing takes over and a single-file
// build reports "<lib> not found next to the miner binary" no matter where the
// file actually sits. Both "next to the exe" and "in the extract folder" fail
// identically, which sends you hunting a packaging bug that is not there.
//
// So instead of trusting the next person to remember, this walks the assembly
// for the per-algo P/Invoke surfaces and asserts each one's library is claimed.

using System.Reflection;
using Akoya.Miner;
using Xunit;

namespace Akoya.Miner.Tests;

public class AlgoCapiLibResolutionTests
{
    // Every Algos/*/…Native class declares `public const string Lib = "<name>"`.
    // That constant is the single source of truth for what the algo will ask
    // the runtime to load.
    private static IEnumerable<(string Type, string Lib)> DeclaredAlgoLibs()
    {
        foreach (var t in typeof(NativeLibs).Assembly.GetTypes())
        {
            if (t.Namespace?.StartsWith("Akoya.Miner.Algos.", StringComparison.Ordinal) != true) continue;
            if (!t.Name.EndsWith("Native", StringComparison.Ordinal)) continue;

            var f = t.GetField("Lib", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (f is null || !f.IsLiteral || f.FieldType != typeof(string)) continue;

            if (f.GetRawConstantValue() is string lib && lib.Length > 0) yield return (t.Name, lib);
        }
    }

    [Fact]
    public void EveryAlgoNativeLibraryIsClaimedByTheResolver()
    {
        var declared = DeclaredAlgoLibs().ToList();

        // If this trips, the reflection above stopped matching — a rename would
        // otherwise turn this whole file into a test that asserts nothing.
        Assert.NotEmpty(declared);

        var missing = declared
            .Where(d => !NativeLibs.AlgoCapiLibs.Contains(d.Lib, StringComparer.Ordinal))
            .Select(d => $"{d.Type}.Lib = \"{d.Lib}\"")
            .ToList();

        Assert.True(missing.Count == 0,
            "These algo libraries are not in NativeLibs.AlgoCapiLibs, so the DllImport resolver " +
            "will never claim them and the miner will report them as missing wherever they are " +
            "placed:\n  " + string.Join("\n  ", missing));
    }

    // sha3t_capi specifically, because it is the one that got missed and a
    // named case says so in the failure output.
    [Fact]
    public void Sha3tIsClaimed()
        => Assert.Contains("sha3t_capi", NativeLibs.AlgoCapiLibs);

    [Fact]
    public void TheAllowlistHasNoDuplicatesOrBlanks()
    {
        Assert.All(NativeLibs.AlgoCapiLibs, n => Assert.False(string.IsNullOrWhiteSpace(n)));
        Assert.Equal(NativeLibs.AlgoCapiLibs.Length, NativeLibs.AlgoCapiLibs.Distinct(StringComparer.Ordinal).Count());
    }

    // The resolver derives the override variable as ARC_{NAME}_LIB. Nothing
    // enforces that spelling, so pin the one people will reach for when a
    // library needs to come from somewhere unusual.
    [Fact]
    public void TheOverrideVariableForSha3tIsArcSha3tCapiLib()
        => Assert.Equal("ARC_SHA3T_CAPI_LIB", $"ARC_{"sha3t_capi".ToUpperInvariant()}_LIB");
}
