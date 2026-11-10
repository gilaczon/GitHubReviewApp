# Deploying the GitHub review app

This guide takes you from nothing to a working deployment: the Azure resources, the secrets, the code, the GitHub App, and installing it on
repositories. Follow the parts in order, because each one needs something from the one before it (the GitHub App needs the Function App's URL,
the code needs the secrets, and so on).

The Azure portal and GitHub move their buttons from time to time. Where a label here does not match what you see, look for the same words
nearby; the values to enter do not change. For the reasons behind the settings, see `docs/operations.md`.

## What you end up with

| Thing | Where | What it does |
|---|---|---|
| Function App (Flex Consumption, .NET 10 isolated, Linux) | Azure | Runs `WebhookReceiver` (takes GitHub's webhook calls) and `ReviewProcessor` (runs the reviews) |
| Storage account with the queue `ai-review-queue` | Azure | Holds review requests between the two functions |
| Three secrets in the shared Key Vault `moneyme-vault` | Azure | The Anthropic key, the GitHub App's private key and the webhook secret |
| GitHub App | GitHub organisation settings | Sends pull request events to the Function App; the app posts comments and check runs as it |

## Before you start

You need:

- **Azure:** rights to create a resource group and a Function App in the subscription, and rights to add secrets and access to the Key Vault
  `moneyme-vault` (or someone who has them, for Part 4).
- **GitHub:** owner of the organisation (for example `MoneyMeTeam`), or an owner who can create or edit the GitHub App and install it.
- **Anthropic:** access to the Anthropic Console for the organisation's account.
- **On your machine:**
  - .NET 10 SDK: `dotnet --version` shows `10.x`.
  - Azure Functions Core Tools v4: `func --version` shows `4.x`. If missing: `npm install -g azure-functions-core-tools@4 --unsafe-perm true`.
  - Azure CLI: `az --version`. Sign in with `az login`.
  - The repository, on an up-to-date `main`, with a clean build: `dotnet build GitHubReviewApp.slnx` and `dotnet test GitHubReviewApp.slnx`.

Keep a scratch note (not in the repository) of five values as you go: the **resource group**, the **Function App name**, its **default
domain**, the **storage account name**, and the **GitHub App ID**. Never write a secret into that note.

---

## Part 1. Create the Anthropic API key

Use a key that only this app uses, so its spend is visible on its own and it can be revoked without affecting anything else.

1. Open <https://console.anthropic.com> and sign in.
2. If your organisation uses workspaces, pick (or create) the workspace this app should bill to. Under the workspace's **Limits**, set a
   monthly spend limit you are comfortable with.
3. In the left menu click **API keys**, then **Create key**.
4. Name it `github-review-app`, choose the workspace, and click **Create key**.
5. Click **Copy**. Keep the key in your clipboard or a password manager only until Part 4; it is shown once.

---

## Part 2. Create the Azure resources

### 2.1 Resource group

1. In <https://portal.azure.com>, search for **Resource groups** and open it.
2. Click **+ Create**.
3. **Subscription:** the one this app should run in. **Resource group:** `rg-github-review-app`. **Region:** the region you use for
   other services (for example *Australia East*).
4. Click **Review + create**, then **Create**.

### 2.2 Function App

1. Search for **Function App** and open it. Click **+ Create**.
2. Choose **Flex Consumption** as the hosting plan and click **Select**. (Do not choose the legacy *Consumption* plan: its 10-minute limit is
   shorter than a review can take.)
3. **Basics** tab:
   - **Subscription:** as above. **Resource Group:** `rg-github-review-app`.
   - **Function App name:** `func-github-review-app` (it must be unique across Azure; add a suffix if it is taken).
   - **Region:** same as the resource group.
   - **Runtime stack:** `.NET`. **Version:** `10 (isolated worker model)`.
   - **Instance size:** `2048 MB`.
4. **Storage** tab: **Storage account:** click **Create new** and name it `stgithubreviewapp` (lowercase letters and digits only, unique
   across Azure). The app uses this account both for the Functions host and for its review queue.
5. **Networking** tab: leave **Enable public access** on. GitHub has to reach the webhook from the internet.
6. **Monitoring** tab: Application Insights is optional. The app's own telemetry goes to Uptrace (Part 4, `UptraceDsn`); turn Application
   Insights on only if you also want the host's logs there.
7. **Deployment** tab: leave continuous deployment **Disabled**. You deploy with the `func` command in Part 5.
8. **Authentication** tab: leave the defaults.
9. Click **Review + create**, then **Create**. Wait for **Your deployment is complete**, then click **Go to resource**.
10. On the Function App's **Overview** page, copy the **Default domain** (it looks like
    `func-github-review-app-<letters>.<region>-01.azurewebsites.net`) into your note. The webhook URL in Part 3 is built from it.

