---
status: draft
revision: 1
requirement: "Replace with one line: what this plan is for"
verify: "dotnet test"
final: "dotnet build -c Release"
---
# Replace with the plan's title

## Goal

One or two sentences: what is true when this plan is done, and for whom.

## Context

- Why the change is wanted and what prompted it.
- What already exists that the work builds on: the files, functions or tools to reuse.
- Decisions already made, and the choices the work must not reopen.
- The header's `verify:` runs after each step and `final:` once at the end. Replace them with commands this project already has (a test run, a build, a linter), or delete a line you have no command for.

## Steps

- [ ] Make one change that a fresh conversation, reading only this plan, can make and check in one sitting.
  Done when: `dotnet test --filter "FullyQualifiedName~SomeTests"` passes.
- [ ] Split a big job into several steps like this one, in the order they must happen.
  Done when: the new behavior shows in a command's output, a file's contents or a test that names it.
- [ ] A step may hold sub-steps, one level deep, each with its own check.
  Done when: every sub-step below is ticked.
  - [ ] The first part of that step.
    Done when: `dotnet build` passes with no new warnings.
  - [ ] The second part of that step.
    Done when: the test written in the first part passes.
- [ ] A step only a person can check starts its check with By hand:, on a step with no sub-steps.
  Done when: By hand: the new window opens and its title reads right; nothing a command could show is left to this line.
- [ ] Update the docs and help that describe what changed.
  Done when: each changed command, setting or behavior is described where its readers look for it.

## Files and areas

- `path/to/file.cs`: what changes there.
- `path/to/OtherFile.cs`: what it gains or loses.

## Risks

- What could break, and how a step guards against it.
- Open question (your call): a choice left for the reader, with the option you'd pick.

## Verification

- The commands that prove the whole plan worked, in order.
- What to check by hand, and where.
