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

        await RunBatchScriptAsync("clone-rebase.bat",
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
            git {{(isRebase ? "rebase" : "merge")}} main
            """,
            line => line.Replace(pushToken, "<REDACTED>", StringComparison.OrdinalIgnoreCase));

        await RunBatchScriptAsync("push.bat",
            $$"""
            cd runtime
            git push pr {{(isRebase ? "-f" : "")}}
            """);
    }

    private async Task RunBatchScriptAsync(string name, string script, Func<string, string>? processLogs = null)
    {
        File.WriteAllText(name, script);
        await RunProcessAsync(name, string.Empty, processLogs: processLogs);
    }
}
