# Running the review app

This page is for whoever deploys and operates the app. It collects the limits that have to agree with one another, the settings the
Azure Functions host needs before the app starts, and the checks to run after a deployment. The values quoted are the shipped ones
(`src/GitHubReviewApp/host.json` and `appsettings.json`). `FunctionBindingTests` pins the queue concurrency and the host timeout that
sections 1 and 3 rely on, so changing either is a decision to make with this page in view. Check the page whenever you change another
value it quotes.

The app reviews each pull request against MoneyMe's eight-check Code Review Checklist. For every review it asks the model twice, at the
same time, merges the two answers in code, and posts one comment and one check run. Sections 8 to 10 cover what the app needs from GitHub
and from Azure App Configuration.

## 1. How much runs at once

Per instance of the function app:

| What | Where it is set | Shipped | Meaning |
|---|---|---|---|
| Reviews at once | `extensions.queues.batchSize` and `newBatchThreshold` in host.json | 4 + 2 = 6 | The queue trigger takes a batch of 4 and fetches another whenever 2 are left, so up to 6 reviews run at once. The limit is per queue-triggered function: the poison-queue handler has its own. |
| Model runs of one review | fixed in code | 2 | Every review asks the model twice at once, so each review holds two model slots. |
| Model requests in flight, all reviews together | `Review:MaxConcurrentModelRequests` | 12 | A limiter shared by every review in the process. When more runs want to start, they wait for a slot. |

Across instances the numbers multiply. Each instance adds its own 6 reviews and its own 12 model requests, while the model provider's
rate limit belongs to the API key, not to an instance (see section 2).

Why 4 + 2 = 6 reviews beside a limiter of 12: a review that waits for a model slot holds its diff in memory and does nothing. With two
slots for each review, every running review can have both of its runs in flight. If you raise `batchSize` or `newBatchThreshold`, raise
the limiter with them (twice the number of reviews at once), and the other way round. `FunctionBindingTests` fails when the limiter's
default is below twice the reviews at once.

Memory grows with the number of reviews at once. The app keeps all of a pull request's patches in memory while it reviews it (GitHub
lists up to 3,000 files), plus up to `Review:MaxDiffBytes` (180,000 bytes) of rendered diff, which both runs of the review share.
Prefer a larger instance size over higher numbers here; on the Flex Consumption plan Microsoft recommends the 2,048 MB size as the default.

## 2. Cap how far the app scales out

The queue trigger scales the app out as the backlog of review requests grows, and every new instance adds 6 reviews. Set a maximum
instance count so that instances x 12 model requests stays within what your Anthropic key allows for the two models in
`Runtime:SonnetModel` and `Runtime:OpusModel` (requests and tokens per minute).

- **Flex Consumption**: `az functionapp scale config set --resource-group <group> --name <app> --maximum-instance-count <n>`. The default
  is 100 and the allowed range is 1 to 1,000. The count applies to each function group, and each queue-triggered function scales in its
  own group, so the group of the review function is the one that matters.
- **Premium and Consumption plans**: the `functionAppScaleLimit` site setting.

Start low (two or three) and raise it only while the telemetry shows no rate-limit trouble: `github_review_app.reviews.failed` with
`kind` = `transient` is the first place it shows up, and the model calls' spans carry the rate-limit exception.

