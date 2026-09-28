# Farm.Console

`Farm.Console` is the Linux-capable Azure DevOps CLI and .NET tool package. Its installed command is `sam`. The command metadata rendered by `sam help` is the single source of truth for command names, usage forms, and descriptions; this README documents setup and behavior around that help output.

## Install and update

GitHub Packages requires a token with `read:packages`.

```bash
dotnet nuget add source https://nuget.pkg.github.com/<owner>/index.json \
  --name github-internet-facing --username <github-username> --password <PAT> --store-password-in-clear-text
dotnet tool install --global Farm.Console --add-source github-internet-facing
dotnet tool update --global Farm.Console --add-source github-internet-facing
dotnet tool uninstall --global Farm.Console
```

`dnx` is not a reliable consumer of this GitHub Packages NuGet v3 feed. Prefer `dotnet tool install --global` with the explicit source.

For a checkout, run `dotnet run --project Farm.Console -- <arguments>`.

## Configuration

Set:

- `FARM_AZURE_DEVOPS_ORGANIZATION_URL`
- `FARM_AZURE_DEVOPS_PROJECT`
- `FARM_AZURE_DEVOPS_PAT`
- `FARM_AZURE_DEVOPS_TEAM`, required by `work-items needs-attention`
- `FARM_AZURE_DEVOPS_TERMINAL_STATES`, optional comma-separated terminal states; default `Done,Closed,Removed`

Credentials are loaded from the environment and are never stored in source files. The PAT needs the least-privilege scopes below:

- User profile Read (`vso.profile`) for `whoami` and assignment to `me`.
- Identity Read (`vso.identity`) for `my-groups` and reviewer/group resolution.
- Work Items Read (`vso.work`) for read-only work-item commands.
- Work Items Read & write (`vso.work_write`) instead when using assign, unassign, comment, or update-description.
- Code Read (`vso.code`) for pull-request and thread commands.

## Commands

Run `sam`, `sam help`, or `sam help <command>`. `--help`/`-h` and `--version`/`-v` are also supported. No arguments prints the version and full command list and exits `0`.

Implemented command families include:

- `work-items <id> [<id> ...]`
- `work-items assigned-to <email-or-me>`
- `work-items needs-attention`
- `work-items assign <id> me`
- `work-items assign <id> <email-or-unique-name>`
- `work-items unassign <id>`
- `work-items comment <id> <text>`
- `work-items comment <id> --file <markdown-file>`
- `work-items update-description <id> <markdown-file>`
- `whoami`
- `my-groups`
- `pull-requests`
- `pull-requests approved-by-me`
- `pull-requests assigned-to-me`
- `pull-requests pending-review`
- `pull-requests mine`
- `pr-threads <pr-id>`

`pr-diff`, `work-item-comments`, and `work-item-relations` validate arguments but currently exit `3` with a not-yet-implemented message. Use `sam help` for exact usage and status.

## Output and examples

ID lookups render one detailed panel per work item with identity, paths, audit dates, users, wrapped description, tags, URL, attachments, and relations. Query commands render compact Spectre.Console tables. Detail output remains readable when redirected.

```bash
sam work-items 123 456
sam work-items assigned-to me
sam work-items needs-attention
sam work-items assign 123 me
sam work-items assign 123 person@example.com
sam work-items unassign 123
sam work-items comment 123 "Investigated the issue; the fix is ready for review."
sam work-items comment 123 --file .\notes\comment.md
sam work-items update-description 123 .\notes\description.md
sam whoami
sam pull-requests pending-review
```

Successful assignment mutations print a concise confirmation followed by the updated detail. The console is not exposed by the Farm web application.
