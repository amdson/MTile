# Tooling hosts map (MTile.Demo, MTile.Probe, Bench, FxLab)
Verified against: 4e34142 (2026-11-14)
Covers: MTile.Demo/*.cs, MTile.Demo/MTile.Demo.csproj, MTile.Probe/Program.cs, MTile.Probe/MTile.Probe.csproj, MTile.Bench/Program.cs (one line), MTile.FxLab/ (one line)

## What this area is
Two desktop-only authoring tools built on `MTile.Core`. `MTile.Demo` is a windowed multi-mode host (animation editor, sprite-bind editor, take viewer, reference-arc editor, art import, strip exporter, tree viewer). `MTile.Probe` is a headless CLI that edits the same clip JSON as numbers. Neither runs in the game; both are render/authoring-only and never feed the sim (the strip exporter's grab/stab/stairs-family scenarios do run `Simulation.Step`, but only to produce animator input).
Both are excluded from the library (`MTile.Core.csproj:17-18` `Compile Remove`) and are NOT in `MTile.sln` (no Demo/Probe entry), so `dotnet build MTile.sln` does not build them; build with `dotnet build MTile.Demo` / `MTile.Probe`. No test project references them; they are covered only indirectly through the Core classes they call (see Tests).
Boundary: clip/rig/binding data formats and solvers live in `Animation/`, `Skeletons/`, `Drawing/` (other maps). This area is only the editors and CLI that load/mutate/save them. Demo depends on ImGui.NET (`MTile.Demo.csproj`), and shares `Content/Content.Demo.mgcb` for `DebugFont`.

## Files
- `MTile.Demo/Program.cs` (94): mode dispatch, see Flow (a).
- `MTile.Demo/DemoGame.cs` (1890) + `DemoGame.Ui.cs` (1449): one `partial class DemoGame`, the animation editor. `.cs` = canvas half (state, picking, drags, hotkeys, draw, save); `.Ui.cs` = ImGui chrome. Indexes below.
- `MTile.Demo/ImGuiRenderer.cs` (259): vendored ImGui to MonoGame backend; `BeginFrame` / `EndFrame` (Update) and `RenderDrawData` (Draw).
- `MTile.Demo/ScenePlacement.cs`, `SceneGuideView.cs`, `ScenePreview.cs`: editor view components owned by DemoGame (`_placement`, `_guides`, `_preview`): zoom/pan/anchor math (shared `ClipMotion` query), guide tools, ghosts/path/body preview. Constant `RigScale = 5` px per rig unit at 100% zoom (`ScenePlacement.cs:24`).
- `MTile.Demo/ArcEditSession.cs`, `ArcEditOps.cs`: in-clip Hermite arc edit (working copy + pure ops); `HermiteClipGame` reuses `ArcEditOps` only (not the session).
- `MTile.Demo/BindGame.cs` (733): sprite-bind editor. `SpriteStripGame.cs` (688): batch PNG exporter. Indexes below.
- `MTile.Demo/ViewerGame.cs` (443): take viewer. `HermiteClipGame.cs` (479): `--ref` arc editor. `ImportGame.cs` (238): `--import` art intake. `TreeViewerGame.cs` (140): `--trees` backdrop viewer.
- `MTile.Demo/CONTROLS.md`, `STRIPS.md`: user-facing controls; see "Doc disagreements".
- `MTile.Probe/Program.cs` (1246): all of Probe; one static class `Probe`. Index below.
- `MTile.Bench/`: release-mode perf harness (`Program.cs` dispatches `--depths/--anim-baseline/--forge/--league/--competition/--corrector/--check/--save/--compare...`; compares against `MTile.Bench/baseline.txt`). Compiles the `MTile.Tests/Sim` harness files in as links (`MTile.Bench.csproj:15-17`; no Tests project reference). See `/perf-profiling`.
- `MTile.FxLab/`: shader/VFX sandbox, `Program.cs` just runs `FxLabGame` (one effect, live keys).

### DemoGame.cs index
- 29-175 fields, enums (`EditMode`, `NameTarget`), ctor (window 1040x680); `_baseSkeleton` vs `_skeleton` at 41-42
- 177 `LoadContent` (env vars 189-193, rig/clip load 201-253)
- 266 `Update` (BuildUi first 270; Escape 282; pan/zoom 297; pick 323; press 347; drags 390-448; release 450)
- 467 `Hotkeys`; 590 `EditBone` (Rotate/Resize/Stretch); 637 `SetBoneLength`; 666 `UpdateRoot`
- 681-858 arc session helpers (`ResolveArc`, `DrawArcSession`, `PickCanvas` 782, `CanvasTarget` enum 780); 848 `ActiveKeyCom`
- 862 `Draw` (screenshot capture 867-919); 956 `DrawEditor`; 1043 `DrawAdditions`
- 1101 `FlipAnimation` (F); 1159-1340 contact/knife/attachment/point ops (`ApplyContact` 1210, `AddKnife` 1235)
- 1342-1616 labeled additions, new bones, `DragAddition`, `PickJoint`
- 1618-1795 clip/keyframe state (`SelectAnimation` 1620, `SampleKeyframe` 1699, `NewAnimation` 1732, `CloneAnimation` 1744)
- 1796 `SaveAll`; 1833 `FindStatesDir`; 1847 `SkeletonsDir`; 1856 `BuildTypeOptions` (reflects over `ActionState` subclasses)

### DemoGame.Ui.cs index
- 26-69 `InitUi`, `BuildUi` (panel order: menu, Clips, Clip properties, Scene, Timeline, popups, then `_canvas` rect 65)
- 72 `MenuBar` (File/Clip/Edit/Scene/View); 163 `SceneMenuItems`; 211 `MapComToArc`
- 228 `ClipsPanel`; 256 `InspectorPanel`; 329 `ScenePanel`; 466-507 add block/ground at view
- 509 `TimelinePanel` (+ contact/attachment span rows 731-850, `DragContactSpan` 799, `DragAttachSpan` 807)
- 851 `ContactCurveWindow`; 893 `AttachmentCurveWindow`; 1082 `EndpointPopup`; 1114 `EndpointMenuItems`
- 1234 `ArcEditorWindow`; 1295 `OpenArcEditor`; 1327 `CloseArcEditor`; 1366 `PromoteArcToShared`; 1387 `NameModal`; 1410 `HelpWindow`

### BindGame.cs index
44 class; 121 `LoadContent` (json-first resolve 130-170, new-from-PNG path, `MTILE_SHOT*` 201-221); 231 `ResolveSkeleton`; 246 `ResolveJson`; 258 `LoadBackdrops`; 327 `FitRigToImage`; 363 `Update`; 465 `EditBone`; 500-534 preview/play/clip/layer cycle, `RebakeSkinIfNeeded` 534; 549 `Save`; 559 `Draw`; 648 header/help; 711 `FindRepoRoot`; 724 `ResolvePng`.

### SpriteStripGame.cs index
14 class + scenario `Column` table (17-34); 62 ctor (arg parse 66-90, validation 99-120: rig default 120); 133-165 SDL window hiding (`QuietWindow`, `HideWindow`); 174 `LoadContent` (builds `_panels` then fits camera); 258 `SampleClip` (raw clip); 276 `ReplayTake`; 311 `CaptureSlashCombo`; 362 `CaptureGrab` (stab/guard/grab/drop/run-/charge/jump scenarios); 456 `CaptureStairs` (stairs + obstacle scenarios); 541 `Draw` (renders once, writes PNG, `Exit()`).

## Flow
### (a) Demo mode dispatch (`MTile.Demo/Program.cs`)
First match wins, top to bottom:
1. `--strip` anywhere in args (`Program.cs:4`) -> `SpriteStripGame.QuietWindow()` then `new SpriteStripGame(args)` (strict own parser; its `--load` means take file, `--scenario`, `--frames/--columns/--size/--facing/--start/--end/--stairs/--world/--overlay/--trails/--contacts/--path/--usebind/--rig`, one bare arg = clip name). Errors print `strip: ...`, exit code 1.
2. Otherwise a loose loop (`:51-63`) reads `--trees`, `--bind <v>`, `--rig <v>`, `--usebind <v>`, `--load <v>`, `--ref <v>`, `--import <v>`, `--out <v>`, `--scale <f>`; any other token becomes the clip name (first one wins, `clip ??=`). A mode flag missing its value falls through to clip name.
3. `--trees` (`:65`) -> `TreeViewerGame`. 4. `--import` (`:70`) -> `ImportGame(dir, out="SpriteBindings", scale=0.25)`. 5. `--ref` (`:75`) -> `HermiteClipGame`. 6. `--load` (`:80`) -> `ViewerGame`. 7. `--bind` (`:85`) -> `BindGame(png|name, rig)`. 8. else `DemoGame(clip, usebind, rig)` (`:90`).
Env vars (screenshot: render to a target on frame >= 10, save PNG, `Exit()`; see `/screenshot`): `MTILE_SHOT=<png>` read by DemoGame (`DemoGame.cs:189`), BindGame (`:204`), HermiteClipGame (`:114`), ViewerGame (`:85`), TreeViewerGame (`:56`, captures frame 20); NOT by ImportGame or SpriteStripGame. Modifiers: editor `MTILE_SHOT_HELP/_WIRE/_NOSKEL/_BODY` (`DemoGame.cs:190-193`); bind `MTILE_SHOT_PREVIEW/_CLIP/_LAYER` (`BindGame.cs:205-211`); viewer `MTILE_SHOT_HELP`, `MTILE_SHOT_FRAME` (`ViewerGame.cs:86-87`).

### Demo editor per frame
1. `Update`: `BuildUi` runs first (`DemoGame.cs:270`) and sets `io.WantCaptureMouse/Keyboard` (`:272-273`). Canvas gestures need `!uiMouse && _canvas.Contains` (`:278`); `Hotkeys` only when `!uiKeys` (`:312`).
2. Pick once per frame via `PickCanvas` (`:330`); press dispatches on `CanvasTarget` (`:350`); drags handled in priority order arc > guide > root > addition > IK > bone (`:390-448`); edits write straight into the active keyframe (`PoseData.Capture`) and set `_dirty`.
3. `Draw`: skin, attachments, `DrawEditor` in a SpriteBatch, then `_ui.RenderDrawData()` (`:902-908`).
4. Ctrl-S `SaveAll` (`:1796`).

### (b) Probe command table (`MTile.Probe/Program.cs`)
Global flags stripped before dispatch (`:68-79`): `--rig <name>` (default biped; also picks clip dir `SkeletonStates/<rig>/`), `--dir <name>` (clip dir override). No command = `list`. Command is lowercased (`:86`). Unknown -> exit 2 (`:116-119`); any exception -> `error: ...`, exit 1. W = writes clips in place (`AnimationStore.Save` to the pool dir).
| Command | Args | Dispatch / handler | Writes |
|---|---|---|---|
| list | | 89 / 131 | no |
| digest | [clip] | 90 / 144 | no clips; with no clip writes `.probe/<clip>.digest.md` for all (dir of `MTile.sln`) |
| diff | clip [ref=idle] | 91 / 160 | no |
| report | clip | 92 / 166 | no |
| anim | base [Action] | 93 / 176 | no (runs a live `CharacterAnimator`; base = idle/walk/run/walkback/jump/fall/crouch/crouchwalk/parkour/mantle/arcjump/ledgepull, `BaseSample` 1050) |
| addcom | [clip] [--dry] | 94 / 222 | W unless --dry (all clips if no clip) |
| ik | clip keyTime tip dx,dy [--to] [--chain a,b] [--write] | 95 / 253 | only with --write (dry by default) |
| new | name type [--dur s] [--from clip[@t]] [--noloop] | 96 / 392 | W (creates file, adds floor guide) |
| addkey | clip t [--from clip[@t]] | 97 / 436 | W |
| contact | clip start end point\|none [external] [corner] [--clear] | 98 / 466 | W (`none` clears all spans) |
| stride | clip | 99 / 553 | no (compiles `ClipStrideTrack`) |
| loopback | clip [--region f] [--minloop f] [--write [name]] | 100 / 521 | only --write (new Misc clip `<clip>_loop`) |
| rot | clip t bone value [--deg] | 101 / 587 | W |
| retime | clip t newT | 102 / 604 | W |
| delkey | clip t | 103 / 621 | W |
| dur | clip seconds | 104 / 634 | W |
| retarget | dstRig [clip] [--dry] [--out dir] | 105 / 657 | W unless --dry; writes to `SkeletonStates/<dst or --out>/`, pool form overwrites all dst clips; exit 1 if not direction-preserving |
| port | dstRig clip [--dry] [--view/--swap/--ref/--shoulderamp] [--noshoulders] | 108 / 789 | W unless --dry (retarget + bakeyaw + digest) |
| stretch | clip t bone s | 109 / 824 | W |
| bakeyaw | [clip] [--turn] [--view deg] [--swap s] [--ref rad] [--shoulderamp f] [--noshoulders] [--dry] | 110 / 893 | W unless --dry (all clips if none named); needs `hip_l/hip_r` |
| mapcom | clip arc [--local] [--stretch] [--dry] | 111 / 845 | W unless --dry |
| motion | clip [samples=8] | 112 / 1109 | no |
| bakepath | clip [--flat] [--dry] | 113 / 1132 | W unless --dry |
| scenecheck | clip [--tag-corners] | 114 / 1161 | only with --tag-corners |
| liftswing | clip [node] [--lift rig=6] [--dry] | 115 / 1147 | W unless --dry |
Flag parsing helpers: `Arg` 1182, `FirstNonFlag` 1186, `FirstPositional` 1195, `FlagValue` 1216, `HasFlag` 1223 (both case-insensitive); `FindUp` 1235 walks up from `AppContext.BaseDirectory`. Authoring helpers 1013-1047 (`NearestKey`, `ParseClipAt` "clip@t", `SamplePoseAt` uses `AnimationSampler.SampleSmooth`).

### Where each tool reads/writes
- Editor: reads `Skeletons/<rig>.json`, `SkeletonStates/<rig>/*.json` (`FindStatesDir` walks up to `MTile.sln`, `DemoGame.cs:1833`), `configs/movement_config.json` (`:208`), `Assets/AnimationEffects/`, `SpriteBindings/<name>.json` (`--usebind`, `:940`), `ReferenceClips/*.json` (arc overlays). Writes: all clips + (only if `_skelDirty`) the base rig file (`:1798-1810`); "Promote to shared" arc writes `ReferenceClips/<name>.json` (`DemoGame.Ui.cs:1374`).
- BindGame: reads `SpriteBindings/<name>.json` or PNG, `SkeletonStates/<rig>/` (preview clips); writes only the binding json (`Save` 549); a PNG argument creates `<png dir>/<name>.json`.
- ViewerGame: reads the take (path argument), `SkeletonStates/biped/`, `configs/anim_solver_config.json`; writes nothing but the screenshot. HermiteClipGame: reads/writes `ReferenceClips/<name>.json` (`HermiteClipGame.cs:96`, save `:261`). ImportGame: writes `<out>/<character>/<part>.png` + `<out>/<character>.json` (`ImportGame.cs:154-233`).
- SpriteStripGame: reads clips/takes/bindings, `configs/` (`:286,:367`); writes only the output PNG. Takes: path as given (conventionally `Takes/*.take.json`).
- Probe: reads/writes `SkeletonStates/<rig|--dir>/`, `SkeletonStates/<out>/`, reads `ReferenceClips/` (`LoadArc` 869), writes `.probe/`. None of the tools touch `SkeletonDrafts/` (no reference in either project).

## Invariants and traps
- Editor rig duality: `_baseSkeleton` is the shared rig; `_skeleton` is base + active clip's ExtraBones, rebuilt on clip switch (`DemoGame.cs:37-42`). `SaveAll` captures only `_baseSkeleton` (`:1807`) so clip-local bones never leak into the rig file. A bind edit on a base bone sets `_skelDirty`, on a clip-local bone `_dirty` (`:445-446`).
- `SaveAll` rewrites every loaded clip, not just dirty ones (`:1798`).
- Root-joint drag edits the active key's `com` INVERSELY (`comAdd.Px -= dm.X/Scale`, `:409-410`); with no active key it pans the view instead (`:417`).
- UI/canvas gating: panels are built before canvas input each Update; breaking that order desyncs `WantCapture*` by a frame (`:270-278`). Escape order: arc edit, IK drag, guide cancel, then quit (`:282-293`).
- Key `X` is overloaded: arc-edit key delete (`:479`) and skeleton toggle with a skin (`:553`); `Delete`/`Back` is a priority chain guide > attachment > point > addition > keyframe (`:492-499`).
- `DemoGame` default rig is `biped` (`DemoGame.cs:201`); the comment at `:158-159` ("default biped_rabbit") is stale.
- Program.cs: `--strip` is detected anywhere before any other parsing (`:4`), so `--load` with `--strip` is the strip exporter's take, not the viewer. Flag order does not matter; mode priority is the code order above, not argument order.
- ViewerGame ignores `--rig`: it hard-loads `SkeletonExamples.Biped()` (`ViewerGame.cs:95`). Strip default rig: biped for takes, `biped_rabbit` otherwise (`SpriteStripGame.cs:120`).
- Strip scenarios in the grab family (`stab*/guard*/grab*/drop*/run-*/charge*/jump*`, `CaptureGrab`) and the stairs/obstacle family (`CaptureStairs`) run real `Simulation.Step` with `MovementConfig.Load` of the repo `configs/` (`SpriteStripGame.cs:367,461`), process-wide for that process only. `slash-combo`/`slash1`/`whip-kick` (`CaptureSlashCombo` 311) are animator-only: no sim, no hitstop. Default frame range: 0-95 grab family (`:364`), 0-149 stairs family (`:458`); max 3599.
- `new`: refuses an existing name (`Probe:395`); `addkey` refuses a key within 1e-3 of an existing time (`:441`); `delkey` refuses the last key (`:624`); `ik` snaps to the nearest keyframe time (`:278`) and uses y-down rig units (+y DOWN).
- `contact`: `end` may exceed 1 to wrap the loop seam; `start` must be in [0,1) and span <= 1 (`:482-485`); point must resolve to a named point id or a bone with a point at its End (`ResolveContactPoint` 505).
- `retarget` pool form (no clip) overwrites every destination clip, including re-authored ones (`:643-649` comment, `:771`); source and dest rig must differ (`:660`). `port` aborts before bake if the retarget is not direction-preserving (`:795-796`).
- `bakeyaw` needs a strutted rig (`hip_l/hip_r`, `:895`); it is re-runnable and overwrites Stretch from the law. `loopback --write` writes a `Misc` viewer copy, never bound (`:542-544`).
- `addcom` and `new` derive ground from `PlayerCharacter.Radius` and `Game1.SkeletonScale` (`:224-225, 390`); these core constants are read-only, changing them invalidates every stamped `com`.
- Probe's unknown-command message lists 18 commands (`:117-118`); the switch handles 25 (table above): `stride`, `loopback`, `port`, `mapcom`, `bakepath`, `scenecheck`, `liftswing` are missing from it. `--rig`/`--dir` strip only the first occurrence and use case-sensitive match (`List.IndexOf`), unlike `HasFlag`.
- Probe paths use `FindUp` from the binary dir; running a copied binary outside the repo tree throws `DirectoryNotFoundException` (`:1244`).

### Doc disagreements
- `CONTROLS.md` "five modes" and its priority list `--import -> --ref -> --load -> --bind -> editor` omit `--trees` (first of the loose modes) and `--strip` (checked before all); code has seven modes.
- `CONTROLS.md` flag table says `--rig` default for the editor is `biped_rabbit`; code default is `biped` (`DemoGame.cs:201`), as CONTROLS.md's own example comment says.
- `CONTROLS.md` says `MTILE_SHOT` works in every mode; ImportGame and SpriteStripGame do not read it.
- `CONTROLS.md` describes the IK toggle as an "IK drag box (header)"; it is now Edit > IK drag in the menu bar (`DemoGame.Ui.cs:111`) plus an "IK drag" checkbox in the Clip properties panel (`:302`). Its two "retarget" example lines are identical (the "one clip" one names no clip).
- `CONTROLS.md` (com-marker row) says the drag saves "`edref` additions; the runtime ignores them". The channel is `body_path` (`BodyPath.ChannelName`; written by `DragAddition`, `DemoGame.cs:1543-1553`). Gameplay placement ignores it (`Animation/BodyPath.cs:19-20`), but the runtime animator does read it: `GaitTiming.CycleDisplacement` paces the cadence from `p(1)-p(0)` when a clip has one (`Animation/GaitTiming.cs:86-91`). So body-path edits do reach the runtime, through gait pacing only.
- `STRIPS.md` matches the code on rig defaults, option names and ranges checked (`SpriteStripGame.cs:62-120`). Its "frames 0-149" default is stated only for stairs; it never states the grab family's 0-95.

## Where to change X
- A new editor hotkey: `DemoGame.Hotkeys` (`DemoGame.cs:467`); add the matching menu item in `DemoGame.Ui.cs:72 MenuBar` and the help row in the `HelpRows` table (`DemoGame.cs:1589`; `HelpWindow` at `DemoGame.Ui.cs:1410` only renders it).
- A new canvas gesture or pick target: `CanvasTarget` enum + `PickCanvas` (`DemoGame.cs:780-782`) and the press switch (`:350`).
- Panels, inspector fields, timeline rows: `DemoGame.Ui.cs` `InspectorPanel` 256 / `TimelinePanel` 509; panel sizes `LeftW/RightW/BottomH` at 32-34.
- Endpoint context menu (elements, contacts, points): `EndpointMenuItems` (`DemoGame.Ui.cs:1114`) backed by `ApplyContact`/`AddKnife` (`DemoGame.cs:1210,1235`).
- What gets saved / where: `SaveAll` (`DemoGame.cs:1796`), `FindStatesDir` 1833, `SkeletonsDir` 1847.
- A new Demo mode: add a flag to the loop and a branch in `Program.cs:51-94` (keep `--strip` first if it needs its own parser); new `Game` subclass like `TreeViewerGame`.
- A new strip scenario: validation list `SpriteStripGame.cs:100-101`, dispatch in `LoadContent` (~`:180-186`), and a `Capture*` method (grab/stairs at 362/456).
- A new Probe command: add a `case` in `Probe.Main` (`Program.cs:87-115`), a static handler, and add it to the unknown-command message (`:117`); use `Find`/`NearestKey` and call `AnimationStore.Save(clip, _statesDir)` to persist.
- Bind editor behavior (rig-over-art alignment, preview, layers): `BindGame.Update` 363 / `EditBone` 465 / `RebakeSkinIfNeeded` 534.
- Take replay or solver overlays: `ViewerGame.Presolve` (`ViewerGame.cs:119`) and its draw/overlay code.

## Tests
No test class targets `MTile.Demo` or `MTile.Probe` directly. The Core code they drive is covered in `MTile.Tests/Animation/`: `MotionProbeTests`, `PoseIkTests`, `AnimDragIkTests`, `AnimLoopBackTests`, `AnimLoopCutTests`, `AnimStrideTrackTests`, `AnimSceneTests`, `AnimStepUpSceneTests`, `AnimContactSpanTests`, `AnimEndpointTests`, `AnimTakeTests`, `AttachmentTests`, `PoseStretchTests`, `CharacterAnimatorTests`. Run with `scripts/test-group.py animation` (terms `Anim`, `MotionProbe`, `Attachment`, `SpriteBinding`, ...). `ClipBindingTests` (clip Type vs `ActionState` class names, relevant to `probe new`) is matched by the `combat` group (`scripts/test-group.py:29`); `animation` catches only its `EveryAnimClip_HasAnAuthoredFile` test (the `Anim` term matches the method name; the namespace is `MTile.Tests`, not `...Animation`). Per CLAUDE.md many clip-content tests are red by design (BACKLOG section 5.0).
