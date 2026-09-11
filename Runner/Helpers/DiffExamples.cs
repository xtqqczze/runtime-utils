using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;

namespace Runner.Helpers;

internal sealed class DiffExamples
{
    public const int MaxEntries = 2_000;
    public const int MaxDiffLength = 128 * 1024;
    public const int TotalDiffLength = 16 * 1024 * 1024;

    public string Summary { get; set; } = "";
    public List<string> Notes { get; } = [];
    public List<Entry> Entries { get; } = [];

    public sealed record Entry(
        string Assembly, string Method, string Category, string Description,
        string? ExtraInfo, string Diff, bool Truncated, long? BaseBytes = null, long? DiffBytes = null);

    private readonly record struct MethodRange(long Offset, long Length);

    private sealed record Method(long Bytes, UInt128 Hash, MethodRange FirstRange, List<MethodRange>? AdditionalRanges)
    {
        public int Occurrences => 1 + (AdditionalRanges?.Count ?? 0);
    }
    private sealed record Candidate(string Assembly, string Name, string Category, Method? Base, Method? Diff)
    {
        public long Delta => (Diff?.Bytes ?? 0) - (Base?.Bytes ?? 0);
    }

    public static string LimitDiff(string diff, int limit, out bool truncated)
    {
        truncated = diff.Length > limit;
        if (!truncated)
        {
            return diff;
        }

        const string Notice = "\n... diff truncated; middle omitted ...\n";
        int half = (limit - Notice.Length) / 2;
        int startEnd = diff.LastIndexOf('\n', half);
        int endStart = diff.IndexOf('\n', diff.Length - half);
        return diff[..(startEnd >= 0 ? startEnd : half)] + Notice +
            diff[(endStart >= 0 ? endStart + 1 : diff.Length - half)..];
    }

