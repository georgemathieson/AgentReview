# AgentReview

A local Blazor app that diffs two git branches and produces output an AI can review without getting confused.

## Why

A normal `git diff` shows only a few lines around each change. AI reviewers then see half a method and report
problems such as "this method is empty" or "missing return statement", because they can't tell what is
abridged context and what is the actual change.

AgentReview fixes this:

- **Complete code units.** For C#, each change is expanded with Roslyn to the *whole* member that contains it,
  including the method, constructor, property, field, enum or type, with its doc comments and attributes.
  If you change one doc-comment line, the AI sees the entire method with only that line marked.
- **Other files.** SQL is expanded to the complete statement or `GO` batch. JSON, YAML, XML, HTML, Razor,
  CSS and TS/JS are expanded to the complete enclosing indented block. Non-C# files of 150 lines or fewer
  are shown in full.
- **Explicit legend.** The output begins with instructions on how to read it. Only `+` and `-` lines are
  changes; space-marked lines are unchanged context. Omitted code is labelled
  `⋯ N unchanged lines not shown ⋯` so it is never mistaken for deleted or empty code.
- **Pull-request semantics.** The diff is taken from the merge-base (`base...head`), so changes made on the
  base branch after you branched off don't appear.
- **Noise control.** Lock files, minified files and source maps are listed but their content is omitted.
  Binary files and pure renames are summarised.

## Run

Requires the .NET 10 SDK and `git` on your PATH.

```bash
dotnet run --project src/AgentReview.Web
```

Open http://localhost:5195, then:

1. Enter the path to a local repository and click **Load branches**. Recently used repositories are remembered.
2. Pick the branch with the changes (head) and the branch to compare with (base), then click **Show diff**.
3. On the diff page, use **Download .md** to save the AI-ready Markdown, or switch to **Visual diff** for a
   colour-coded view of exactly the same regions.

To run it without the SDK tooling, publish once and start the DLL:

```bash
dotnet publish src/AgentReview.Web -c Release -o ./publish
dotnet ./publish/AgentReview.Web.dll
```

The app listens on localhost only. It runs read-only git commands (`for-each-ref`, `rev-parse`,
`merge-base`, `diff`, `cat-file`) and never changes your repository.

## Settings

Defaults are in `src/AgentReview.Web/appsettings.json` under `Review`. The first three can also be changed
per run under **Advanced options**.

| Setting | Default | Meaning |
|---|---|---|
| `ContextLines` | 5 | Context lines around a change when no enclosing unit can be found, e.g. `using` directives. |
| `MaxUnitLines` | 500 | Largest member or block shown in full. Bigger units show their declaration plus the changed area, with a note saying so. |
| `SmallFileLines` | 150 | Non-C# files up to this length are shown in full. |
| `MaxWholeFileLines` | 2000 | Largest added or deleted file shown in full. |
| `OmitContentPatterns` | lock files, `*.min.js`, ... | Files listed without content. |

## Layout

- `src/AgentReview.Core`: git access, the merged diff model, region strategies (Roslyn C#, SQL,
  indentation blocks) and the Markdown formatter. It has no UI dependencies, so a CLI could reuse it.
- `src/AgentReview.Web`: the Blazor Web App (.NET 10, interactive server rendering).
- `tests/AgentReview.Core.Tests`: xUnit tests that create real temporary git repositories.

```bash
dotnet test
```
