namespace Runner.Helpers;

internal static class GitHelper
{
    public static async Task<List<string>> DiffAsync(JobBase job, string leftFile, string rightFile, bool fullContext = false)
    {
        List<string> lines = [];

        await job.RunProcessAsync("git",
            $"diff --histogram {(fullContext ? "-U1000000" : "")} {leftFile} {rightFile}",
            lines,
            checkExitCode: false,
            suppressOutputLogs: true,
            suppressStartingLog: true);

        lines.RemoveAll(ShouldSkipLine);

        return lines;
    }

    private static bool ShouldSkipLine(string line)
    {
        ReadOnlySpan<char> span = line.AsSpan().TrimStart();

        return
            span.StartsWith("diff --git", StringComparison.Ordinal) ||
            span.StartsWith("index ", StringComparison.Ordinal) ||
            span.StartsWith("+++", StringComparison.Ordinal) ||
            span.StartsWith("---", StringComparison.Ordinal) ||
            span.StartsWith("@@", StringComparison.Ordinal) ||
            span.StartsWith("\\ No newline at end of file", StringComparison.Ordinal);
    }
}
