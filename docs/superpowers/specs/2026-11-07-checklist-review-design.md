# Checklist review: design

Date: 2026-11-07. Owner: Rogelio Aczon. Status: draft for the owner's approval.

The specification this builds is [2026-11-07-checklist-review-spec.md](2026-11-07-checklist-review-spec.md) (the "spec"), kept
verbatim. This document records the owner's decisions, how the existing app changes, and every place where the build departs from
or fills in the spec. Where this document is silent, the spec rules.

## 1. Intent

MoneyMe wants every pull request in the repositories where its GitHub App is installed reviewed by Claude against MoneyMe's Code
Review Checklist, with the same rules the owner's Command Center applies. The review is advisory and predictable:

- The model only rates eight checks. Code decides the result, so the same ratings always give the same verdict.
- Only Security can fail a review. A code reviewer agent has already been through the change before the PR is opened, so the other
  checks are comments.
- Each PR is reviewed twice at the same time and the stricter rating of each check is kept, because one run alone has rated the same
  issue PASS WITH COMMENTS once and FAIL the next time.
- The result is short: one PR comment that each push replaces, and a `MoneyMe AI review` check run.

Success: on every push to an open, non-draft PR, within a few minutes, one comment and one check run show the same deterministic
verdict, and a PR whose review could not run is never shown as PASS.

## 2. Owner decisions (2026-11-07)

| Question | Decision |
|---|---|
| Where the result shows | One PR comment, updated in place on each push, plus a check run named `MoneyMe AI review`. Never an approving or change-requesting review. Whether the check becomes required is the owner's later call (a ruleset setting, not code). |
| Inline comments | None. The comment's "To fix" list gives `file:line`. |
| Which repositories | Every repository the GitHub App is installed on. The installation's repository selection is the gate; no repository allow-list. The existing author-association allow-list (`Review:AllowedAuthorAssociations`) stays as it is. |
| When to review | Every push: `opened`, `synchronize`, `reopened`, `ready_for_review`. Two runs each time. |
| Check run when there is no verdict | Conclusion `neutral`, title "Review failed". Never `success`. |
| Standards version | The published Coding Standards pages, as the spec's prompt states them. The v2.0 drafts are not applied until the owner says so. |
| How to build | Rebuild the review core inside the existing app (section 4). |
| Checkpoint | The existing hardening work was committed first (`9771750`), so the rebuild is its own diff. |
| Refusal fallback | On: Anthropic's server-side refusal fallback (beta), behind a setting. |

## 3. Goals, non-goals, anti-goals

Goals
- Implement the spec's pipeline, deterministic rules, comment layout and check run exactly, with the deviations in section 6.
- Keep the existing app's hardening: signature check before anything else, bounded body, queue with retries and a poison handler,
  per-repository installation tokens, streamed structured output with refusal, truncation and rejection handling, markdown and
  secret sanitising of everything posted, settings validated at startup, OpenTelemetry.