### 2.3 Turn on the Function App's managed identity

The app signs in to Key Vault and to the storage queue with this identity. No password is involved.

1. In the Function App, open **Settings** → **Identity**.
2. On the **System assigned** tab, set **Status** to **On** and click **Save**, then **Yes**.
3. Wait until an **Object (principal) ID** appears. That identity is what you grant access to next.

### 2.4 Create the review queue and grant access to it

1. Open the storage account `stgithubreviewapp` (search for it by name).
2. Open **Data storage** → **Queues**, click **+ Queue**, name it `ai-review-queue`, and click **OK**. (The app also creates
   `ai-review-queue-poison` and `ai-review-queue-poison-poison` itself when it needs them.)
3. Open **Access control (IAM)** → **+ Add** → **Add role assignment**.
4. On the **Role** tab, search for and select **Storage Queue Data Contributor**. Click **Next**.
5. On the **Members** tab, choose **Managed identity**, click **+ Select members**, set **Managed identity** to **Function App**, select
   `func-github-review-app`, and click **Select**.
6. Click **Review + assign** twice.

### 2.5 Application settings

1. In the Function App, open **Settings** → **Environment variables**. Stay on the **App settings** tab.
2. Click **+ Add** for each row below, then **Apply** at the bottom, and **Confirm**. Saving restarts the app, which is fine now.

| Name | Value | Required |
|---|---|---|
| `ReviewQueueConnection__queueServiceUri` | `https://stgithubreviewapp.queue.core.windows.net` (your storage account name) | Yes |
| `ReviewQueueConnection__credential` | `managedidentity` | Yes |
| `Review__AllowedAuthorAssociations__0` | `OWNER` | Recommended |
| `Review__AllowedAuthorAssociations__1` | `MEMBER` | Recommended |
| `Review__AllowedAuthorAssociations__2` | `COLLABORATOR` | Recommended |

- **Leave alone** the settings Azure created: `AzureWebJobsStorage` (or `AzureWebJobsStorage__*`), `FUNCTIONS_EXTENSION_VERSION`,
  `APPLICATIONINSIGHTS_CONNECTION_STRING` and any deployment storage settings. Do not add `FUNCTIONS_WORKER_RUNTIME` on Flex Consumption:
  the runtime comes from the stack you chose.
- **The three `Review__AllowedAuthorAssociations__*` rows** limit reviews to pull requests by organisation owners, members and
  collaborators. Without them every author's pull request is reviewed and the app logs a warning at start. On private repositories that is
  usually the same set of people, but setting them keeps an outside contributor from spending the review budget.
- **Do not** add the review settings from `appsettings.json` here (`Review__MaxDiffBytes`, `Review__TargetBranches__0` and so on). The
  file wins over app settings on any key it sets, so such a setting would be ignored. Change them in `appsettings.json` and redeploy.

### 2.6 Cap how far the app scales out

Every extra instance adds up to 12 model requests at once against the one Anthropic key. Start low.

1. In the Function App, open **Settings** → **Scale and concurrency**.
2. Set **Maximum instance count** to `3` and click **Save**.

(With the CLI: `az functionapp scale config set --resource-group rg-github-review-app --name func-github-review-app --maximum-instance-count 3`.)

---

## Part 3. Create (or update) the GitHub App

`src/GitHubReviewApp/appsettings.json` holds `"GitHubAppId": "3384929"`, the app created earlier for this project. If you are deploying with
that app, do **3.2 (Use the existing app)**. If you are creating a new one (for example a separate app for a test organisation), do **3.1**
instead, then come back to 3.3.

Whichever you do, first make the webhook secret: a random string that GitHub uses to sign every webhook call and the app uses to check the
signature. In PowerShell:

```powershell
[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)) | Set-Clipboard
```

That puts a 64-character secret on your clipboard without showing it. You paste it twice: into the GitHub App now, and into Key Vault in
Part 4. Keep it in a password manager until then, not in your note.

### 3.1 Create a new GitHub App

1. On GitHub, open the organisation: <https://github.com/organizations/MoneyMeTeam/settings/apps> (replace `MoneyMeTeam` with your
   organisation). Or: your avatar → **Your organizations** → the organisation → **Settings** → **Developer settings** → **GitHub Apps**.
