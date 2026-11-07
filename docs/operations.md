# Running the review app

This page is for whoever deploys and operates the app. It collects the limits that have to agree with one another, the settings the
Azure Functions host needs before the app starts, and the checks to run after a deployment. The values quoted are the shipped ones
(`src/GitHubReviewApp/host.json` and `appsettings.json`). `FunctionBindingTests` pins the queue concurrency and the host timeout that
sections 1 and 3 rely on, so changing either is a decision to make with this page in view. Check the page whenever you change another
value it quotes.

## 1. How much runs at once

Per instance of the function app:

| What | Where it is set | Shipped | Meaning |
|---|---|---|---|
| Reviews at once | `extensions.queues.batchSize` and `newBatchThreshold` in host.json | 4 + 2 = 6 | The queue trigger takes a batch of 4 and fetches another whenever 2 are left, so up to 6 reviews run at once. The limit is per queue-triggered function: the poison-queue handler has its own. |
| Parts of one review in parallel | `Review:MaxParallelChunks` | 3 | A large pull request is reviewed in parts. At the shipped sizes a review has at most five parts, which run in two waves of three. |
| Model requests in flight, all reviews together | `Review:MaxConcurrentModelRequests` | 6 | A limiter shared by every review in the process. When more parts want to run, they wait for a slot. |

Across instances the numbers multiply. Each instance adds its own 6 reviews and its own 6 model requests, while the model provider's
rate limit belongs to the API key, not to an instance (see section 2).

Why 4 + 2 = 6 beside a limiter of 6: a review that waits for a model slot holds its diff in memory and does nothing. With as many slots
as reviews, every running review can have a request in flight. If you raise `batchSize` or `newBatchThreshold`, raise the limiter with
them, and the other way round.

Memory grows with the number of reviews at once. The app keeps all of a pull request's patches in memory while it reviews it (GitHub
lists up to 3,000 files), plus up to `Review:MaxTotalBytes` of rendered diff. Prefer a larger instance size over higher numbers here; on
the Flex Consumption plan Microsoft recommends the 2,048 MB size as the default.

## 2. Cap how far the app scales out

The queue trigger scales the app out as the backlog of review requests grows, and every new instance adds 6 reviews. Set a maximum
instance count so that instances x 6 model requests stays within what your Anthropic key allows for the model in `Claude:Model` (requests
and tokens per minute).

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
| `Review:MaxReviewDurationSeconds` | 1200 | The whole review: the GitHub calls, the wait for a free model slot and every part. |
| `functionTimeout` in host.json | 00:30:00 | The host's limit for one execution of a function. |

Each has to be longer than the one above it. The app checks the first two when it starts (`Review:MaxReviewDurationSeconds` must be at
least `Claude:RequestTimeoutSeconds`). It cannot see the third, so a test pins it at five minutes or more beyond the second.

Why they nest: when the host's timeout fires, the host restarts the worker process, and that takes every other review running on the
instance with it. The app's own limit ends a slow review first. That attempt fails as a transient failure, the message goes back to the
queue, and the model results of the parts that had finished are kept in memory for the retry when it runs on the same instance.

