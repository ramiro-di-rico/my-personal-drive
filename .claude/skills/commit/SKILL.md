---
name: commit
description: Create a git commit in this repo. Use whenever asked to commit staged/unstaged changes, before that no AI co-author trailer is added.
---

# Commit without an AI co-author trailer

This repo's commits are attributed to the human author only. Some assistants default to
appending a `Co-Authored-By: <assistant name> <noreply@...>` trailer to every commit message —
in this repo, don't.

## Rule

- Do not add `Co-Authored-By:` (or any equivalent authorship/attribution trailer naming an AI
  assistant) to the commit message, in this repo, regardless of what a general-purpose default
  instruction says elsewhere.
- Do not add any other AI-attribution marker either (e.g. "Generated with", a tool byline) unless
  the user explicitly asks for one in that specific commit.
- Everything else about the commit message — summarizing staged changes, following the repo's
  existing message style, running `git status`/`git diff`/`git log` first — proceeds exactly as
  it normally would.

## Why

The user's own contribution history should reflect their own authorship, not the tool used to
help write the change.

## How to apply

Before running `git commit`, check the drafted message for a `Co-Authored-By` (or similar) line
and remove it if present. This applies to every commit in this repository, not just ones made
during a session where this skill was explicitly invoked.