- Log every review (repository, PR, head SHA, model, both runs' results, tokens, cost) without diff content, model text or secrets.
- Every behaviour covered by unit tests; the build and all tests pass; the convention tests (`SourceConventionTests`) stay green.

Non-goals
- No repository allow-list, no inline comments, no GitHub review state.
- No re-run button handling (`check_run` `rerequested` events). A new push, or closing and reopening the PR, retries.
- No reading of repository guidelines files (section 6.2).
- No commit messages or linked tickets in the prompt (the spec's user message does not list them).
- No change to how the app is hosted (Azure Functions, isolated worker, .NET 10, storage queues).

Anti-goals (would be failures even if everything else works)
- A verdict shown for a PR whose review did not run, or a PASS where any check (Security included) was not assessed. Per the spec,
  a check no run assessed is a `concern` "Not assessed by the reviewer.", so the best such a review can show is PASS WITH COMMENTS
  with that check marked ⚠️.
- One PR's verdict shown in another PR's comment, or a PR left without its own review because another PR shares its head commit.
- One run seeing the other's answer, or a cached answer of one run reused as the other run's answer.
- Diff content, model output text or a secret value in logs, traces or metrics.
- A comment that piles up: more than one review comment from the app per PR in normal operation.
- Any text written by the PR author, or by the model, rendered on GitHub as a live mention, link, image or HTML.

## 4. Approaches

| Approach | Trade-off | Outcome |
|---|---|---|
| A. Rebuild the review core inside the existing app | Keeps the hardened, tested webhook, queue, auth, Claude transport and sanitising layers; replaces the chunked review, severity findings, aggregator, inline-review formatter and review history. Large diff, many tests rewritten. | **Chosen** |
| B. A new app built from the spec | Clean slate, but re-creates and re-tests the layers A keeps, and leaves two apps to operate. | Dropped |
| C. Keep the chunked engine and rate the checks per chunk | Reviews very large PRs whole, but departs from the spec's single request of about 180 KB with a coverage note, multiplies calls (chunks x 2 runs), and adds a third merge dimension. | Dropped |

## 5. Design

### 5.1 Pipeline

1. **Webhook** (`WebhookReceiver`, unchanged in substance): signature (`X-Hub-Signature-256`, HMAC over the raw bytes) before
   anything else; only `pull_request` with the four actions; drafts ignored; author-association allow-list; then, new, the runtime
   on/off switch (5.11): when reviews are switched off, answer `200 Ignored: reviews are switched off.` and queue nothing. Otherwise
   queue the message and answer 202.
2. **Processor** (`ReviewProcessor`, queue trigger): unchanged failure classification (transient rethrown for the queue's retry;
   permanent and spend-limit failures reported at once; host shutdown rethrown). It checks the switch again before starting.
3. **Orchestrator** (rebuilt), in this order:
   1. Installation token; app login; read the PR (state, draft, title, body, author, base and head refs and SHAs, counts).
   2. Skip if closed or merged, draft, or the head moved on (existing reasons).
   3. Skip if this PR has already been reviewed at this commit (5.2).
   4. In-process duplicate guard (`ReviewInFlightRegistry`, existing; keyed by PR and commit).
   5. Read every changed file; build the review input (5.3); choose the model (5.4).
   6. Run the review twice in parallel (5.5).
   7. Read, merge and derive (5.6, 5.7).
   8. Fresh token; re-read the PR; if it was closed, turned into a draft, or the head moved on, post nothing and stop. Re-check
      "already reviewed" (another instance may have finished first).
   9. Create the check run, already completed with its conclusion (5.9), then upsert the PR comment (5.8). The comment is the
      review record (5.2) and is written last, so a failure between the two leads to a retry instead of a PR whose comment was never
      written. A retry may add a second, identical check run, which is harmless.
   10. Log the review record (5.10).
4. **Poison handler** (existing trigger): after the last retry, creates the "Review failed" check run and posts the "Review failed"
   comment (5.9, 5.8).

### 5.2 Reviewed once per pull request and head commit

The spec says "review once per head commit SHA". The record is kept per pull request and commit, because two open PRs can share a
head commit (one branch with PRs into two bases, or a new PR opened on an already-reviewed commit). Their diffs differ by base, and
each needs its own review and comment. For a single PR the behaviour is the same as the spec's.

A PR counts as reviewed at its head commit when this PR has a comment written by this app's bot login (nobody else can post as it)
whose trailing marker names that commit with outcome `reviewed` (5.8). This is the same rule today's app applies to its PR reviews
(`ReviewHistory`). A "Review failed" outcome never counts, so a reopen or a new push tries again, as the failure notice tells the
author. Check runs are not the record: they belong to the commit, which several PRs can share.

### 5.3 The review input

All lengths are UTF-8 bytes unless stated.

**Changed files** come from the existing paginated files API (GitHub lists up to 3,000). Each file gets a risk tier, first match wins:

1. Not readable as text: dependency lockfiles, binaries, minified or bundled files (the existing `DiffFileClassifier` lists). Listed,
   never included.
2. Generated or vendored (existing rules). Ordered after docs.
3. Tests (existing test-path rules). Ordered after the rest.
4. Docs (`.md`, `.mdx`, `.markdown`, `.txt`, `.rst`, `.adoc`, licence files, or under a `docs/` folder). Ordered after tests. A licence
   file is one named `LICENSE`, `LICENCE`, `COPYING` or `NOTICE`, or starting `LICENSE-` or `LICENCE-`, in any case and with no
   extension or any extension but a source-code or configuration one (`COPYING.LESSER`): it is documentation, though `license` is a
   personal-data keyword. A source-code or configuration file named like one (`src/Models/License.cs`, `license.json`) is the
   application's own, classed as it would be anywhere else.
5. Otherwise the first of these that matches, in this order: **auth**, **payments**, **cards**, **bank login**, **personal data**,
   **pipelines and workflows**, **config**, else **the rest**.

High-risk matching (auth to personal data) splits the path into lower-cased word tokens: at every character that is not a letter or
digit (such as `/ \ . _ -` and spaces), at camelCase and acronym boundaries, and where a letter and a digit meet, so a run of digits
is a token of its own (`OAuth2Client` gives `o`, `auth`, `2`, `client`). An acronym keeps a plural `s` that ends the word or is
followed by a capital or a digit (`JWTsValidator` gives `jwts`, `validator`). A token matches a keyword when it is the keyword, or the
keyword with one final `s` added (`jwts` matches `jwt`, `logins` matches `login`). A starting set, tunable in code with tests:

| Tier | Keywords (tokens) |
|---|---|
| auth | auth, authentication, authorization, authorize, authn, authz, login, logon, logout, signin, signup, oauth, oidc, openid, jwt, token, tokens, identity, permission, permissions, rbac, sso, saml, password, passwords, credential, credentials, session, sessions, mfa, otp, totp, security, entra, msal, claims, apikey, secret, secrets, keyvault, topsecret |
| payments | payment, payments, pay, payid, payto, payout, payouts, payee, payer, stripe, refund, refunds, charge, charges, billing, invoice, invoices, ledger, transaction, transactions, disbursement, disbursements, disburse, repayment, repayments, directdebit, debit, bpay, npp, settlement, settlements, remittance, fee, fees, interest, loan, loans, collection, collections |
| cards | card, cards, creditcard, debitcard, pan, cvv, cvc, cardholder, tokenisation, tokenization, threeds |
| bank login | bank, banks, banking, openbanking, bankstatement, bankstatements, basiq, illion, yodlee, proviso, creditsense, cdr, consent, consents |
| personal data | customer, customers, pii, personal, person, profile, profiles, kyc, aml, address, addresses, dob, birth, birthdate, phone, mobile, email, emails, contact, contacts, applicant, applicants, borrower, borrowers, licence, license, passport, medicare, document, documents, verification, privacy, gdpr, tfn, employment, income, payslip, payslips, member, members |

Pipelines and workflows: `.github/workflows/*`, `azure-pipelines*`, `Dockerfile*` and `*.dockerfile`, `.gitlab-ci.yml`, `Jenkinsfile`,
infrastructure as code anywhere (`*.bicep`, `*.bicepparam`, `*.tf`, `*.tfvars`), and the files kept in a folder named `pipelines`,
`.azuredevops`, `infra`, `deploy`, `deployment`, `helm`, `charts`, `k8s` or `kubernetes` (at any depth, any case), such as ARM
templates, Helm charts and Kubernetes manifests. Application code in such a folder is not a pipeline file because of the folder: a file
with a source-code extension there (`src/components/charts/LineChart.vue`, `src/Api/Infra/AppDbContext.cs`) is classed as it would be
anywhere else, unless it is a script or infrastructure code (`.sh`, `.bash`, `.ps1`, `.psm1`, `.psd1`, `.bat`, `.cmd`, `.bicep`,
`.tf`). Everything else in those folders (YAML, JSON, Helm `.tpl`, files with no extension) stays a pipeline file. Config:
`appsettings*.json`, `*.config`, `host.json`, `.env*`, `*.json`,
`*.yml`, `*.yaml`, `*.xml`, `*.toml`, `*.ini`, `*.properties`, project and build files (`*.csproj`, `*.props`, `*.targets`, `*.sln`,
`*.slnx`, `package.json`, `global.json`, `nuget.config`).

**Order** is by tier (auth first), then GitHub's order. Removed files take part like any other: deleting an auth filter matters.

**Diff budget.** Walk the ordered files and include each file's rendered diff while it fits in `Review:MaxDiffBytes` (180,000).
Rendering reuses `DiffRenderer` (every added or context line prefixed with its line number in the new file). A file whose rendered
diff is over `Review:MaxFileBytes` (60,000) is cut at a line boundary and marked partial; a cut slice under 2,000 bytes is left out
instead and listed as "a line too long to show": a line near the top of the file is longer than what is left of the file limit, as in
a bundled script or a one-line JSON file. That is checked before the budget, so it is the reason given even when the budget has run
out too. A file that does not fit what is left is left out and the walk continues, since a later, smaller file may still fit. Files
with no patch from GitHub (rename only, binary, too large to diff) are listed as "no diff from GitHub".

**User message**, in the spec's order. Every author-controlled string (title, description, branch names, author login, file paths,
diff lines) passes through `PromptSanitizer.NeutralizeTags` so it cannot close or open the tags around it.

```
<pr_metadata>
repository: {owner}/{repo}
title: {title}
author: {login}
base_branch: {base ref}
head_branch: {head ref}
additions: {n}
deletions: {n}
head_sha: {40-hex}
</pr_metadata>

<pr_description_untrusted>
{description, cut to Review:MaxDescriptionChars (10,000 characters) with "[description cut]"; "(no description)" when empty}
</pr_description_untrusted>

<changed_files>
{n} files changed, highest risk first. {a} diffs included in full, {b} partly, {c} left out.
{only when the PR changed more files than GitHub listed: GitHub listed only {listed} of the {total} changed files.}
- {path} ({status}, +{add} -{del}, {tier}): {included | partial, cut at 60 KB | left out: over the 180 KB limit | left out: a line too long to show | left out: lockfile, not read | left out: binary or minified, not read | left out: no diff from GitHub}
...
</changed_files>

<diff>
{the included file elements, same order}
</diff>
```

The changed-files list is capped at `Review:MaxListedFiles` (1,000) lines, ending with "and {n} more files not listed". A count of one
is singular: "1 file changed", "1 diff included in full", "and 1 more file not listed"; every other number keeps the plural. GitHub
lists at most 3,000 of a PR's files, so when the PR's changed-file count is larger than the number of files listed, the line after the
counts line says "GitHub listed only {listed} of the {total} changed files.", both numbers written without a thousands separator.

### 5.4 Model choice

Opus (`Runtime:OpusModel`) when both hold, else Sonnet (`Runtime:SonnetModel`):
- the change is over `Review:OpusLineThreshold` (40) lines: the PR's additions plus deletions;
- it touches a high-risk area: any changed file outside the tests, docs, generated and not-readable tiers is in the auth, payments,
  cards, bank login, personal data or pipelines tier; or the repository name matches the same keyword sets; or the repository is
  listed in `Review:HighRiskRepositories` (seeded with `MoneyMeTeam/Moneyme.Global.Library.VueApp`, which holds the bank-login and
  Stripe card flows but whose name says neither).

Both runs of one review use the same model and identical requests.

### 5.5 The two runs

- Two independent `ClaudeService` calls started together. Neither sees the other. Each waits for its own slot in the shared
  `ModelRequestLimiter`; `Review:MaxConcurrentModelRequests` becomes 12 (two per review, six reviews per instance).
- Request: streamed; system prompt (5.6.1) marked cacheable; the user message (5.3); structured output with the schema (5.6.2);
  `output_config.effort` = `Claude:Effort` (`high`; Opus 5.5 defaults to `medium`, so it is always sent); `max_tokens` 32,000; no
  tools; adaptive thinking by omission (thinking cannot be turned off on these models). One time budget per call
  (`Claude:RequestTimeoutSeconds`, 600) and the SDK's retries (`Claude:MaxRetries`, 1), as today.
- **Refusal fallback** (`Claude:IsRefusalFallbackEnabled`, true): the call moves to the beta Messages endpoint with
  `betas: ["server-side-fallback-2026-07-01"]` and `fallbacks: "default"`. A classifier refusal is then answered by the model
  Anthropic picks for that refusal category in the same call. The answer's text is all text blocks in order (a mid-stream fallback
  continues the partial text), and the `fallback` block is ignored. The model reported is the one that produced the answer
  (`message.model`); cost sums every attempt (`usage.iterations`). A final `stop_reason: "refusal"` means the whole chain refused.
  With the setting off, the call uses today's non-beta endpoint.
