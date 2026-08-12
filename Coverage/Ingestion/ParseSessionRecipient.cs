using System.Text;
using Coverage.Entities;
using Coverage.Ingestion.Parsing;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents.Session;

namespace Coverage.Ingestion;

/// <summary>
/// Parses one uploaded session's raw report attachments and merges the result
/// into the Build's FileCoverage documents (max semantics). Sessions of a queue
/// are processed strictly FIFO (Spark messaging, MaxDocsPerBatch=1), so the
/// read-modify-write on FileCoverage needs no extra locking.
/// </summary>
public partial class ParseSessionRecipient : IRecipient<ParseSessionMessage>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly ICoverageParserFactory parserFactory;
    [Inject] private readonly ILogger<ParseSessionRecipient> logger;

    public async Task HandleAsync(ParseSessionMessage message, CancellationToken cancellationToken = default)
    {
        var build = await session.LoadAsync<Build>(message.BuildId, cancellationToken);
        var buildSession = build?.Sessions.FirstOrDefault(s => s.SessionId == message.SessionId);
        if (build is null || buildSession is null)
        {
            logger.LogWarning("Build {BuildId} / session {SessionId} not found — skipping parse", message.BuildId, message.SessionId);
            return;
        }

        try
        {
            var fileList = await ReadAttachmentText(build, UploadAttachments.FileListName(message.SessionId), cancellationToken) is { } fl
                ? fl.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];

            var touched = new Dictionary<string, FileCoverage>(StringComparer.Ordinal);
            var parsedAnything = false;

            foreach (var attachmentName in buildSession.RawFileNames)
            {
                var content = await ReadAttachmentText(build, attachmentName, cancellationToken);
                if (content is null)
                {
                    logger.LogWarning("Attachment {Name} missing on {BuildId}", attachmentName, message.BuildId);
                    continue;
                }

                var parser = parserFactory.Resolve(content);
                if (parser is null)
                {
                    logger.LogWarning("Unrecognized report format in {Name} on {BuildId}", attachmentName, message.BuildId);
                    continue;
                }

                var result = parser.Parse(content);
                var normalizer = new PathNormalizer(buildSession.RootDir, result.SourceRoots, fileList);

                foreach (var parsedFile in result.Files)
                {
                    var (path, matched) = normalizer.Normalize(parsedFile.RawPath);
                    var documentId = FileCoverage.DocumentId(build.Id!, path);

                    if (!touched.TryGetValue(documentId, out var fileCoverage))
                    {
                        fileCoverage = await session.LoadAsync<FileCoverage>(documentId, cancellationToken);
                        if (fileCoverage is null)
                        {
                            fileCoverage = new FileCoverage { BuildId = build.Id!, Path = path, Matched = matched };
                            await session.StoreAsync(fileCoverage, documentId, cancellationToken);
                        }
                        touched[documentId] = fileCoverage;
                    }

                    fileCoverage.Matched |= matched;
                    CoverageMerger.MergeInto(fileCoverage, parsedFile, parser.FormatName);
                }

                parsedAnything = true;
                logger.LogInformation("Parsed {Name} ({Format}, {Files} files) for {BuildId}",
                    attachmentName, parser.FormatName, result.Files.Count, message.BuildId);
            }

            buildSession.ParseStatus = parsedAnything ? "Parsed" : "Failed";
            buildSession.Error = parsedAnything ? null : "No parsable coverage report found in the upload";
            buildSession.FilesCount = touched.Count;

            // Persist the merged FileCoverage docs first — the summary streams
            // them back from the server (it must also cover files earlier
            // sessions touched but this one didn't).
            await session.SaveChangesAsync(cancellationToken);
            await RecomputeBuildSummary(build, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to parse session {SessionId} of {BuildId}", message.SessionId, message.BuildId);
            buildSession.ParseStatus = "Failed";
            buildSession.Error = ex.Message;
        }

        await session.SaveChangesAsync(cancellationToken);
    }

    private async Task RecomputeBuildSummary(Build build, CancellationToken cancellationToken)
    {
        var files = new List<FileCoverage>();
        await using (var stream = await session.Advanced.StreamAsync<FileCoverage>(
            startsWith: $"{build.Id}/files/", token: cancellationToken))
        {
            while (await stream.MoveNextAsync())
            {
                files.Add(stream.Current.Document);
            }
        }

        build.Coverage = CoverageMerger.Summarize(files.Where(f => f.Matched));
    }

    private async Task<string?> ReadAttachmentText(Build build, string name, CancellationToken cancellationToken)
    {
        var attachment = await session.Advanced.Attachments.GetAsync(build, name, cancellationToken);
        if (attachment is null) return null;

        await using var stream = attachment.Stream;
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();

        // Uploads may be gzipped (magic 1f 8b) — the action gzips by default.
        if (bytes.Length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
        {
            using var gzip = new System.IO.Compression.GZipStream(new MemoryStream(bytes), System.IO.Compression.CompressionMode.Decompress);
            using var decompressed = new MemoryStream();
            await gzip.CopyToAsync(decompressed, cancellationToken);
            bytes = decompressed.ToArray();
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
