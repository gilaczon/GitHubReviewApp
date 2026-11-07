# Recorded GitHub responses

These three files are real responses from the GitHub REST API, kept so the tests exercise payloads GitHub actually
produced and not only ones written by hand. They are used by `RecordedGitHubResponseTests` and three tests in
`PipelineIntegrationTests`.

| File | Request | Recorded |
|---|---|---|
| `pull-request.json` | `GET /repos/microsoft/vscode/pulls/346019` | 2026-11-04, with `gh api` |
| `pull-request-files.json` | `GET /repos/microsoft/vscode/pulls/346019/files?per_page=100&page=1` | 2026-11-04, with `gh api` |
| `pull-request-reviews.json` | `GET /repos/microsoft/vscode/pulls/346019/reviews?per_page=100&page=1` | 2026-11-04, with `gh api` |

The pull request is public, in a repository licensed under the MIT license (microsoft/vscode). It was chosen because it holds what the
app has to cope with in real life: a dependency lockfile (`pnpm-lock.yaml`) among source files, patches with many hunks, and a review by
a bot (`copilot-pull-request-reviewer[bot]`) whose body begins with its own hidden HTML marker, next to a human approval.

## What was changed, and what was not

Every value the app reads is exactly as GitHub returned it: states, titles, commit ids, counters, file names, patches, review ids,
review states, the bot's login and the bot's review text. On 2026-11-05 the three requests were made again and compared with these
files: every value kept here is identical to the live response, except the two replacements below. What was removed or replaced is
listed here so nobody mistakes these for raw dumps.

**Removed**

- `pull-request.json`: the description (`body`), the author (`user`) and every other person (`merged_by`, `assignee`, `assignees`,
  `requested_reviewers`, `requested_teams`), `labels`, `milestone`, `auto_merge`, `active_lock_reason`, `merge_commit_sha`,
  `mergeable`, `rebaseable`, `maintainer_can_modify`, `node_id`, `_links`, every `*_url` field, and from `head` and `base` the `label`,
  `repo` and `user` objects.
- `pull-request-files.json`: `blob_url`, `raw_url` and `contents_url` of each file.
- `pull-request-reviews.json`: `_links`, `html_url` and `pull_request_url` of each review, and from each reviewer the profile and avatar
  links, `gravatar_id`, `node_id` and `user_view_type`.

**Replaced**

- The head branch name (`head.ref`) carried a person's username, so it is `feature-branch`.
- The one human reviewer is shown as `octocat` (id 583231, not a site admin) in place of a real account.

Other real fields that the app ignores were kept on purpose (`id`, `locked`, `mergeable_state`, `merged`, `merged_at`, `closed_at`,
`comments`, `commits`, `author_association`, `url` and others), so the tests also prove unknown fields are tolerated. Because the pull
request was merged when it was recorded, `merged`, `merged_at` and `closed_at` say so. The tests that need an open pull request change
only `state` (`RecordedGitHub.PullRequestAsOpen`) and leave those three as recorded; the app reads none of them.

## What these files do not prove

They show that the app reads one real pull request correctly. They do not show that the app handles every shape GitHub can return
(renamed files, binary files with no `patch`, a pull request with more than 100 files, a pending review). Those cases are covered by
the hand-written tests, which are labelled as hand-written in their own names and comments.

The recorded patches are also uniform in ways real patches are not. They use LF line endings only (no CRLF), none holds a
`\ No newline at end of file` marker, and every character in them is ASCII. Tabs and many hunks (up to 15 in one file) do occur.
CRLF, the marker and other languages' text are covered by the hand-written and generated tests, not by these files.

To record another response, run `gh api <path>`, keep the fields the app reads, and add what you removed to the lists above.
