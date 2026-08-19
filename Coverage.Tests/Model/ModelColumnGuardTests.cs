using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Coverage.Tests.Model;

/// <summary>
/// #13 U5: the generic Spark grids show exactly the attributes whose
/// <c>showedOn</c> contains <c>Query</c>, ordered by <c>order</c> — and
/// <c>--spark-synchronize-model</c> re-derives <c>showedOn</c> from index
/// projection membership on every run for <c>[FromIndex]</c> entities,
/// silently wiping the curated per-surface trims (that is exactly how
/// <c>45354c0</c> regressed the account and commit pages; CI's
/// <c>--spark-verify-model</c> deliberately does not hash <c>showedOn</c>).
/// This test is the loud failure that wipe otherwise lacks: if synchronize
/// reverts the model JSON, re-apply the curated <c>showedOn</c> values below
/// (see docs/adoption-findings.md M6) — or, once Spark preserves hand-edits,
/// delete this guard.
/// </summary>
public class ModelColumnGuardTests
{
    private static JsonElement LoadPersistentObject(string entity)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Coverage.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        dir.Should().NotBeNull("the test must run somewhere under the repository root");
        var path = Path.Combine(dir!, "Coverage", "App_Data", "Model", $"{entity}.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("persistentObject").Clone();
    }

    private static string[] VisibleQueryColumns(JsonElement persistentObject)
        => [.. persistentObject.GetProperty("attributes").EnumerateArray()
            .Where(a => a.GetProperty("isVisible").GetBoolean()
                && a.GetProperty("showedOn").GetString()!.Contains("Query"))
            .OrderBy(a => a.GetProperty("order").GetInt32())
            .Select(a => a.GetProperty("name").GetString()!)];

    [Fact]
    public void Repository_grid_shows_the_curated_columns_with_the_repository_name_first()
    {
        // The row link lives on the first visible column; Account/OwnerLogin are
        // constant on the account page and belong on the detail view only.
        VisibleQueryColumns(LoadPersistentObject("Repository")).Should().Equal(
            "Name", "LatestCoverage", "FullName", "LatestCoverageSha");
    }

    [Fact]
    public void Build_grid_shows_the_curated_columns()
    {
        VisibleQueryColumns(LoadPersistentObject("Build")).Should().Equal(
            "Run", "Status", "Sessions", "Coverage", "CreatedAtUtc");
    }
}