    public static async Task<DiffExamples> CreateJitAsync(
        JobBase job, string mainDirectory, string prDirectory,
        Func<string, string?>? extraInfo = null, Func<string, string>? displayName = null)
    {
        DiffExamples report = new();
        int changedCount = 0;
        int incompleteCount = 0;
        int newCount = 0;
        int removedCount = 0;
        bool includeNew = job.TryGetFlag("includeNewMethodRegressions");
        bool includeRemoved = job.TryGetFlag("includeRemovedMethodImprovements");

        string[] assemblies = Directory.EnumerateFiles(mainDirectory, "*.dasm", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(mainDirectory, path))
            .Union(Directory.EnumerateFiles(prDirectory, "*.dasm", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(prDirectory, path)), StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        await job.LogAsync($"Indexing diff examples across {assemblies.Length:N0} assemblies");
        Candidate[][] candidatesByAssembly = new Candidate[assemblies.Length][];
        await Parallel.ForAsync(0, assemblies.Length, job.JobTimeout, async (index, cancellationToken) =>
        {
            string assembly = assemblies[index];
            var baseline = await IndexMethodsAsync(Path.Combine(mainDirectory, assembly), cancellationToken);
            var diff = await IndexMethodsAsync(Path.Combine(prDirectory, assembly), cancellationToken);
            Interlocked.Add(ref incompleteCount, baseline.Incomplete.Count + diff.Incomplete.Count);
            List<Candidate> assemblyCandidates = [];

            foreach (string name in baseline.Methods.Keys.Union(diff.Methods.Keys, StringComparer.Ordinal))
            {
                if (baseline.Incomplete.Contains(name) || diff.Incomplete.Contains(name))
                {
                    continue;
                }
                baseline.Methods.TryGetValue(name, out Method? before);
                diff.Methods.TryGetValue(name, out Method? after);
                if (before is null)
                {
                    Interlocked.Increment(ref newCount);
                }
                if (after is null)
                {
                    Interlocked.Increment(ref removedCount);
                }
                if ((before?.Hash == after?.Hash && before?.Bytes == after?.Bytes) ||
                    (before is null && !includeNew) || (after is null && !includeRemoved))
                {
                    continue;
                }

                long delta = (after?.Bytes ?? 0) - (before?.Bytes ?? 0);
                string category = delta < 0 ? "improvement" : delta > 0 ? "regression" : "same-size";
                assemblyCandidates.Add(new(assembly, name, category, before, after));
            }

            Interlocked.Add(ref changedCount, assemblyCandidates.Count);
            // No single assembly/category can consume the global example budget.
            candidatesByAssembly[index] = assemblyCandidates.GroupBy(c => c.Category)
                .SelectMany(group => group.OrderByDescending(c => Math.Abs(c.Delta))
                    .ThenBy(c => c.Name, StringComparer.Ordinal).Take(MaxEntries))
                .ToArray();
        });

        Candidate[] selected = candidatesByAssembly.SelectMany(candidates => candidates)
            .GroupBy(c => (c.Assembly, c.Category))
            .SelectMany(group => group.Select((candidate, rank) => (Candidate: candidate, Rank: rank)))
            .OrderBy(item => item.Rank)
            .ThenBy(item => item.Candidate.Assembly, StringComparer.Ordinal)
            .ThenBy(item => item.Candidate.Category, StringComparer.Ordinal)
            .Take(MaxEntries)
            .Select(item => item.Candidate)
            .ToArray();

        await job.LogAsync($"Generating {selected.Length:N0} diff examples from {changedCount:N0} changed method listings");
        int limit = Math.Min(MaxDiffLength, TotalDiffLength / Math.Max(1, selected.Length));
        Entry?[] entries = new Entry?[selected.Length];
        int noisyCount = 0;
        bool includeNoise = job.TryGetFlag("includeKnownNoise");

        await Parallel.ForEachAsync(selected.Select((candidate, index) => (Candidate: candidate, Index: index))
            .GroupBy(item => item.Candidate.Assembly),
            job.JobTimeout,
            async (group, cancellationToken) =>
            {
                var files = group.ToDictionary(item => item.Candidate.Name,
                    _ => (Base: new TempFile("txt"), Diff: new TempFile("txt")), StringComparer.Ordinal);
                try
                {
                    await ExtractMethodsAsync(Path.Combine(mainDirectory, group.Key),
                        group.Select(item => (item.Candidate.Base, files[item.Candidate.Name].Base.Path)), cancellationToken);
                    await ExtractMethodsAsync(Path.Combine(prDirectory, group.Key),
                        group.Select(item => (item.Candidate.Diff, files[item.Candidate.Name].Diff.Path)), cancellationToken);

                    foreach (var (candidate, index) in group)
                    {
                        var paths = files[candidate.Name];
                        bool compact = new FileInfo(paths.Base.Path).Length + new FileInfo(paths.Diff.Path).Length > limit;
                        List<string> lines = await GitHelper.DiffAsync(job, paths.Base.Path, paths.Diff.Path, fullContext: !compact, preserveHunkHeaders: true);
                        if (!includeNoise && lines.Any(line => JitDiffUtils.LineIsIndicativeOfKnownNoise(line.AsSpan().TrimStart())))
                        {
                            Interlocked.Increment(ref noisyCount);
                            continue;
                        }

                        if (lines.Count == 0)
                        {
                            continue;
                        }

                        string text = LimitDiff(string.Join('\n', lines), limit, out bool truncated);
                        string description = $"{candidate.Base?.Bytes ?? 0:N0} -> {candidate.Diff?.Bytes ?? 0:N0} bytes ({candidate.Delta:+#;-#;0})";
                        if (candidate.Base is null || candidate.Diff is null)
                        {
                            description += candidate.Base is null ? "; new method" : "; removed method";
                        }
                        if (candidate.Base?.Occurrences > 1 || candidate.Diff?.Occurrences > 1)
                        {
                            description += $"; {candidate.Base?.Occurrences ?? 0} -> {candidate.Diff?.Occurrences ?? 0} listings";
                        }

                        entries[index] = new(
                            candidate.Assembly, displayName?.Invoke(candidate.Name) ?? candidate.Name,
                            candidate.Category, description, extraInfo?.Invoke(candidate.Name),
                            text, compact || truncated, candidate.Base?.Bytes ?? 0, candidate.Diff?.Bytes ?? 0);
                    }
                }
                finally
                {
                    foreach (var paths in files.Values)
                    {
                        paths.Base.Dispose();
                        paths.Diff.Dispose();
                    }
                }
            });

        report.Entries.AddRange(entries.Where(e => e is not null)!);
        report.Summary = $"{changedCount:N0} changed method listings; {report.Entries.Count:N0} examples across {report.Entries.Select(e => e.Assembly).Distinct().Count():N0} assemblies.";
        if (newCount > 0 || includeNew)
        {
            report.Notes.Add(includeNew
                ? $"{newCount:N0} new method listings are included with -includeNewMethodRegressions."
                : $"{newCount:N0} new method listings are excluded. Use -includeNewMethodRegressions to include them.");
        }
        if (removedCount > 0 || includeRemoved)
        {
            report.Notes.Add(includeRemoved
                ? $"{removedCount:N0} removed method listings are included with -includeRemovedMethodImprovements."
                : $"{removedCount:N0} removed method listings are excluded. Use -includeRemovedMethodImprovements to include them.");
        }
        if (changedCount > selected.Length)
        {
            report.Notes.Add($"Showing a sample of at most {MaxEntries:N0} methods; {changedCount - selected.Length:N0} additional changed listings were not selected.");
        }
        if (noisyCount > 0)
        {
            report.Notes.Add($"{noisyCount:N0} selected examples were excluded as known noise. Use -includeKnownNoise to include them.");
        }
        if (incompleteCount > 2)
        {
            report.Notes.Add($"{incompleteCount:N0} incomplete method listings could not be compared.");
        }
        if (report.Entries.Any(e => e.Truncated))
        {
            report.Notes.Add("Large methods use reduced context, and exceptionally large diffs omit their middle. Each affected example is marked. Use -uploadDasm for the complete JIT disassembly artifacts.");
        }
        return report;
    }

    private static async Task<(Dictionary<string, Method> Methods, HashSet<string> Incomplete)> IndexMethodsAsync(string path, CancellationToken cancellationToken)
    {
        Dictionary<string, Method> methods = new(StringComparer.Ordinal);
        HashSet<string> incomplete = new(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return (methods, incomplete);
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = new XxHash128();
        string? name = null;
        long? size = null;
        long methodOffset = 0;

        await ReadUtf8LinesAsync(stream, stream.Length, ProcessLine, cancellationToken);
        FinishMethod(stream.Position);
        return (methods, incomplete);

        void ProcessLine(ReadOnlySpan<byte> line, long startOffset, long endOffset)
        {
            ReadOnlySpan<byte> content = line[..^1];
            ReadOnlySpan<byte> methodPrefix = "; Assembly listing for method "u8;
            if (content.StartsWith(methodPrefix))
            {
                FinishMethod(startOffset);
                name = Encoding.UTF8.GetString(content[methodPrefix.Length..]);
                methodOffset = startOffset;
            }
            if (name is null)
            {
                return;
            }

            ReadOnlySpan<byte> sizePrefix = "; Total bytes of code "u8;
            if (content.StartsWith(sizePrefix))
            {
                ReadOnlySpan<byte> value = content[sizePrefix.Length..];
                int end = value.IndexOfAnyExceptInRange((byte)'0', (byte)'9');
                size = long.Parse(end < 0 ? value : value[..end], CultureInfo.InvariantCulture);
                // Scores and other footer metadata alone are not changed machine code.
                return;
            }

            hash.Append(line);
            if (content.StartsWith("; ============================================================"u8))
            {
                FinishMethod(endOffset);
            }
        }

        void FinishMethod(long endOffset)
        {
            if (name is null)
            {
                return;
            }

            UInt128 fingerprint = hash.GetCurrentHashAsUInt128();
            hash.Reset();
            if (size is { } bytes)
            {
                methods.TryGetValue(name, out Method? previous);

                MethodRange range = new(methodOffset, endOffset - methodOffset);
                List<MethodRange>? additionalRanges = previous?.AdditionalRanges;
                if (previous is not null)
                {
                    Span<byte> hashes = stackalloc byte[32];
                    BinaryPrimitives.WriteUInt128LittleEndian(hashes, previous.Hash);
                    BinaryPrimitives.WriteUInt128LittleEndian(hashes[16..], fingerprint);
                    fingerprint = XxHash128.HashToUInt128(hashes);

                    additionalRanges ??= [];
                    additionalRanges.Add(range);
                }
                methods[name] = new(bytes + (previous?.Bytes ?? 0), fingerprint, previous?.FirstRange ?? range, additionalRanges);
            }
            else
            {
                incomplete.Add(name);
            }
            name = null;
            size = null;
        }
    }

    private static async Task ExtractMethodsAsync(string path, IEnumerable<(Method? Method, string OutputPath)> methods, CancellationToken cancellationToken)
    {
        FileStream? input = null;
        try
        {
            foreach (var (method, outputPath) in methods.OrderBy(item => item.Method?.FirstRange.Offset ?? -1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024);
                if (method is null)
                {
                    continue;
                }

                input ??= new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 1, FileOptions.Asynchronous | FileOptions.RandomAccess);
                await CopyRangeAsync(method.FirstRange);
                if (method.AdditionalRanges is { } ranges)
                {
                    foreach (MethodRange range in ranges)
                    {
                        await CopyRangeAsync(range);
                    }
                }

                async Task CopyRangeAsync(MethodRange range)
                {
                    input.Position = range.Offset;
                    await ReadUtf8LinesAsync(input, range.Length, (line, _, _) => output.Write(line), cancellationToken);
                }
            }
        }
        finally
        {
            if (input is not null)
            {
                await input.DisposeAsync();
            }
        }
    }

    private delegate void Utf8LineHandler(ReadOnlySpan<byte> line, long startOffset, long endOffset);

    // Lines include a normalized LF, but offsets always refer to the original file bytes.
    private static async Task ReadUtf8LinesAsync(Stream stream, long length, Utf8LineHandler processLine, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long offset = stream.Position;
        long remaining = length;
        int buffered = 0;
        int scan = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (remaining > 0)
                {
                    EnsureSpace();
                    int read = await stream.ReadAsync(buffer.AsMemory(buffered, (int)Math.Min(remaining, buffer.Length - buffered)), cancellationToken);
                    if (read == 0)
                    {
                        throw new EndOfStreamException("The disassembly file ended before its indexed byte range.");
                    }
                    buffered += read;
                    remaining -= read;
                }

                int start = 0;
                while (scan < buffered)
                {
                    int newline = buffer.AsSpan(scan, buffered - scan).IndexOfAny((byte)'\r', (byte)'\n');
                    if (newline < 0)
                    {
                        scan = buffered;
                        break;
                    }

                    int end = scan + newline;
                    bool carriageReturn = buffer[end] == (byte)'\r';
                    if (carriageReturn && end + 1 == buffered && remaining > 0)
                    {
                        scan = end;
                        break;
                    }

                    int next = end + 1;
                    if (carriageReturn && next < buffered && buffer[next] == (byte)'\n')
                    {
                        next++;
                    }
                    buffer[end] = (byte)'\n';
                    EmitLine(buffer.AsSpan(start, end - start + 1), offset + start, offset + next);
                    start = next;
                    scan = next;
                }

                buffer.AsSpan(start, buffered - start).CopyTo(buffer);
                buffered -= start;
                offset += start;
                scan -= start;
                if (remaining == 0)
                {
                    if (buffered > 0)
                    {
                        EnsureSpace();
                        buffer[buffered] = (byte)'\n';
                        EmitLine(buffer.AsSpan(0, buffered + 1), offset, offset + buffered);
                    }
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        void EnsureSpace()
        {
            if (buffered == buffer.Length)
            {
                byte[] larger = ArrayPool<byte>.Shared.Rent(checked(buffer.Length * 2));
                buffer.AsSpan(0, buffered).CopyTo(larger);
                ArrayPool<byte>.Shared.Return(buffer);
                buffer = larger;
            }
        }

        void EmitLine(ReadOnlySpan<byte> line, long start, long end)
        {
            if (start == 0 && line.StartsWith("\uFEFF"u8))
            {
                line = line[3..];
                start += 3;
                if (start == end)
                {
                    return;
                }
            }
            processLine(line, start, end);
        }
    }
}
