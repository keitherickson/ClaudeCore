# Plan: Make the described pipeline the default studio graph

## Context
The user wants the KeithUI node studio to open with this pipeline by default:
1. **Sources** — Load Image, Generate Sound, Enhance Prompt
2. **Generate Video** using the loaded image + generated sound + enhanced prompt
3. **Save Video**
4. **Trim Tail** (remove the last N seconds, take the final frame)

The catch (confirmed by exploration of `GraphExecutor.cs:78-96`): the executor only runs
nodes that lead **into** a `Preview Save` node. `Trim Tail → Frame` outputs an *image*,
which can't feed `Preview Save` (it takes video). So a terminal Trim Tail sitting next to
Save would be **silently skipped** and never produce its frame.

**User decisions:**
- **Run Trim Tail by feeding its frame into a 2nd Generate Video that reaches a Save.**
  This makes Trim Tail upstream of a Save, so the existing executor runs it — *no backend
  change required*.
- **Image delivery: in-node preview only** (already how `Video/trim_tail_frame` behaves —
  it emits a `node-image` event handled at `studio.js:654-661`).
- **Deliverable: update the default starter graph.**

## Scope
Single change: rewrite `starterGraph()` in `KeithUI/wwwroot/js/studio.js` (currently
lines ~538-556). No backend changes, no new node types, no validation changes.

## New default graph

Nodes (8):
- `Image/load_image`        → Load Image
- `Sound/sound`             → Generate Sound  (the user's "generate audio"; the current
                               starter uses Load Sound — swap it)
- `Prompts/enhance`         → Enhance Prompt
- `Video/generate` (#1)     → Generate Video — first clip
- `Preview Save/save` (#1)  → saves clip #1  (step 3)
- `Video/trim_tail_frame`   → Trim Tail → Frame  (step 4)
- `Video/generate` (#2)     → Generate Video — continuation from the trimmed frame
- `Preview Save/save` (#2)  → saves clip #2 (this Save is what makes Trim Tail run)

Wiring (`node.connect(outSlot, targetNode, targetSlot)` — slot indices verified from the
node definitions: Generate inputs are image=0, audio=1, prompt=2, output video=0; Trim Tail
input video=0, output image=0; Enhance output prompt=0; Generate Sound output audio=0):
- `img.connect(0, gen1, 0)`    Load Image → Generate #1 (image)
- `gsnd.connect(0, gen1, 1)`   Generate Sound → Generate #1 (audio)
- `enh.connect(0, gen1, 2)`    Enhance Prompt → Generate #1 (prompt)
- `gen1.connect(0, save1, 0)`  Generate #1 → Save #1  (step 3)
- `gen1.connect(0, trim, 0)`   Generate #1 → Trim Tail (step 4 — one output, two links is fine)
- `trim.connect(0, gen2, 0)`   Trim Tail frame → Generate #2 (image — i2v continuation)
- `enh.connect(0, gen2, 2)`    Enhance Prompt → Generate #2 (prompt; reuse the same prompt)
- `gen2.connect(0, save2, 0)`  Generate #2 → Save #2  (makes Trim Tail upstream of a Save)

Seeds (so the default graph passes `validateGraph()` and runs as-is):
- `enh` idea widget — keep a sample like the current starter
  ("a serene mountain lake at sunrise, mist rising off the water").
- `gsnd` prompt widget — seed a sample (e.g. "gentle lapping water and distant birdsong");
  `validateGraph()` (studio.js:744-747) requires Generate Sound to have a non-empty prompt.
- Leave Generate #1 model at its default `bf16-2.3`: audio-to-video validation
  (studio.js:721-723) requires bf16 when an audio input is wired. Generate #2 has only
  image+prompt (i2v), bf16 is fine.

Positions: lay out left→right in three bands so wires read cleanly, e.g.
sources column at x≈40 (img y≈60, enh y≈320, gsnd y≈540); gen1 x≈380; save1 x≈760 (top);
trim x≈760 (lower band), gen2 x≈1040, save2 x≈1420. Exact coords are cosmetic and adjustable.

## Why no executor / backend change
With `gen1 → trim → gen2 → save2`, the executor's upstream walk from `save2`
(`GraphExecutor.cs:83-91`) includes `trim`, so it executes and emits `node-image`; the frame
renders inside the node (studio.js:654-661, draw band at studio.js:411-429). Two Preview Save
nodes are both run-roots — the executor unions the upstream sets, so clip #1 and clip #2 both
generate and play in their nodes.

## Verification (manual, in the browser — JS-only change, no compile needed)
1. Run the app (the SessionStart hook builds it; `dotnet run --project KeithUI`), open the
   Studio page.
2. Confirm the canvas loads the 8-node default graph wired as above, with the seeded
   Enhance Prompt idea and Generate Sound prompt.
3. Click **Reset** — confirm it restores this same default (Reset calls `starterGraph()`).
4. Click **Run**. Expect: no validation warning; Generate #1 runs → Save #1 plays clip #1;
   Trim Tail runs and shows the extracted frame in its preview band; Generate #2 runs from
   that frame → Save #2 plays clip #2.
5. (Optional, faster sanity check without GPU generation) Replace the two Generate nodes
   mentally with Load Video to confirm the Trim Tail branch executes and previews a frame.

## Out of scope (per user answers)
- No image download button / Save Image sink (in-node preview only).
- No executor change to run terminal image nodes.
- No new node types; no changes to `GraphExecutor.cs` or `VideoSpeedService.cs`.
