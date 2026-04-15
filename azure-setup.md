# Azure Setup Guide

## What You Need to Create in Azure Portal

### 1. Resource Group

A logical container for all resources below. Create this first.

- **Name**: e.g. `rg-github-review-app`
- **Region**: choose the closest to you

---

### 2. Storage Account

Used for the Azure Storage Queue (`ai-review-queue`) and the Functions runtime storage.

- **Name**: e.g. `stgithubreviewapp` (globally unique, lowercase, no hyphens)
- **Region**: same as resource group
- **Redundancy**: Locally Redundant Storage (LRS) — sufficient for this workload
- **Plan**: Standard

---

### 3. Key Vault

Stores the three secrets the app needs at runtime.

- **Name**: e.g. `kv-github-review-app` (globally unique)
- **Region**: same as resource group
- **Plan**: Standard
- **Permission model**: Azure role-based access control (RBAC)

After creation, add these three secrets:

| Secret Name | Value |
|---|---|
| `github-webhook-secret` | The HMAC secret you configure in your GitHub App webhook settings |
| `github-app-private-key` | The PEM private key downloaded from your GitHub App |
| `anthropic-api-key` | Your Anthropic API key (`sk-ant-...`) |

---

### 4. Function App

The compute host for the two Azure Functions (`WebhookReceiver` and `ReviewProcessor`).

- **Name**: e.g. `func-github-review-app` (globally unique)
- **Region**: same as resource group
- **Runtime stack**: .NET 10 (isolated worker model)
- **OS**: Linux
- **Plan**: Flex Consumption

During creation, point it at the Storage Account created in step 2.

---

## Managed Identity & RBAC

After the Function App is created, enable its system-assigned Managed Identity and grant it access to the other resources.

### Enable Managed Identity

Function App → **Identity** → **System assigned** → toggle **On** → Save

### Grant Key Vault access

Key Vault → **Access control (IAM)** → **Add role assignment**

| Setting | Value |
|---|---|
| Role | `Key Vault Secrets User` |
| Assign access to | Managed identity |
| Member | your Function App |

### Grant Storage Queue access

Storage Account → **Access control (IAM)** → **Add role assignment** — repeat twice:

| Role | Purpose |
|---|---|
| `Storage Queue Data Contributor` | Allows enqueuing messages (WebhookReceiver) |
| `Storage Queue Data Message Processor` | Allows dequeuing and deleting messages (ReviewProcessor) |

---

## Function App Settings

Function App → **Configuration** → **Application settings** → add the following:

| Name | Value |
|---|---|
| `KeyVaultUri` | `https://<your-keyvault-name>.vault.azure.net/` |
| `GitHubAppId` | Your GitHub App's numeric ID |
| `ReviewQueueConnection__accountName` | Your storage account name (no URL, just the name) |
| `AzureWebJobsStorage__accountName` | Same storage account name |
| `UptraceDsn` | Your Uptrace DSN (leave blank if not using Uptrace) |

> `FUNCTIONS_WORKER_RUNTIME` is set automatically by Azure when you create the Function App with .NET selected.

---

## GitHub App Webhook URL

Once the Function App is deployed, set the webhook URL in your GitHub App settings to:

```
https://<your-function-app-name>.azurewebsites.net/api/webhook/github
```

GitHub App → **Settings** → **Webhook URL** → paste the URL above
