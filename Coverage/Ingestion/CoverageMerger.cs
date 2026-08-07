using Coverage.Entities;
using Coverage.Ingestion.Parsing;

namespace Coverage.Ingestion;

/// <summary>
/// Merge semantics: MAX, never sum. A retried job, a re-run attempt, or the
/// same file uploaded twice must not inflate counts — max is idempotent under
/// all three. Branches merge per (line, block, branch) key; line status is
/// recomputed from merged hits + merged branches.
/// </summary>
public static class CoverageMerger
{
    public static void MergeInto(FileCoverage target, ParsedFile parsed)
    {
        var lines = target.Lines.ToDictionary(l => l.Number);
        foreach (var (number, parsedLine) in parsed.Lines)
        {
            if (lines.TryGetValue(number, out var existing))
            {
                existing.Hits = MaxNullable(existing.Hits, parsedLine.Hits);
                existing.Status = (LineStatus)Math.Max((int)existing.Status, (int)parsedLine.Status);
            }
            else
            {
                lines[number] = new LineCoverage { Number = number, Hits = parsedLine.Hits, Status = parsedLine.Status };
            }
        }

        var branches = target.Branches.ToDictionary(b => (b.Line, b.BlockId, b.BranchId));
        foreach (var ((line, block, branch), taken) in parsed.Branches)
        {
            if (branches.TryGetValue((line, block, branch), out var existing))
            {
                existing.Taken = MaxNullable(existing.Taken, taken);
            }
            else
            {
                branches[(line, block, branch)] = new BranchCoverage { Line = line, BlockId = block, BranchId = branch, Taken = taken };
            }
        }

        // A line that was partial can become fully covered once another session
        // takes the remaining branches — recompute from the merged branch set.
        var partialLines = branches.Values
            .GroupBy(b => b.Line)
            .Where(g => g.Any(b => b.Taken is null or 0))
            .Select(g => g.Key)
            .ToHashSet();

        foreach (var line in lines.Values)
        {
            var executed = line.Hits is > 0 || (line.Hits is null && line.Status != LineStatus.NotCovered);
            if (executed)
                line.Status = partialLines.Contains(line.Number) ? LineStatus.PartiallyCovered : LineStatus.Covered;
        }

        target.Lines = [.. lines.Values.OrderBy(l => l.Number)];
        target.Branches = [.. branches.Values.OrderBy(b => b.Line).ThenBy(b => b.BlockId).ThenBy(b => b.BranchId)];
    }

    public static CoverageSummary Summarize(IEnumerable<FileCoverage> files)
    {
        var summary = new CoverageSummary();
        foreach (var file in files)
        {
            summary.FilesCount++;
            summary.LinesCoverable += file.Lines.Count;
            summary.LinesCovered += file.Lines.Count(l => l.Status != LineStatus.NotCovered);
            summary.BranchesTotal += file.Branches.Count;
            summary.BranchesCovered += file.Branches.Count(b => b.Taken is > 0);
        }
        return summary;
    }

    private static int? MaxNullable(int? a, int? b)
        => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
}