2. Click **New GitHub App**.
3. Fill in:
   - **GitHub App name:** for example `MoneyMe AI Code Review`. This name is shown as the author of the comments.
   - **Homepage URL:** the repository's URL, or any page that describes the app.
   - **Callback URL**, **Setup URL**, **Request user authorization**, **Device flow:** leave empty and unticked.
   - **Webhook:** tick **Active**.
   - **Webhook URL:** `https://<default domain>/api/webhook/github`, using the default domain from step 2.2.10.
   - **Webhook secret:** paste the secret you just generated.
   - **SSL verification:** leave **Enabled**.
4. **Permissions** → **Repository permissions**:

   | Permission | Access |
   |---|---|
   | Checks | Read and write |
   | Pull requests | Read and write |
   | Metadata | Read-only (set automatically) |

   Leave every other permission at **No access**. (The app does not read repository contents through the Contents permission.)
5. **Subscribe to events:** tick **Pull request**. Leave the rest unticked.
6. **Where can this GitHub App be installed?** Choose **Only on this account**. A public app could be installed by anyone, and every
   installation would spend your review budget.
7. Click **Create GitHub App**.
8. On the app's **General** page, copy the **App ID** into your note.
9. Scroll to **Private keys** and click **Generate a private key**. Your browser downloads a `.pem` file. Keep it somewhere outside the
   repository (it is a secret); you upload it in Part 4 and then delete it.
10. In the repository, open `src/GitHubReviewApp/appsettings.json`, set `"GitHubAppId"` to the new App ID, and commit that change before
    Part 5 (through a pull request, as for any change).

### 3.2 Use the existing app

1. Open <https://github.com/organizations/MoneyMeTeam/settings/apps> and click **Edit** next to the app whose **App ID** is `3384929`.
2. On the **General** page:
   - **Webhook URL:** `https://<default domain>/api/webhook/github`, using the default domain from step 2.2.10.
   - **Webhook secret:** paste the secret you just generated (this replaces the old one).
   - Click **Save changes**.
3. Under **Private keys**, click **Generate a private key**. Your browser downloads a `.pem` file. Keep it outside the repository; you upload
   it in Part 4 and then delete it. Once the new deployment works, delete any older private keys listed here that nothing uses any more.
4. Open **Permissions & events** and check the table in 3.1 step 4 and the **Pull request** event in step 5. If you change a permission,
   click **Save changes**. Each organisation that installed the app then has to accept the new permission (GitHub emails the owners; they
   accept it under **Settings** → **GitHub Apps** → the app → **Review request**).
5. Open **Advanced**. If it offers a **Make public** button, the app is private, which is what you want: do not click it. A public app could
   be installed by anyone, and every installation would spend your review budget.

### 3.3 Check the webhook can reach Azure

GitHub sends a `ping` event when a webhook is created or changed. Under the app's **Advanced** tab, **Recent Deliveries** shows it. At this
point it fails (the code is not deployed yet): that is expected. You redeliver it in Part 6.

---

## Part 4. Put the secrets in Key Vault and grant access

In Production the app reads every secret from `https://moneyme-vault.vault.azure.net` (`appsettings.Production.json`) with its managed
identity. The vault is shared, so the app's secrets carry its name as a prefix.

### 4.1 The secrets

| Secret name | Value | Required |
|---|---|---|
| `GitHubReviewAppAnthropicApiKey` | The Anthropic key from Part 1 (`sk-ant-...`) | Yes |
| `GitHubReviewAppPrivateKey` | The whole `.pem` file from Part 3, including the `-----BEGIN` and `-----END` lines | Yes |
| `GitHubReviewAppWebhookSecret` | The webhook secret from Part 3 | Yes |
| `UptraceDsn` | Your Uptrace DSN | Optional: without it the app sends no traces, metrics or logs, and warns at start. The shared vault may already hold one. |
| `AppConfigurationConnectionString` | The App Configuration store's read-only connection string | Optional: only for changing models or switching reviews off without a restart (`docs/operations.md` section 10) |

One rule for any secret in this vault, this app's or another's: a name starting with `Claude--`, `Review--` or `Runtime--` is read by this app
as one of its settings and can stop it from starting. Do not create such names.

### 4.2 Add the secrets

The private key has several lines, and the portal's value box keeps only one, so add that one with the Azure CLI. The other two can go through
the portal or the CLI.

**The private key (CLI):**