- **Run outcomes**: `Completed` (a readable answer), `Refused`, `OutputTruncated` (output limit or context window), `InvalidOutput`
  (not valid JSON of the right shape), `Rejected` (400, 413, 422: the request itself refused), or a thrown failure, which is
  `transient` or `permanent` by the existing `FailureClassifier`.
- **Result cache**: the existing in-memory cache (`ChunkResultCache`) keeps finished runs for a retry on the same instance. Its key
  must include the run index (0 or 1) as well as both prompts, or the second run would reuse the first's answer.

### 5.6 Prompt, schema and reading one run

#### 5.6.1 System prompt

The text between the spec's `<reviewer_system_prompt>` tags, kept as an embedded resource file so it can be diffed against the
Command Center's copy, with one addition placed after "How to work": a short section "How the PR is given to you" that names the four
tags, says the description tag holds untrusted text, and explains the diff line format (line number in the new file, then `|`, then
`+`, `-` or a space; removed lines have no number; cite the number shown). Nothing else is changed.

#### 5.6.2 Schema

The spec's schema with the keywords the API refuses taken out: `maxLength`, `minItems` and `maxItems` (structured outputs reject
numeric, length and complex array constraints with a 400, which would fail every review). Each limit moves into the field's
`description` ("At most 100 characters." / "Exactly eight entries, one per check id." / "At most eight entries."), and the code
enforces it while reading (below). Everything else (ids, enums, required fields, `additionalProperties: false`) is as the spec has it.

