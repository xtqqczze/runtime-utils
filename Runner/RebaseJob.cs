namespace Runner;

internal sealed class RebaseJob : JobBase
{
    private string SourceRepo => Metadata["PrRepo"];
    private string SourceBranch => Metadata["PrBranch"];

    public RebaseJob(HttpClient client, Dictionary<string, string> metadata) : base(client, metadata) { }

    protected override async Task RunJobCoreAsync()
    {
        string pushToken = Metadata["MihuBotPushToken"];

        bool isRebase = CustomArguments.StartsWith("rebase", StringComparison.OrdinalIgnoreCase);
        bool isJitFormat = CustomArguments.Contains("format", StringComparison.OrdinalIgnoreCase);

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
            await RunBatchScriptAsync("test.bat",
                """
                cd runtime
                git revert --no-edit ad1bb21f4a3c1bcc9375229ce9dc7d5b2183cee3
                """);

            await RunProcessAsync(
                "python",
                "runtime/src/coreclr/scripts/jitformat.py -r runtime -a x64 -o windows",
                checkExitCode: false);

            if (!File.Exists("runtime/format.patch"))
            {
                throw new Exception("Expected jitformat to require changes.");
            }

            await UploadArtifactAsync("runtime/format.patch");

            await RunBatchScriptAsync("patch.bat",
                """
                cd runtime
                git apply format.patch
                rm format.patch
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

    private async Task<int> RunBatchScriptAsync(string name, string script, Func<string, string>? processLogs = null, bool checkExitCode = true)
    {
        File.WriteAllText(name, script);
        return await RunProcessAsync(name, string.Empty, checkExitCode: checkExitCode, processLogs: processLogs);
    }
}