```powershell
az keyvault secret set --vault-name moneyme-vault --name GitHubReviewAppPrivateKey --file "C:\path\to\your-app.private-key.pem" --query name
```

It prints only the secret's name. Then delete the `.pem` file from your Downloads folder (empty the Recycle Bin too). If GitHub ever needs
a new one, generate a new key rather than keeping copies.

**The Anthropic key and the webhook secret (portal):**

1. Open the Key Vault `moneyme-vault` → **Objects** → **Secrets** → **+ Generate/Import**.
2. **Upload options:** Manual. **Name:** `GitHubReviewAppAnthropicApiKey`. **Secret value:** paste the key (right-click → Paste, or
   Ctrl+V; check the field is not empty). Leave the dates empty and **Enabled** on Yes. Click **Create**.
3. Repeat for `GitHubReviewAppWebhookSecret` with the webhook secret.

Or with the CLI, straight from the clipboard so nothing lands in your history:

```powershell
# Copy the Anthropic key, then:
az keyvault secret set --vault-name moneyme-vault --name GitHubReviewAppAnthropicApiKey --value (Get-Clipboard) --query name
# Copy the webhook secret, then:
az keyvault secret set --vault-name moneyme-vault --name GitHubReviewAppWebhookSecret --value (Get-Clipboard) --query name
Set-Clipboard -Value ' '
```

### 4.3 Let the Function App read the vault

The app needs to **list** and **read** secrets. How you grant that depends on the vault's permission model: open `moneyme-vault` →
**Settings** → **Access configuration** and see which is selected.

**If it says Azure role-based access control:**

1. In the vault, open **Access control (IAM)** → **+ Add** → **Add role assignment**.
2. Role: **Key Vault Secrets User**. Click **Next**.
3. Members: **Managed identity** → **+ Select members** → **Function App** → `func-github-review-app` → **Select**.
4. **Review + assign** twice.

**If it says Vault access policy:**

1. In the vault, open **Access policies** → **+ Create**.
2. **Permissions:** under **Secret permissions**, tick **Get** and **List** only. Click **Next**.
3. **Principal:** search for `func-github-review-app`, select it, click **Next**.
4. **Application (optional):** skip. Click **Next**, then **Create**.

Role and policy changes can take a few minutes to apply. Until they do, the app cannot start (it reads the vault at start).

---

## Part 5. Deploy the code

1. Open a terminal at the repository root, on `main`, up to date (`git pull`).
2. Build and test once more:

   ```powershell
   dotnet build GitHubReviewApp.slnx
   dotnet test GitHubReviewApp.slnx
   ```

   Both must succeed (one test, the live model test, is skipped unless you ask for it).
3. Make sure the Azure CLI is signed in to the right subscription:

   ```powershell
   az account show --query name
   # if it is the wrong one:
   az account set --subscription "<subscription name>"
   ```

4. Publish:

   ```powershell
   cd src/GitHubReviewApp
   func azure functionapp publish func-github-review-app
   ```

   It builds in Release, uploads, and ends by listing the three functions: `ReviewPoisonHandler` and `ReviewProcessor` (queue triggers) and
   `WebhookReceiver - [httpTrigger]` with its URL. Do not deploy with Visual Studio's publish profile or a ZIP deploy: Flex Consumption
   does not support them.

---

## Part 6. Check the deployment

Do all four checks. Each one proves something the others do not.

1. **The app started and can read the webhook secret.** Send an unsigned request:

   ```powershell
   curl.exe -i -X POST https://<default domain>/api/webhook/github
   ```

   Expect `HTTP/1.1 401` and `Invalid signature.`. A 5xx, a 404 or no answer means the app did not start: open the Function App →
   **Monitoring** → **Log stream** (or Application Insights), restart it from **Overview** → **Restart**, and read the startup lines. The
   usual causes are vault access not applied yet (4.3), a missing app setting (2.5), or a setting the app does not recognise (it names it).
2. **GitHub and the app share the same webhook secret.** In the GitHub App → **Advanced** → **Recent Deliveries**, open the `ping`
   delivery and click **Redeliver**. Expect `200` with `Ignored: not a pull_request event.`. A `401` means the secret in Key Vault differs from
   the one in the GitHub App: set both again from one freshly generated secret. Do not redeliver a `pull_request` delivery to test: it queues
   a real, paid review.
3. **The private key and the Anthropic key work.** These are only used by a real review, so they are checked in Part 7 step 5.
4. **The queue works.** Also checked by that first review: the webhook answers 202 only after putting the request on the queue.

---

## Part 7. Install the app on repositories

