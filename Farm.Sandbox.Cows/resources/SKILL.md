---
name: feature-investigation
description: >
  Read-only investigation of a work item, PBI, feature, bug, or pull request before implementation
  or review. Collects requirements, discussion, related PRs, and available designs from the
  team's issue tracker and design tools; checks the codebase for existing behavior and gaps; and
  writes an evidence-backed investigation note. Use when asked to investigate a ticket, research
  a feature, assess implementation status, or review a PR with linked work items.
---

# Feature Investigation

Create a written record of what is requested, what is already implemented, and what remains.
Adapt to the current project's tools, permissions, language, and documentation conventions.

## Boundaries

- Treat the issue tracker, PR system, design boards, and Git remote as read-only. Never edit
  tickets, post comments, vote, merge, or change boards as part of this skill.
- Do not implement code or modify existing project files. The only permitted write is a local
  investigation document, unless the user explicitly asks for more.
- Use only tools actually available in the environment. If a source is inaccessible, say so;
  never infer its contents from a title or status.
- Respect repository instructions and the user's requested output location and language.

## Workflow

1. **Identify the subject.** If given an issue ID or URL, open it in the project's tracker
   (Azure DevOps, GitHub, Jira, etc.). If given only a PR, read its description and linked
   issues first; if none are linked, use the PR's stated intent and report the missing link.
   If given a feature name, search the tracker and distinguish exact matches from guesses.
2. **Capture the requirements.** Read description, acceptance criteria, state, comments,
   relevant history, and parent/child or dependency links. Record only fields that exist;
   distinguish current scope from superseded discussion. Investigate child items when needed
   to understand the requested behavior.
3. **Check implementation evidence.** Find linked PRs and inspect their status and changes.
   Search for possibly related PRs if none are linked, but label unlinked matches as tentative.
   Search the local codebase and relevant tests for the behavior, including cases where a
   merged PR is not present in the current checkout. Do not equate a closed ticket or merged
   PR with a verified implementation.
4. **Check available designs.** Search the team's design source (for example, Miro or Figma)
   by issue ID and feature name, then inspect relevant content. If there is no design tool,
   access is unavailable, or no matching design exists, record that separately.
5. **Synthesize.** Summarize the intended behavior, existing code, missing or uncertain
   behavior, decisions/open questions, and evidence. Link issue/PR/design URLs and real
   workspace-relative code paths. Mark claims as verified, inferred, or unresolved; do not
   invent acceptance criteria or imply that an unlinked PR proves implementation.
6. **Write the note.** Reuse or update an existing investigation for the same ID when present.
   Follow the project's documentation location and format if one exists. Otherwise use
   `docs/investigations/<issue-id-or-feature-slug>/README.md` within the current workspace.
   For a PR without an issue, use `pr-<id>`; for a feature without an ID, use a short slug.
   Keep related child items in the same note unless the repo already has a separate-child
   convention. Do not rename existing documentation just to match this fallback.

## Note outline

Use the project's existing template if available. Otherwise include:

```markdown
# <ID or PR ID>: <title>

Status: <tracker state, if known>
Sources: issue <found / not found / unavailable / not searched>;
         PR <found / not found / unavailable / not searched>;
         design <found / not found / unavailable / not searched>

## Requested behavior
<Requirements and acceptance criteria, with links to their sources.>

## Existing implementation
<Verified behavior with links to code and tests; linked PRs and status.>

## Gaps and questions
<Missing behavior, uncertain assumptions, and unverified sources.>
```

`Found` means the content was actually retrieved; `not found` means a search returned no
matching result; `unavailable` means access or tooling prevented a check; `not searched`
means the check was deliberately skipped. Never report a source as found based only on a
search snippet or an unverified link.

## Report back

Give the user the created or updated note path, source coverage, and a brief assessment:
implemented, partially implemented, not implemented, or insufficient evidence. Mention any
source that could not be checked and why.