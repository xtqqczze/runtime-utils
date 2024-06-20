namespace Runner;

internal sealed class RebaseJob : JobBase
{
    public RebaseJob(HttpClient client, Dictionary<string, string> metadata) : base(client, metadata) { }

    protected override async Task RunJobCoreAsync()
    {
        string pushToken = Metadata["MihuBotPushToken"];

        bool isJitFormat = CustomArguments.Contains("format", StringComparison.OrdinalIgnoreCase);
        bool isRebase = !isJitFormat && CustomArguments.StartsWith("rebase", StringComparison.OrdinalIgnoreCase);

        await RunBatchScriptAsync("clone.bat",
            $$"""
            git config --system core.longpaths true
            git clone --progress https://github.com/dotnet/runtime runtime
            cd runtime
            git log -1
            git config --global user.email mihubot@mihubot.xyz
            git config --global user.name MihuBot
            git remote add pr https://MihuBot:{{pushToken}}@github.com/{{SourceRepo}}.git
            git fetch pr {{SourceBranch}}
            git checkout {{SourceBranch}}
            git log -1
            """,
            line => line.Replace(pushToken, "<REDACTED>", StringComparison.OrdinalIgnoreCase));

        if (isJitFormat)
        {
            await RunProcessAsync(
                "python",
                "runtime/src/coreclr/scripts/jitformat.py -r runtime -a x64 -o windows",
                checkExitCode: false);

            if (File.Exists("runtime/format.patch"))
            {
                if (new FileInfo("runtime/format.patch").Length > 0)
                {
                    await UploadArtifactAsync("runtime/format.patch");
                }

                File.Delete("runtime/format.patch");
            }

            await RunBatchScriptAsync("commit.bat",
                """
                cd runtime
                git diff
                git add .
                git commit -m "Apply jitformat patch"
                """);
        }
        else
        {
            await RunBatchScriptAsync("rebase.bat",
                $$"""
                cd runtime
                git {{(isRebase ? "rebase" : "merge")}} main
                """);
        }

        if (!TryGetFlag("no-push"))
        {
            await RunBatchScriptAsync("push.bat",
                $$"""
                cd runtime
                git push pr {{(isRebase ? "-f" : "")}}
                """);
        }
    }

    private async Task<int> RunBatchScriptAsync(string name, string script, Func<string, string>? processLogs = null)
    {
        File.WriteAllText(name, script);
        return await RunProcessAsync(name, string.Empty, processLogs: processLogs);
    }
}
