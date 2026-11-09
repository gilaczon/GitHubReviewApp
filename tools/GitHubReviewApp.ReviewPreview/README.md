# Review preview

Runs the app's own review on any GitHub pull request and posts the result to a Slack conversation. Nothing is written to the pull request:
no comment, no check run. Use it to judge the reviews before installing the GitHub App on more repositories.

```bash
dotnet run --project tools/GitHubReviewApp.ReviewPreview -- https://github.com/my-org/my-service/pull/7
# or: dotnet run --project tools/GitHubReviewApp.ReviewPreview -- my-org/my-service#7
```

It takes as long as a real review: the two model runs go side by side, so a small pull request is done in well under a minute and a large
one can take several. It costs what a real review costs (two runs, on Sonnet or Opus as production would choose; a small pull request on
Opus is about USD 0.25). The console shows the review record (model, tokens and cost of each run) when it finishes.

## What is the same as production, and what is not

The same: the review code, `appsettings.json` (limits, model choice, prompts), the two runs, the merge, and the comment, word for word.

Different, on purpose:

- **Who reads the pull request.** Your GitHub CLI login (`gh auth token`), not the GitHub App, so it works on repositories where the app is not
  installed. Any pull request you can open on github.com, you can preview.
- **Merged, closed and draft pull requests are reviewed.** The app skips them; the preview reviews them as they stand, at their last commit.
- **An earlier review by the app is ignored**, so a pull request the app already reviewed is reviewed again.
- **The result goes to Slack.** The comment is shown in Slack's formatting (bold headings, inline code kept), under a header that links the
  pull request. A pull request with nothing to review gets a one-line note saying why.

## One-time setup

1. **GitHub CLI.** Install `gh` and run `gh auth login`. For an organisation with SSO, authorize the CLI's token for it when GitHub asks.
2. **Slack incoming webhook.** At <https://api.slack.com/apps> create an app (from scratch) in your workspace, open **Incoming Webhooks**,
   turn it on, and choose **Add New Webhook to Workspace**. Pick the conversation the previews go to (`DG05T25JQ`). The webhook's URL
   decides where messages go: the tool needs only the URL, not the channel id. If your workspace needs an admin to approve new apps,
   request it there.
3. **Secrets.** The tool reads two secrets, from user secrets (kept in your profile, outside the repository) or environment variables:
   `AnthropicApiKey` and `SlackWebhookUrl`. Both are secrets: never commit them or paste them into a ticket. To set them without leaving
   them in your shell history (Git Bash):

   ```bash
   read -rs ANTHROPIC_KEY && dotnet user-secrets set AnthropicApiKey "$ANTHROPIC_KEY" --project tools/GitHubReviewApp.ReviewPreview; unset ANTHROPIC_KEY
   read -rs SLACK_URL && dotnet user-secrets set SlackWebhookUrl "$SLACK_URL" --project tools/GitHubReviewApp.ReviewPreview; unset SLACK_URL
   ```

   Each `read -rs` waits for you to paste the value and shows nothing. In PowerShell, copy the value and set it from the clipboard, so no
   prompt is involved: `dotnet user-secrets set AnthropicApiKey (Get-Clipboard) --project tools/GitHubReviewApp.ReviewPreview`. (A hidden
   prompt such as `Read-Host -MaskInput` can take Ctrl+V as a typed character instead of a paste.) Or edit the file itself:
   `%APPDATA%\Microsoft\UserSecrets\github-review-app-review-preview\secrets.json`, a JSON object with the two names.

   `dotnet user-secrets list --project tools/GitHubReviewApp.ReviewPreview` prints the values, so check only that both names are there. The
   tool checks both before it starts: a key that does not start with `sk-ant-`, a URL that is not https, or either holding a space or a
   control character, stops it with nothing reviewed.

## Comparing models

The preview chooses the model as production does. To try another one, set it for one run with an environment variable, for example
`Runtime__SonnetModel=claude-opus-5-5` (the runtime settings, `docs/operations.md` section 4). Add a model that `Claude:Pricing` does not list
and the cost shows as unknown.

## When it fails

The tool prints the reason and exits with a non-zero code; nothing is posted to Slack. `is not set` or `is not an ...`: set that secret
again (setup step 3). `gave no token`: run `gh auth login`. A GitHub 404 on a
private repository: your login cannot read it, or its SSO authorization is missing. `Slack refused the message`: the webhook was removed or
its app uninstalled; create a new one.