Start with one test repository, check the first review, then add the rest.

1. Open the GitHub App's page: <https://github.com/organizations/MoneyMeTeam/settings/apps> → the app → **Install App** in the left menu.
2. Click **Install** next to the organisation.
3. Choose **Only select repositories**, pick one test repository, and click **Install**.
4. On that repository, open a pull request into `main`, `master`, `qa` or `production` (the app ignores pull requests into any other branch).
   It must not be a draft.
5. Within a minute or two (larger pull requests take longer), check:
   - The pull request gets one comment from the app, starting with PASS, PASS WITH COMMENTS or FAIL, with the eight checklist lines.
   - The **Checks** tab shows **MoneyMe AI review**.
   - If nothing appears: the GitHub App's **Recent Deliveries** shows what the webhook answered (`202 Review queued.` means it was queued,
     a `200 Ignored: ...` says why not), and the Function App's log stream shows the review. A "Review failed" comment names no secret; the
     log does say which call failed (a 401 from GitHub means the private key or the App ID is wrong; "The AI service rejected" with HTTP 401
     or 400 usually means the Anthropic key is wrong or was pasted incompletely).
6. When you are happy with the reviews, add repositories: the organisation's **Settings** → **GitHub Apps** → the app → **Configure**. Under
   **Repository access**, either pick more under **Only select repositories** or choose **All repositories**, then **Save**.

---

## Part 8. Day-to-day operations

- **Stop all reviews at once:** Function App → **Settings** → **Environment variables** → add `Runtime__IsReviewEnabled` = `false` →
  **Apply**. The app restarts and ignores every webhook (answering `200 Ignored: reviews are switched off.`). Delete the setting to resume.
- **Deploy a new version:** repeat Part 5 from an up-to-date `main`, then the 401 check in Part 6 step 1.
- **Rotate a secret:** add a new value to the same secret name in Key Vault (**+ New Version**), then restart the Function App so it reads
  it at once (otherwise it picks it up within four hours). For the webhook secret, change it in the GitHub App in the same minute, then
  redeliver the `ping`. For the private key, generate a new key in the GitHub App first, store it, restart, and only then delete the old key
  on GitHub.
- **Change which branches or repositories get reviewed,** or the review limits: edit `src/GitHubReviewApp/appsettings.json`
  (`Review:TargetBranches`, `Review:HighRiskRepositories`, ...) and redeploy. App settings cannot override those keys.
- **Try a pull request without the app installed:** the preview tool in `tools/GitHubReviewApp.ReviewPreview` reviews any pull request and
  posts the result to Slack. See its README.
- **Watch:** `docs/operations.md` section 6 lists the metrics and when to act on them.

## Reference: everything the app reads

**Secrets (Key Vault `moneyme-vault`):** `GitHubReviewAppAnthropicApiKey`, `GitHubReviewAppPrivateKey`, `GitHubReviewAppWebhookSecret`
(required); `UptraceDsn`, `AppConfigurationConnectionString` (optional).

**Function App application settings:**

| Name | Value | Required |
|---|---|---|
| `ReviewQueueConnection__queueServiceUri` | `https://<storage account>.queue.core.windows.net` | Yes |
| `ReviewQueueConnection__credential` | `managedidentity` | Yes |
| `AzureWebJobsStorage` or `AzureWebJobsStorage__*` | Created by Azure with the Function App | Yes (leave as created) |
| `Review__AllowedAuthorAssociations__0`, `__1`, `__2` | `OWNER`, `MEMBER`, `COLLABORATOR` | Recommended |
| `Runtime__IsReviewEnabled` | `false` to stop reviews | Only when stopping |
| `Runtime__SonnetModel`, `Runtime__OpusModel` | A model id, to override the defaults (`claude-sonnet-5-5`, `claude-opus-5-5`) | No |

**In the repository (`src/GitHubReviewApp/appsettings.json` and `appsettings.Production.json`), changed by a commit and a redeploy:**
`GitHubAppId`, `KeyVaultUrl`, `Review:*` (including `TargetBranches` and `HighRiskRepositories`) and `Claude:*`. The meaning of each is in
`docs/operations.md` section 4.

**GitHub App:** permissions Checks (read and write), Pull requests (read and write), Metadata (read); event Pull request; webhook URL
`https://<default domain>/api/webhook/github`; installable only on this account.

**Azure access for the Function App's managed identity:** Storage Queue Data Contributor on the storage account; Key Vault Secrets User
(or a Get and List access policy) on `moneyme-vault`.
