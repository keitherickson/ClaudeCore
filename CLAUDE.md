# CLAUDE.md

Guidance for Claude when working in this repository.

## Project

KeithVision — an ASP.NET Core (.NET 10) app for local AI video generation/processing.
Key projects:
- `KeithVision.Core/` — services (LTX/Wan video, upscaling, speed, sound, ffmpeg helpers).
- `KeithUI/` — the node "studio": a LiteGraph (ComfyUI-style) editor. Frontend nodes are
  registered in `KeithUI/wwwroot/js/studio.js`; each maps to an executor case in
  `KeithUI/Services/GraphExecutor.cs`. Edges carry files (image/audio/video) between nodes.
- Root `KeithVision.csproj` / `KeithVision.slnx` — the solution.

## Workflow rules

- **Solo project — never open pull requests.** Just commit and push to the working branch.
  Do not create PRs unless explicitly asked.
- **Develop on the designated feature branch.** Commit with clear messages and push with
  `git push -u origin <branch>`. Never push to a different branch without explicit permission.
- **Build before committing.** Run `dotnet build KeithVision.slnx` and ensure it succeeds
  (0 errors) before committing C# changes. For `studio.js`, run `node --check` for syntax.
- The .NET 10 SDK is installed automatically on web sessions via the SessionStart hook
  (`.claude/hooks/session-start.sh`), which also builds the solution. If the SDK is missing,
  install `dotnet-sdk-10.0` from Ubuntu's apt repos (the Microsoft CDN is blocked by policy).

## Studio node conventions

When adding a node, change all three layers consistently:
1. `studio.js` — `define(...)` the node (inputs/outputs/widgets), and add any needed checks
   in `validateGraph()` / `REQUIRED_INPUT`.
2. `GraphExecutor.cs` — add a `case "<Type>":` that reads widgets and calls the reused
   `KeithVision.Core` services; return the produced file path (or null).
3. Reuse existing `VideoSpeedService` ffmpeg helpers rather than shelling out anew.
