using AwesomeAssertions;
using Harness.Web;
using Xunit;

namespace Harness.Integration.Web;

/// <summary>
/// The Avalonia UI's WebAssembly bundle stays within its size budget (TASK-0163): what a browser downloads the first
/// time, each file's Brotli copy when the publish wrote one (scripts/ui_bundle.py counts the same way). The budget is
/// 6.0 MB, 25 % over the 4.81 MB the TASK-0144 spike measured: growing past it is a decision, made by raising
/// <see cref="BudgetBytes"/> here and BUDGET_MB in the script. Skipped without a published bundle.
/// </summary>
public sealed class UiBundleBudgetTests
{
    public const long BudgetBytes = 6_000_000;

    [Fact]
    public void TheBundleStaysWithinItsSizeBudget()
    {
        var root = Bundle();
        var files = Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".br", StringComparison.Ordinal) && !f.EndsWith(".gz", StringComparison.Ordinal))
            .ToArray();
        var brotli = files.Sum(f => File.Exists(f + ".br") ? new FileInfo(f + ".br").Length : new FileInfo(f).Length);
        TestContext.Current.SendDiagnosticMessage(
            $"Avalonia UI bundle: {files.Length} files, {brotli / 1e6:0.00} MB Brotli (budget {BudgetBytes / 1e6:0.00} MB)"
        );

        brotli.Should().BeLessThanOrEqualTo(BudgetBytes, $"the bundle is {brotli / 1e6:0.00} MB with Brotli");
        files
            .Select(Path.GetFileName)
            .Count(name => name!.StartsWith("dotnet.native.", StringComparison.Ordinal) && name.EndsWith(".wasm", StringComparison.Ordinal))
            .Should()
            .Be(1, "a publish into a used folder leaves the old fingerprinted files beside the new ones");
    }

    private static string Bundle()
    {
        var folder = Environment.GetEnvironmentVariable(UiNextAssets.FolderVariable) is { Length: > 0 } set
            ? set
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/next/wwwroot"));
        return File.Exists(Path.Combine(folder, "index.html"))
            ? folder
            : throw Xunit.Sdk.SkipException.ForSkip(
                $"No Avalonia UI bundle at {folder}: publish it with 'dotnet publish src/Harness.Ui.Browser -c Release "
                    + $"-o artifacts/next' into a new folder (it needs the wasm-tools workload) or set {UiNextAssets.FolderVariable}."
            );
    }
}