#### 5.6.3 Reading one run (the spec's rules, made exact)

For each run with outcome `Completed`:
- Entries whose `id` is not one of the eight are dropped; the first entry for an id wins; the rest are dropped.
- A `status` that is not exactly one of the four counts as `concern` (never `pass`).
- A `fail` on any check other than `security` becomes `concern`, keeping its note.
- A note is kept only for `concern` and `fail`: line breaks become spaces, whitespace is collapsed, cut to 100 characters (with an
  ellipsis when cut). A `concern` with no note gets "Not assessed by the reviewer."
- `serious` is true only when the final status is `concern` and the JSON value is the boolean `true`.
- `summary` cut to 240 characters, `coverage_note` to 160, findings to at most 8 entries; in a finding `file` 250, `issue` 160,
  `fix` 120 characters. A finding whose `check` is not one of the eight is dropped. A finding `line` below 1 reads as 0, which the
  comment shows as no line.
- A JSON property whose name cannot be read as text (the escape of half a surrogate pair) is ignored as if it were absent, and the
  object's other properties are still read. Reading never throws.
- A check the run left out is "missing" for that run (see merging).
- An answer in which none of the eight checks is rated (an empty or entirely invalid `checks` list) is not a completed run: its
  outcome is `InvalidOutput`. The spec's schema required exactly eight entries (`minItems`/`maxItems` 8); the API cannot enforce that,
  so this rule keeps a run that assessed nothing from producing a verdict.