The shipped `functionTimeout` is the documented default for the Flex Consumption, Premium and Dedicated plans (30 minutes, with no
maximum). On the legacy Consumption plan the default is 5 minutes and the maximum 10, which is shorter than a review may take, so do not
run the app there. See [Function app timeout duration](https://learn.microsoft.com/en-us/azure/azure-functions/functions-scale#function-app-timeout-duration).

Retries also come from host.json: `maxDequeueCount` is 3 and `visibilityTimeout` is 5 minutes, so a failed attempt is tried again after
five minutes, at most three times in all. After the third failure the message moves to `ai-review-queue-poison`, and the poison handler
tells the pull request's author that the review could not be completed. If the notice itself cannot be posted (GitHub is down), that message
is tried three times as well and then lands in `ai-review-queue-poison-poison`, which nothing reads: look in it when
`github_review_app.failure_notices.failed` keeps rising, and clear it by hand. If the host itself crashes in the middle of a run, the
message becomes visible again after ten minutes whatever `visibilityTimeout` says; that default belongs to the storage service.

## 4. Startup validation and the check after a deployment

The `Review:*` and `Claude:*` settings are checked when the host starts. A value that is invalid or out of range, and a key that the settings
class does not have (a misspelt name, which the configuration binder would otherwise skip without a word), stops the worker from starting,
and the startup log names the setting. A key that is missing altogether takes its default. The shipped `appsettings.json` sets the model, the
limits and the budgets, but not `Review:AllowedAuthorAssociations`, whose default is every author (the app logs a warning about that at
start outside Development: set it on any repository you do not control), nor `Review:GuidelinePaths`. Failing at once and visibly is
deliberate: a wrong value should not wait for some later review to show itself.

The secrets are loaded into the same configuration, and the Key Vault provider reads a double hyphen in a secret's name as a colon. A secret
named `Claude--Something` or `Review--Something` would therefore be read as a setting the class does not have, and stop the worker. Keep
this app's secrets out of those two prefixes. If the vault is shared with other applications, check the name of any new secret in it against
every application that reads the vault, not only this one, because a secret added for another application can stop this one at its next start.

The secrets are not checked at startup. The webhook secret is read on every request, so a missing one fails every request. The Anthropic key
and the GitHub App's private key are read when a review first needs them, so a wrong value shows up on the first review.

After every deployment or settings change, check that the app started. A POST to `https://<function app host>/api/webhook/github` with no
signature should answer `401 Invalid signature.`: the signature is checked before anything else, so an unsigned request is refused. That
answer shows the worker is running with valid settings and can read the webhook secret. A 5xx, a 404 or no answer means the worker did not
start or the secret is missing: read the function app's startup log. It does not show that the secret matches GitHub's. For that, open the
GitHub App's settings, **Advanced**, **Recent deliveries** (kept for three days), and redeliver one that is not a `pull_request` delivery,
such as the `ping` GitHub sent when the webhook was created. The answer to expect is `200 Ignored: not a pull_request event.`. Do not
redeliver a `pull_request` delivery: it queues a real review.

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

| Signal | What it tells you | Act when |
|---|---|---|
| `github_review_app.reviews.failed` by `kind` | `transient` is retried; `permanent` was given up on; `ai_spend_limit` means the provider's monthly cap is reached and nothing is retried until it is raised or the month ends; `exhausted` means the retries ran out, and is counted for each delivery of the poison message (up to three when the notice cannot be posted). | `permanent`, `ai_spend_limit` or `exhausted` appear at all. |
| `github_review_app.reviews.processed` by `coverage` | `full`, `partial` (something readable was not read) or `none` (a review was posted but no file was reviewed). | `none` appears together with `chunks` outcomes other than `Completed`: every part failed. By itself `none` is normal for a pull request that holds only lockfiles, binaries or generated files, where no model runs. |
| `github_review_app.chunks` by `outcome` | How each model request ended: `Completed`, `OutputTruncated`, `Refused`, `InvalidOutput`, `Rejected` or `Failed`. | Anything but `Completed` becomes common. |
| `github_review_app.review.duration` | End-to-end time of an attempt. | The 95th percentile nears 1,200,000 ms, the app's own limit. |
| `github_review_app.failure_notices.failed` by `outcome` | An attempt to tell an author that a review failed which itself failed: `refused` (GitHub will not accept it; the author is untold) or `retry` (it will be tried again, so one author can count more than once). | It keeps rising: authors are going untold. See `ai-review-queue-poison-poison` in section 3. |
| `github_review_app.reviews.interrupted` | Attempts cut short by a host shutdown, which return to the queue. | It rises with no deployment going on. |
| `gen_ai.usage.*_tokens` by `stop_reason` | Token spend, so replies that were cut off can be told from finished ones. | Spend moves without a matching change in traffic. |

## 7. Who can use the app

Keep the GitHub App private (**Only on this account** under who can install it). The author allow-list decides whose pull requests are reviewed
once the app is installed, but the person who installs an app is its owner on that repository, so a public app could be installed by anyone,
and every installation would spend your model budget. The app does not check which accounts have installed it. If it ever has to be public,
add that check in code first.
