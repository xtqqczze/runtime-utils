using System.Net.Http.Headers;

namespace Runner.Jobs;

internal sealed class BackportJob : JobBase
{
    public BackportJob(HttpClient client, Dictionary<string, string> metadata) : base(client, metadata) { }

    protected override async Task RunJobCoreAsync()
    {
        string pushToken = Metadata["MihuBotPushToken"];

        string baseRepo = Metadata["BackportJob_BaseRepo"];
        string forkRepo = Metadata["BackportJob_ForkRepo"];
        string targetBranch = Metadata["BackportJob_TargetBranch"];
        string newBranch = Metadata["BackportJob_NewBranch"];
        string patchUrl = Metadata["BackportJob_PatchUrl"];
        string title = Metadata["BackportJob_Title"];

        File.WriteAllBytes("changes.patch", await HttpClient.GetByteArrayAsync(patchUrl));

        await RunBatchScriptAsync("backport.bat",
            $$"""
            git config --system core.longpaths true
            git clone --progress https://github.com/{{baseRepo}} repo
            cd repo
            git log -1
            git config --global user.email mihubot@mihubot.xyz
            git config --global user.name MihuBot
            git remote add fork https://MihuBot:{{pushToken}}@github.com/{{forkRepo}}.git
            git checkout {{targetBranch}}
            git switch -c {{newBranch}}
            git am --3way --empty=keep --ignore-whitespace --keep-non-patch ../changes.patch
            git push --set-upstream fork HEAD:{{newBranch}}
            """,
            line => line.Replace(pushToken, "<REDACTED>", StringComparison.OrdinalIgnoreCase));

        await CreatePullRequestAsync(pushToken, baseRepo, targetBranch, forkRepo, newBranch, title, body: string.Empty, maintainerCanModify: true);
    }

    private async Task<int> RunBatchScriptAsync(string name, string script, Func<string, string>? processLogs = null)
    {
        File.WriteAllText(name, script);
        return await RunProcessAsync(name, string.Empty, processLogs: processLogs);
    }

    private async Task CreatePullRequestAsync(string token, string baseRepo, string baseRef, string headRepo, string head, string title, string body, bool maintainerCanModify)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.github.com/repos/{baseRepo}/pulls");

        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        request.Content = JsonContent.Create(new
        {
            Title = title,
            Head = head,
            HeadRepo = headRepo,
            Base = baseRef,
            Body = body,
            MaintainerCanModify = maintainerCanModify
        }, options: new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        });

        using HttpResponseMessage response = await HttpClient.SendAsync(request, JobTimeout);

        await UploadTextArtifactAsync("GithubResponse.json", await response.Content.ReadAsStringAsync(JobTimeout));

        response.EnsureSuccessStatusCode();
    }
}