A run's **own result** (used to pick the stricter run) treats its missing checks as `concern` "Not assessed by the reviewer." and
applies the derivation in 5.7.

### 5.7 Merging and deriving

**Merging** (two completed runs, A started first):
- For each check, if both runs rated it, the stricter status wins (`na` < `pass` < `concern` < `fail`). Among the runs with the
  winning status, the note is the first serious run's real note, else the first real note (A before B), else "Not assessed by the
  reviewer." when a run holds only that, else empty. A real note is one that is not empty and is not "Not assessed by the
  reviewer.", which stands for "no note". `serious` is true if any run with the winning status flagged it.
- A check only one run rated takes that run's rating. A check neither run rated is `concern` "Not assessed by the reviewer.".
- With one completed run (5.12), its missing checks become `concern` "Not assessed by the reviewer.".
- Findings: both runs' findings, stricter run first, each run's own order kept. Each finding's file is first matched to the PR's
  changed files, so a diff prefix such as `b/` is dropped; a path that matches no changed file is kept as written. A finding
  repeating the same check, file and line (file compared case-insensitively, `\` and `/` and a leading `./` normalised) counts once.
- Summary and coverage note come from the stricter run: the one whose own result is worst (FAIL, then PASS WITH COMMENTS, then
  PASS); if tied, A.

**Deriving**: exactly the spec. FAIL when Security is `fail`; PASS WITH COMMENTS when nothing is `fail` and at least one check is
`concern`; PASS when every check is `pass` or `na`. Serious count = checks that are `concern` with `serious` true; when above zero the
headline adds ` ({n} serious)`. Meaning texts are the spec's; "no security problem, but read the serious comment before approving"
replaces the PASS WITH COMMENTS meaning when the serious count is above zero. FAIL's meaning is always "changes required".
Deriving takes exactly the eight checks, each once, after missing ones are filled in, and refuses any other list, so a check that was
not assessed can never yield PASS.

**Agreement line**: "Reviewed twice; both reviews agreed." when every check has the same status in both runs. Otherwise "Reviewed
twice; the stricter rating of each check is shown." and, when the two runs' own results differ, " They differed: {stricter result}
and {other result}." With one run: "Reviewed once: the other review did not finish."

**Findings shown**: only findings whose check ended `fail` or `concern`; first those of checks that are `fail`, then those of checks
that are serious, then the rest, keeping the merged order within each group; at most six.

### 5.8 The comment

Layout exactly as the spec, in this order: headline line, summary, agreement line, checklist (heading, then one line per check in the
spec's order), the counts line ("1 pass, 4 concerns, 1 fail, 2 n/a"), "To fix, worst first" with the shown findings (omitted when
there are none), the coverage line, the footer, then the hidden marker.

- Icons: pass ✅, concern ⚠️, fail ❌, na ➖. A `pass` line has no note text after the label; a `na` with no note says "not
  applicable"; a serious note starts with "SERIOUS: ".
- Finding line: `- {icon of its check} {Check label} `{file}:{line}`: {issue} Fix: {fix}`; a line of 0 or less shows the file only.
  A file the model named is matched to a changed file when possible (the existing `PathResolver`), else shown as given.
- Coverage line: shown only when some diff was left out or cut. It uses the stricter run's coverage note; if that is empty, the code
  writes one: "{n} of {total} changed files were not reviewed in full."
- Footer: `{owner}/{repo} #{number} · {head} → {base} · by {author} · {files} files +{added} -{deleted} ({included} reviewed) ·
  {model} at {sha first 10}`, where `included` counts files whose diff was sent in full or in part, and `model` is the answering
  model, or both joined with " + " when the runs differed (a fallback).
- Marker, the last line: `<!-- moneyme-ai-review sha={40-hex} outcome={reviewed|failed} -->`.
- Every model-written and author-written string is cut to its limit, passed through `SecretRedactor.Redact`, then rendered with
  the existing `MarkdownSanitizer` (inline text through `NeutralizeInline`, paths and refs through `InlineCode`), so no mention, link,
  image, HTML or math becomes live. Fixed text the app writes is not sanitised.

**Upsert**: list the PR's issue comments (paginated, capped), take the oldest one written by this app's bot login whose body ends
with a `moneyme-ai-review` marker, and replace its body; create one when there is none. Comments by anyone else are never edited,
whatever they contain.

**"Review failed" comment** (same upsert and marker with `outcome=failed`): "Review failed - no verdict", a one-line reason that
quotes no model output (5.12), "Push a new commit, or close and re-open this pull request, to try again.", and the footer without a
model.

### 5.9 The check run

- Name `MoneyMe AI review`, on the head SHA. Created once per finished review, already `completed`, in one request: FAIL →
  `failure`; PASS WITH COMMENTS → `neutral`; PASS → `success`; Review failed → `neutral`. There is no `in_progress` state and
  nothing is created for a review that is abandoned (PR closed, turned draft, or head moved on), so no unfinished or `skipped` run is
  ever left behind. `external_id` holds the PR number, for tracing.
- Output: title = the headline (for example "PASS WITH COMMENTS (1 serious)", "Review failed"); summary = the comment body without
  the marker.
- GitHub shows, and a required-check rule uses, the most recent check run of a name on a commit. When two open PRs share a head
  commit, the check therefore shows whichever PR's review finished last, while each PR's comment shows its own. This is rare and
  accepted; putting the base branch in the check name would fix it but would make the required-check name vary by branch.
- If the check becomes required, a PR cannot merge until its review has reported (GitHub waits for a missing required check).
- Created only when the installation granted Checks: write (5.13); otherwise everything else still happens.

### 5.10 Review record, cost and telemetry

One structured log line per finished review (Information): repository, PR number, head SHA, model chosen, run count, for each run
its outcome, answering model, own result, the eight statuses as a compact string, tokens (input, output, cache read, cache write)
and cost; the merged result, serious count, whether the runs agreed, the number of files included, partial and left out, the
total cost and the duration. No diff text, no notes, findings or summary, no titles or descriptions, no secrets.

Cost per attempt = (input x input price + output x output price + cache write x cache-write price + cache read x cache-read price) /
1,000,000, from the static price table `Claude:Pricing` (USD per million tokens):

| Model | Input | Output | Cache write (5 min) | Cache read |
|---|---|---|---|---|
| claude-sonnet-5-5 | 2.00 | 10.00 | 2.50 | 0.20 |
| claude-opus-5-5 | 4.00 | 20.00 | 5.00 | 0.20 |
| claude-sonnet-5 | 2.00 | 10.00 | 2.50 | 0.20 |
| claude-opus-5 | 5.00 | 25.00 | 6.25 | 0.50 |
| claude-opus-4-8 | 5.00 | 25.00 | 6.25 | 0.50 |

A model not in the table gives "cost unknown" and a warning, never a guess. The last three rows cover fallback models.

Metrics (OpenTelemetry, names prefixed `github_review_app.` as today): reviews by `result` (`pass`, `pass_with_comments`, `fail`,
`review_failed`) and `runs` (1 or 2); runs by `outcome` and `model`; disagreements between runs; check statuses by `check` and
`status`; serious flags; model choice (`sonnet`, `opus`) and why; cost in USD; the existing token counters, skip reasons, failure
kinds, durations and failure-notice counters. Existing chunk, inline-comment and finding-severity metrics go.

### 5.11 Settings

Static, in `appsettings.json` (validated at startup, unknown keys refused, as today):
- `Claude`: `Effort` (high), `MaxOutputTokens` (32,000), `RequestTimeoutSeconds` (600), `MaxRetries` (1), `IsRefusalFallbackEnabled`
  (true), `Pricing` (5.10). `Model` is removed.
- `Review`: `MaxDiffBytes` (180,000), `MaxFileBytes` (60,000), `MaxListedFiles` (1,000), `MaxDescriptionChars` (10,000),
  `OpusLineThreshold` (40), `HighRiskRepositories`, `MaxConcurrentModelRequests` (12), `MaxReviewDurationSeconds` (1,200),
  `AllowedAuthorAssociations` (unchanged). Removed: `MaxChunkBytes`, `MaxTotalBytes`, `MaxFiles`, `MaxParallelChunks`,
  `MaxInlineComments`, `MaxFindings`, `LargePullRequestFileThreshold`, `LargePullRequestLineThreshold`, `GuidelinePaths`,
  `MaxGuidelinesChars`.

Runtime, the house rule's "settings that must change at runtime", in a `Runtime` section that is **not** bound by the strict startup
binder: `IsReviewEnabled` (true), `SonnetModel` (`claude-sonnet-5-5`), `OpusModel` (`claude-opus-5-5`). Defaults live in
`appsettings.json`. Azure App Configuration overrides them when the `AppConfigurationConnectionString` secret is present (from Key
Vault through TopSecret, per `/dev-appconfig-integrate`):
- Added in `Program.cs` after `AddSecretsToConfiguration`, on the same `ConfigurationManager` (the skill's non-web bootstrap), with
  `optional: true`, so an unreachable store at startup logs a warning and the appsettings values apply.
- Selects only keys `GitHubReviewApp:Runtime:*` (no label), trimming `GitHubReviewApp:`, so the store can never reach the static
  settings. Refresh on the sentinel `GitHubReviewApp:Sentinel` (refresh all), every 120 seconds in Production and 30 otherwise,
  driven by the Functions worker middleware (`Microsoft.Azure.AppConfiguration.Functions.Worker`, `UseAzureAppConfiguration()`).
- Read through a purpose-built holder, never `IOptionsMonitor` with validation: on each configuration change it parses and validates
  the three values (model ids must look like `claude-[a-z0-9.-]+`, at most 100 characters; the switch must be a boolean) and
  publishes an immutable snapshot only when all are valid; otherwise it keeps the last good snapshot and logs a warning. Its callback
  never throws.
- Without the secret, App Configuration is not added and the app logs that the appsettings values are in use.

Secrets (Key Vault through TopSecret, unchanged): `AnthropicApiKey`, `GitHubAppPrivateKey`, `GitHubWebhookSecret`, and now the
optional `AppConfigurationConnectionString` (the store's read-only connection string). No secret name may start with `Claude--`,
`Review--` or `Runtime--` (they would be read as settings).

### 5.12 Failure handling

| What happened | Result |
|---|---|
| Both runs `Completed` | Verdict, "Reviewed twice ..." |
| One run `Completed`, the other ended any other way | Verdict from the one run, "Reviewed once: the other review did not finish." |
| No run completed, and at least one failed transiently | Rethrown: the queue retries (three attempts, five minutes apart). After the last, the poison handler posts "Review failed" ("The AI service or GitHub could not be reached; three attempts failed."). |
| No run completed, a permanent failure (bad key or model name, spend limit) | Existing path: reported at once as "Review failed" with the existing reasons. |
| No run completed; both ended `Refused`, `OutputTruncated`, `InvalidOutput` or `Rejected` | "Review failed" at once, not retried, with a reason naming what happened ("Both reviews were declined by the AI service's safety checks.", "Both reviews ran out of output space.", "Neither review returned a readable answer.", "The AI service refused the review request.", or "Neither review finished." when they differ). |

"Review failed" means a neutral "Review failed" check run (5.9) and the "Review failed" comment (5.8), in that order. It is not
posted when the PR has since closed, become a draft or moved to a newer commit, or when the PR already has a `reviewed` record for
the commit (5.2), as today. A run that rated none of the checks counts as `InvalidOutput` here (5.6.3).

### 5.13 GitHub App permissions and token scope

- App permissions needed: Pull requests: Read and write (read the PR and files; issue comments on a PR), Checks: Read and write (new),
  Metadata: Read. Contents: Read is no longer used; the owner may keep or drop it.
- Installation tokens are requested for the one repository with `pull_requests: write` and `checks: write`. When GitHub answers 422
  because the installation has not granted Checks, the token is requested again without it, cached as "no checks access", and the
  review runs without a check run; the comment and the review record (5.2) are unaffected. The existing contents-permission
  fallback goes.
- The HTTP surface stays one Azure Functions HTTP trigger authenticated by the webhook signature. The house rule's MVC-controllers
  requirement concerns ASP.NET Core APIs protected by `MoneyMe.Core.Security.Authorization`; it does not apply here.

### 5.14 What goes from today's app

The chunk planner (`ReviewPlanner` chunking, `ReviewChunk`, `ReviewPlan`'s chunks), the severity schema, `ReviewResponseParser`,
`ReviewAggregator`, the inline-review `ReviewFormatter`, `ReviewSubmission`, `InlineReviewComment`, `DiffLineIndex` (inline anchoring),
the PR-review based `ReviewHistory` and `ReviewMarker` (rewritten for the comment record of 5.2 and the marker of 5.8, whatever
they end up called), the review-posting, review-listing, pending-review deletion and guidelines
methods of `IGitHubService`, `RepositoryGuidelines`, `ReviewFinding`/`ReviewSeverity`, the chunk-related settings and metrics, and the
tests that cover only these. Kept and reused: `WebhookReceiver`, `ReviewProcessor`, `ReviewPoisonHandler`, `ReviewQueue`,
`GitHubAppAuthService` (permissions changed), `GitHubService` (methods added), `ClaudeService` (schema, parsing and endpoint changed),
`MockClaudeService` (returns a checklist), `FailureClassifier`, `GitHubApiException`, `ModelRequestLimiter`, `ReviewInFlightRegistry`,
`ChunkResultCache` (re-keyed), `PatchParser`, `DiffRenderer`, `DiffFileClassifier` (extended to the tiers), `PathResolver`,
`PromptSanitizer`, `SecretRedactor`, `MarkdownSanitizer`, telemetry and startup diagnostics.

### 5.15 Testing

- Unit tests for every new rule, named `MethodName_GivenContext_Should_ExpectedOutcome`, AAA comments, FluentAssertions and Moq, as
  the repository's CLAUDE.md requires; the convention tests enforce the rules on the new code.
- The deterministic core (reading, merging, deriving, findings selection, layout) gets table-driven tests that cover each spec rule
  and each case in 5.7, including the anti-goals: a check no completed run rated (Security included, and in the one-run case) is a
  `concern` "Not assessed by the reviewer." and never gives PASS; an answer that rates no check is `InvalidOutput`, not a completed
  run; a non-security `fail` never gives FAIL; `serious` without a real boolean `true` is false; the two runs never share a cached
  answer; the review record is per PR, so a second PR on the same head commit is reviewed and gets its own comment.
- Each assertion that is the only cover for an anti-goal is proven by mutation: break the production line, see the test fail.
- The Claude transport is tested against the existing fake HTTP backend, including a beta fallback block, a mid-stream fallback,
  `usage.iterations`, a refusal of the whole chain, and the schema sent (no refused keywords).
- GitHub calls (check runs, comments, the token scope fallback) are tested against recorded or handler-backed responses.
- The live smoke test (skipped without a key) is updated to the new schema.

### 5.16 Rollout (owner actions)

1. GitHub App settings: add Checks: Read and write; each installation owner approves the new permission.
2. Optional, for runtime switching: create the App Configuration keys `GitHubReviewApp:Runtime:IsReviewEnabled`,
   `GitHubReviewApp:Runtime:SonnetModel`, `GitHubReviewApp:Runtime:OpusModel` and `GitHubReviewApp:Sentinel`, and put the store's
   read-only connection string in Key Vault as `AppConfigurationConnectionString`.
3. Deploy, run the post-deployment check in `docs/operations.md`, and open a test PR on the test repository.
4. Later, the owner's call: make `MoneyMe AI review` a required check in a ruleset.

`docs/operations.md` is updated with the new settings, limits (two model slots per review), metrics, App Configuration keys,
permissions and the check run.

## 6. Where the build departs from or fills in the spec

1. **Schema keywords** (5.6.2): `maxLength`, `minItems`, `maxItems` removed because the API rejects them; the limits are in the
   descriptions and enforced in code.
2. **No repository guidelines** in the prompt: today's app appends `.github/ai-review.md` or `CLAUDE.md` from the base branch; the
   checklist is now the whole standard, and agent-oriented guideline files would change how checks are rated.
3. **System prompt addition**: the "How the PR is given to you" section (5.6.1). Otherwise verbatim.
4. **Missing checks in a merge** (5.7): the spec's two rules ("left out counts as concern" and "a check only one run rated takes
   that run's rating") are reconciled by applying "not assessed" only when no completed run rated the check.
5. **Changed-files list cap**, **description cap** and **per-file 60 KB cut** (5.3): the spec lists every file and does not mention
   partial files; very large PRs would otherwise spend the budget on the list or drop a big high-risk file entirely.
6. **Coverage line fallback** (5.8): written by the code when the model's coverage note is empty but diff was left out.
7. **Reviewed once per PR and commit** (5.2): the spec says per commit; the record is kept per PR so that two PRs sharing a head
   commit each get their own review and comment. Identical to the spec for a single PR.
8. **Check run written once, at the end** (5.9): created already completed, nothing for an abandoned review, PR number in
   `external_id`. The spec only names the conclusions.
9. **An answer that rates no check is not a completed run** (5.6.3): the spec's schema guaranteed eight entries, which the API cannot.
10. **Retries before "Review failed"** (5.12): when a run failed for a reason that can clear up, the queue retries first.

## 7. Calls I made (please check these while reading)

- The risk keyword lists, the config and pipelines patterns, and that test, docs and generated files never trigger Opus (5.3, 5.4).
- `Review:HighRiskRepositories` seeded with `MoneyMeTeam/Moneyme.Global.Library.VueApp` (5.4).
- Removed files reviewed in risk order like other files (5.3).
- Limits: 60 KB per file, 1,000 listed files, 10,000-character description (5.3).
- Effort `high` for both models; 32,000 output tokens (5.5).
- Agreement line wording when only some checks differ (5.7).
- The "Review failed" reason texts (5.12).
- App Configuration key prefix `GitHubReviewApp:`, no label, sentinel `GitHubReviewApp:Sentinel`, refresh 120 s in Production (5.11).
- Contents permission no longer requested in tokens (5.13).
- Reviewed once per PR and commit, with the PR's own comment as the record; on a commit shared by two open PRs, the check shows the
  review that finished last (5.2, 5.9).
- The check run is created once, completed, before the comment; the comment is written last as the record (5.1, 5.9).
- A check no run assessed, Security included, is a `concern` "Not assessed by the reviewer.", as the spec says, so such a review
  shows PASS WITH COMMENTS with Security marked ⚠️, not "Review failed". An answer that rates no check at all is a failed run (5.6.3).

## 8. Left to the builder

Class and file names and folders within the existing layout; the exact metric names and tag values within 5.10; the exact
pagination caps for comments and check runs; how the system prompt resource is loaded; the test organisation; and whether
`ChunkResultCache` and the chunk-era names are renamed (for example to a run-result cache), as long as the key includes the run index.
