$scriptId = 'test'
$githubToken = 'testToken'
$githubIssueNumber = 366
$githubActionUrl = 'https://github.com/MihuBot/runtime-utils/actions/runs/8992730947'

$script = 'curl -X POST -H "Authorization: token $githubToken" -H "Content-Type: application/json" https://httpbin.org/anything/$githubIssueNumber/comments --data "{`"body`":`"$githubActionUrl`""'
$script | Invoke-Expression