Microsoft's pages for this: [Event-driven scaling, limit scale-out](https://learn.microsoft.com/en-us/azure/azure-functions/event-driven-scaling#limit-scale-out),
[Storage queue trigger concurrency](https://learn.microsoft.com/en-us/azure/azure-functions/functions-bindings-storage-queue-trigger#concurrency).

## 3. Timeouts that must nest

| Setting | Shipped | What it covers |
|---|---|---|
| `Claude:RequestTimeoutSeconds` | 600 | One model request, the SDK's own retries included (`Claude:MaxRetries`, shipped as 1). |
| `Review:MaxReviewDurationSeconds` | 1200 | The whole review: the GitHub calls, the wait for a free model slot and both model runs. |
| `functionTimeout` in host.json | 00:30:00 | The host's limit for one execution of a function. |

Each has to be longer than the one above it. The app checks the first two when it starts (`Review:MaxReviewDurationSeconds` must be at
least `Claude:RequestTimeoutSeconds`). It cannot see the third, so a test pins it at five minutes or more beyond the second.

Why they nest: when the host's timeout fires, the host restarts the worker process, and that takes every other review running on the
instance with it. The app's own limit ends a slow review first. That attempt fails as a transient failure, the message goes back to the
queue, and the answer of a model run that had finished is kept in memory for the retry when it runs on the same instance, so the retry
does not pay for that run again.

The shipped `functionTimeout` is the documented default for the Flex Consumption, Premium and Dedicated plans (30 minutes, with no
maximum). On the legacy Consumption plan the default is 5 minutes and the maximum 10, which is shorter than a review may take, so do not
run the app there. See [Function app timeout duration](https://learn.microsoft.com/en-us/azure/azure-functions/functions-scale#function-app-timeout-duration).

Retries also come from host.json: `maxDequeueCount` is 3 and `visibilityTimeout` is 5 minutes, so a failed attempt is tried again after
five minutes, at most three times in all. After the third failure the message moves to `ai-review-queue-poison`, and the poison handler
tells the pull request's author that the review could not be completed (a "Review failed" comment and a neutral check run, see sections 8 and
9). If the notice itself cannot be posted (GitHub is down), that message is tried three times as well and then lands in
`ai-review-queue-poison-poison`, which nothing reads: look in it when `github_review_app.failure_notices.failed` keeps rising, and clear it
by hand. If the host itself crashes in the middle of a run, the message becomes visible again after ten minutes whatever `visibilityTimeout`
says; that default belongs to the storage service.

## 4. Settings, secrets and the check after a deployment

The `Review:*` and `Claude:*` settings are checked when the host starts. A value that is invalid or out of range, and a key that the settings
class does not have (a misspelt name, which the configuration binder would otherwise skip without a word), stops the worker from starting,
and the startup log names the setting. A key that is missing altogether takes its default. Failing at once and visibly is deliberate: a wrong
value should not wait for some later review to show itself. The shipped `appsettings.json` sets these:

| Setting | Shipped | Meaning |
|---|---|---|
| `Review:MaxDiffBytes` | 180000 | The most rendered diff one review sends, in UTF-8 bytes. Files are taken highest risk first; a file that does not fit what is left is listed as left out. |
| `Review:MaxFileBytes` | 60000 | A file larger than this is cut at a line boundary and reported as partly reviewed. At least 10,000, and not above `Review:MaxDiffBytes`. |
| `Review:MaxListedFiles` | 1000 | The most changed files the review lists by name. The rest are counted in one line. |
| `Review:MaxDescriptionChars` | 10000 | A pull request description longer than this is cut, and the prompt says so. |
| `Review:OpusLineThreshold` | 40 | The stronger model is used only for a change of more than this many lines (added plus deleted) that touches a high-risk area. A change of exactly 40 lines stays on the other model. |
| `Review:HighRiskRepositories` | one entry, `MoneyMeTeam/Moneyme.Global.Library.VueApp` | Repositories, written `owner/repo`, that always count as a high-risk area, for a repository whose name does not say what it holds. See below. |
| `Review:MaxConcurrentModelRequests` | 12 | Section 1. |
| `Review:MaxReviewDurationSeconds` | 1200 | Section 3. |
| `Review:AllowedAuthorAssociations` | not set | Whose pull requests are reviewed. The default is every author (the app logs a warning about that at start outside Development): set it on any repository you do not control. |
| `Claude:Effort` | `high` | The effort level sent with every run. Leave it empty to omit it. |
| `Claude:MaxOutputTokens` | 32000 | The output cap of one run, thinking included. |
| `Claude:RequestTimeoutSeconds`, `Claude:MaxRetries` | 600, 1 | Section 3. |
| `Claude:IsRefusalFallbackEnabled` | true | A run that the asked model declines is answered by the model Anthropic picks for that refusal, in the same call. Off, a refusal ends the run. |
| `Claude:Pricing` | five models | What each model charges per million tokens (input, output, cache write, cache read), which puts a cost on every review. A model that is missing gives "cost unknown" and a warning, never a guess: add a model here whenever you point a `Runtime:*Model` setting at one that is not listed or the provider falls back to one. |

To change `Review:HighRiskRepositories`, edit `appsettings.json`. A list is written one entry per item. As an application setting an entry
is `Review__HighRiskRepositories__0`, `Review__HighRiskRepositories__1` and so on, and an entry at an index replaces the seeded entry at
that index. The list cannot be emptied by a setting: an empty `Review__HighRiskRepositories` value does not clear a seeded list, and a value
that is not empty and not an indexed entry (such as a comma-separated list) stops the worker from starting, with the startup log naming it.

The model ids and the on/off switch are not in those two sections. They are in a `Runtime` section, which the startup check does not bind,
so that a bad value can never stop the app from starting:

| Setting | Shipped | Meaning |
|---|---|---|
| `Runtime:IsReviewEnabled` | true | The switch. When it is false the webhook answers `200 Ignored: reviews are switched off.` and queues nothing, and a message already in the queue is skipped (`reviews.skipped` with `reason` = `SwitchedOff`). |
| `Runtime:SonnetModel` | `claude-sonnet-5-5` | The model for most reviews. |
| `Runtime:OpusModel` | `claude-opus-5-5` | The model for a change of more than `Review:OpusLineThreshold` lines in a high-risk area. |

These can be changed while the app runs through Azure App Configuration (section 10). Each value is checked on its own whenever it is read: a
model id has to look like `claude-` followed by lowercase letters, digits, dots and hyphens (at most 100 characters), and the switch has to be
`true` or `false`. A value that is missing takes its default. A value that is invalid keeps the last good value of that key and logs a warning
that names the key, never the value, so a bad model id never keeps the switch from turning reviews off.

The secrets are loaded into the same configuration, and the Key Vault provider reads a double hyphen in a secret's name as a colon. A secret
named `Claude--Something`, `Review--Something` or `Runtime--Something` would therefore be read as a setting under one of those sections
(the first two stop the worker, the third would be taken for a runtime setting). Keep this app's secrets out of those three prefixes. If the
vault is shared with other applications, check the name of any new secret in it against every application that reads the vault, not only
this one, because a secret added for another application can stop this one at its next start.

The secrets are `AnthropicApiKey`, `GitHubAppPrivateKey`, `GitHubWebhookSecret` and, optional, `AppConfigurationConnectionString` (section 10).
None is checked at startup. The webhook secret is read on every request, so a missing one fails every request. The Anthropic key and the
GitHub App's private key are read when a review first needs them, so a wrong value shows up on the first review. A connection string for App
Configuration that cannot be parsed is not used, and a warning at start names the secret.

After every deployment or settings change, check that the app started. A POST to `https://<function app host>/api/webhook/github` with no
signature should answer `401 Invalid signature.`: the signature is checked before anything else, so an unsigned request is refused. That
answer shows the worker is running with valid settings and can read the webhook secret. A 5xx, a 404 or no answer means the worker did not
start or the secret is missing: read the function app's startup log. It does not show that the secret matches GitHub's. For that, open the
GitHub App's settings, **Advanced**, **Recent deliveries** (kept for three days), and redeliver one that is not a `pull_request` delivery,
such as the `ping` GitHub sent when the webhook was created. The answer to expect is `200 Ignored: not a pull_request event.`. Do not
redeliver a `pull_request` delivery: it queues a real review.

After the first deployment of the checklist review, also do these:

1. Open a pull request on a test repository and let the first real review run. Look at the comment and at the check run on GitHub, because
   only GitHub shows how they render (the tables, the layout, the collapsed parts, the check run's title and summary). The tests cannot see that.
2. Run the live smoke test once, with a key, before relying on the answer schema. It calls the real API and costs a few cents, so it is
   skipped unless both `LIVE_MODEL_TESTS=1` and `ANTHROPIC_API_KEY` are set in the environment of the machine running the tests.
3. Ask the owner of the GitHub App to add the Checks permission (section 8). It can be done after the deployment: until it is added and each
   installation owner has approved it, reviews post the comment only, and the app logs a warning with each one.

## 5. Settings the Functions host reads itself

The host reads these before the app's own configuration exists, so they have to be application settings on the function app. The app
reads everything else through its own configuration.

| Setting | Value | For |
|---|---|---|
| `AzureWebJobsStorage__accountName` | the host's storage account | The host's own storage. Add `AzureWebJobsStorage__credential` = `managedidentity`, and `AzureWebJobsStorage__clientId` for a user-assigned identity. |
| `ReviewQueueConnection__queueServiceUri` | `https://<account>.queue.core.windows.net` | The queue the webhook writes and the processor and poison handler read. Add `ReviewQueueConnection__credential` = `managedidentity`, and `ReviewQueueConnection__clientId` for a user-assigned identity. |

`ReviewQueueConnection` is the connection name in code (`ReviewQueue.Connection`). The `__accountName` form is valid for
`AzureWebJobsStorage` only, which is why the queue connection uses the URI. The queue names `ai-review-queue` and
`ai-review-queue-poison` are fixed in code: renaming one orphans the messages already in it. Grant the identity the queue roles the
trigger and the output binding need; Microsoft lists them under *Grant permissions to an identity* in
[Configure connections to remote services](https://learn.microsoft.com/en-us/azure/azure-functions/manage-connections).

Prefer the `appsettings` files for `Review:*` and `Claude:*`, and the secret store that `Program.cs` loads for every secret, over
application settings. An application setting lives outside the repository, so it is neither versioned nor reviewed with the code, and it has
to be copied by hand to every new function app or slot you stand up, with nothing to remind you.

## 6. What to watch

All names start with `github_review_app.` except the token counters.

| Signal | What it tells you | Act when |
|---|---|---|
| `reviews.failed` by `kind` | `transient` is retried; `permanent` was given up on; `ai_spend_limit` means the provider's monthly cap is reached and nothing is retried until it is raised or the month ends; `exhausted` means the retries ran out, and is counted for each delivery of the poison message (up to three when the notice cannot be posted). | `permanent`, `ai_spend_limit` or `exhausted` appear at all. |
| `reviews.processed` by `result`, `runs` and `coverage` | Reviews whose result was published. `result` is `pass`, `pass_with_comments`, `fail` or `review_failed`; `runs` is the number of model runs that gave an answer (1 or 2, and 0 for `review_failed`); `coverage` is `full`, `partial` (something readable was not read) or `none` (no diff was sent). A `review_failed` result is counted here, with `runs` = 0 and `coverage` = `none`, and not in `reviews.failed`: the app did what it was built to do and told the author. | `review_failed` appears at all: neither run gave an answer, for a reason a retry would not change (declined by the safety checks, out of output space, unreadable, or refused by the AI service). Look at `review.runs` for which. A `fail` result is a normal verdict, not a fault. By itself `none` is normal for a pull request that holds only lockfiles, binaries or generated files, where no model runs. |
| `reviews.partial` | Reviews that were posted with a file cut, over the diff budget or without a diff. A file GitHub did not list at all (a pull request of more than 3,000 files) is not counted here, though the comment says it was not reviewed. | It becomes common: raise `Review:MaxDiffBytes` only with the model's context in view. |
| `review.runs` by `outcome` and `model` | How each model run ended: `Completed`, `OutputTruncated`, `Refused`, `InvalidOutput` or `Rejected`, and the model that answered (the one asked for when none did). A run that fails outright is thrown and retried, not counted here. | Anything but `Completed` becomes common, or a `model` appears that is not one of the two you set (the provider fell back). |
| `review.disagreements` by `kind` | Published reviews whose two runs did not agree: `checks` (a check was rated differently, or by one run only) or `verdict` (the runs' own verdicts differ; such a review counts under both). | The share of reviews that disagree climbs. The comment shows the stricter rating of each check, so disagreement costs nothing in safety but says the prompt or the model is unsteady. |
| `review.check_status` by `check` and `status` | The eight checks of every review that had a verdict, after merging. `check` is `standards-design`, `functionality`, `performance`, `security`, `testing`, `error-handling`, `scalability` or `dependencies`; `status` is `pass`, `concern`, `fail` or `na`. | One check is a `concern` or `fail` in nearly every review, or none ever is. |
| `review.serious` | Concerns flagged serious in published reviews, after merging. A serious flag is named in the headline ("PASS WITH COMMENTS (1 serious)") and never changes the verdict: only a failed Security check gives FAIL. | It jumps without a change in traffic. |
| `review.model_choice` by `tier` and `reason` | The model chosen for each published review, "Review failed" included: `tier` is `sonnet` or `opus`, `reason` is `high_risk`, `low_risk` or `small_change`. | The share of `opus` rises: it is the dearer model. |
| `review.cost` by `tier` | What one published review cost in US dollars, both runs together. A review where either run threw is not recorded (the cost of a run that failed outright is unknown), nor one whose model is missing from `Claude:Pricing`, which also logs a warning. | The cost of a review or the daily total moves without a matching change in traffic. |
| `reviews.skipped` by `reason` | A queued review that was not carried out: `NotOpen`, `Draft`, `Superseded`, `AlreadyReviewed`, `NothingToReview`, `InProgress` or `SwitchedOff`. A review dropped because the commit moved on or because the pull request closed after the model had run is counted here (`Superseded`, `NotOpen`, `Draft`) and not in the metrics above. | `SwitchedOff` appears when you did not switch reviews off. |
| `review.duration` by `outcome` | End-to-end time of an attempt. `outcome` is `posted`, `review_failed`, `skipped`, `retry`, `failed` or `interrupted`. | The 95th percentile nears 1,200,000 ms, the app's own limit. |
| `failure_notices.failed` by `outcome` | An attempt to tell an author that a review failed which itself failed: `refused` (GitHub will not accept it; the author is untold) or `retry` (it will be tried again, so one author can count more than once). | It keeps rising: authors are going untold. See `ai-review-queue-poison-poison` in section 3. |
| `reviews.interrupted` | Attempts cut short by a host shutdown, which return to the queue. | It rises with no deployment going on. |
| `files.reviewed` and `pull_request.changed_lines` | Files sent to the model, and lines changed, for published reviews that had a verdict. | For capacity planning only. |
| `gen_ai.usage.*_tokens` by `stop_reason` | Token spend, so replies that were cut off can be told from finished ones. | Spend moves without a matching change in traffic. |

The per-review metrics (`review.model_choice`, `review.cost`, `review.check_status`, `review.serious`, `review.disagreements`,
`reviews.partial`) are counted once, when the result is published, not once per attempt: a review that is retried is not counted twice,
and one that is dropped because the commit moved on is counted by the newer commit's review instead. `review.model_choice` and
`review.cost` include "Review failed"; the others cover the reviews that had a verdict.

Each published review also writes one structured log line (the review record): repository, pull request, commit, the model chosen, each run's
outcome, model, verdict and eight statuses, tokens and cost, and the merged result. It holds no diff text, no titles, no model-written text and
no secret.

## 7. Who can use the app

Keep the GitHub App private (**Only on this account** under who can install it). The author allow-list decides whose pull requests are reviewed
once the app is installed, but the person who installs an app is its owner on that repository, so a public app could be installed by anyone,
and every installation would spend your model budget. The app does not check which accounts have installed it. If it ever has to be public,
add that check in code first.

## 8. The GitHub App's permissions

| Permission | Level | For |
|---|---|---|
| Pull requests | Read and write | Reading the pull request and its files, and the app's one comment on it (a pull request's comments are issue comments). |
| Checks | Read and write | Creating the `MoneyMe AI review` check run (section 9). |
| Metadata | Read | Required of every GitHub App. |

Contents is no longer used, so the app does not need it. The owner may keep it or drop it; dropping it narrows what a leaked key could read.

The app asks for a token for the one repository with `pull_requests: write` and `checks: write`. When GitHub answers 422 because the
installation has not been granted Checks, the app asks again without it and remembers that this installation has no checks access. The review
then runs without a check run: the comment, and the app's record of what it has reviewed, are unaffected. So the Checks permission can be added
after the deployment, and until each installation owner has approved it, reviews post the comment only and the log says why. After it is
approved, the app uses it from the next token it fetches (a token is cached until shortly before it expires, so allow up to an hour).

The webhook needs the **Pull request** event, as before. The app answers 202 for a `pull_request` event of the four actions it reviews
(`opened`, `reopened`, `synchronize`, `ready_for_review`) on a pull request that is not a draft, from an author the allow-list accepts.

## 9. The check run

Each finished review creates one check run named `MoneyMe AI review` on the pull request's head commit. It is created already completed,
in one request. There is no in-progress state, and nothing is created for a review that is abandoned (the pull request closed, was turned into
a draft, or moved to a newer commit), so no unfinished run is left behind.

| Result | Conclusion on GitHub | Title |
|---|---|---|
| PASS | `success` | The verdict, for example "PASS" |
| PASS WITH COMMENTS | `neutral` | The verdict |
| FAIL | `failure` | The verdict |
| Review failed | `neutral` | "Review failed" |

When the review has serious concerns, the title adds their number after the verdict, for example "PASS WITH COMMENTS (1 serious)". FAIL is
given only when the Security check failed; PASS WITH COMMENTS when any other check is a concern or failed; PASS otherwise.

The summary is the comment's text without its hidden marker. The run's `external_id` holds the pull request number.

The comment is separate. It is one comment per pull request, written by the app's bot login and updated in place, and its last line is a
hidden marker, `<!-- moneyme-ai-review sha=<commit> outcome=reviewed -->` (or `outcome=failed`). That marker is how the app knows it has
reviewed a commit: a pull request counts as reviewed at a commit when the app's own comment on it carries a `reviewed` marker for that commit.
A "Review failed" comment never counts, so reopening the pull request or pushing a commit tries again. The check run is written first and the
comment last, so a failure between the two leads to a retry instead of a pull request that was never marked reviewed. A retry may add a second,
identical check run, which is harmless.

When two open pull requests share a head commit (one branch with pull requests into two bases), GitHub keeps check runs per commit and shows
the most recent run of a name. The check therefore shows whichever pull request's review finished last, while each pull request's comment
shows its own review. This is rare and accepted.

If you make `MoneyMe AI review` a required check in a ruleset or branch protection rule, two things follow:

- A pull request cannot merge until its review has reported, because GitHub waits for a required check that is missing. A pull request that
  cannot be reviewed (the author is not on the allow-list, a draft, reviews switched off) therefore cannot merge either.
- Pin the required check to this GitHub App as its expected source. Any app, and any workflow, can create a check run with the same name, and
  without a pinned source such a run could satisfy the rule. In the ruleset's required status check, choose this app under the source (the
  option is labelled **GitHub App** or **Integration**). Leave it on "any source" only if you accept that.

## 10. Azure App Configuration

App Configuration is optional. It lets you switch reviews off and change the two model ids while the app runs, without a deployment. With no
`AppConfigurationConnectionString` secret, the app does not use it, logs that the values in `appsettings.json` are in use, and those apply
for the life of the process.

The app reads the store through its read-only access-key connection string, which is kept in Key Vault as `AppConfigurationConnectionString`
and loaded like every other secret. The read-write keys stay with the owners of the store's configuration: the app never needs them. Do not
turn off local authentication (access keys) on the store, because this connection string is how the app and the other consumers of the store
connect, and they would all lose access.

The app reads these four keys, with no label, and nothing else in the store:

| Key | Value | Meaning |
|---|---|---|
| `GitHubReviewApp:Runtime:IsReviewEnabled` | `true` or `false` | The switch (section 4). |
| `GitHubReviewApp:Runtime:SonnetModel` | a model id | The model for most reviews. |
| `GitHubReviewApp:Runtime:OpusModel` | a model id | The model for large changes in high-risk areas. |
| `GitHubReviewApp:Sentinel` | any text | Change this value to make the app read the other keys again. |

The app selects only keys that start with `GitHubReviewApp:Runtime:`, so the store can never reach the settings that are validated at
startup. The key `GitHubReviewApp:Runtime:SonnetModel` is read as the setting `Runtime:SonnetModel`.

To change a value, change the key and then change the sentinel, in that order. The app does not look at the other keys until the sentinel has
changed. It checks the sentinel when a function starts, at most every 120 seconds in Production and every 30 seconds in other
environments, so a change reaches an instance on the first invocation after that interval has passed. A review that is running keeps the
model it started with.

Last-known-good behaviour:

- At startup the app waits at most three seconds for the store. If the store cannot be reached, the app starts on the values in
  `appsettings.json`, and the refresh picks the store up when it is reachable again. A cold start is therefore never held for long by an
  outage. The package logs "A refresh operation failed." for each refresh that fails.
- When a refresh fails, the values already read stay in use.
- A key that is missing from the store takes its default from `appsettings.json`. A key with an invalid value keeps its last good value, with
  a warning that names the key (section 4). The other keys are not held back by it.
- A connection string that is not well formed is not used at all: the app still starts, the `appsettings.json` values apply, and a warning
  names the secret, never its value.

Switching reviews off is the fast way to stop spending: set `GitHubReviewApp:Runtime:IsReviewEnabled` to `false` and change the sentinel.
Messages already in the queue are skipped, not reviewed, and `reviews.skipped` with `reason` = `SwitchedOff` counts them. A pull request
skipped this way is reviewed again only when it gets a new commit or is reopened, because the app does not replay the queue.
