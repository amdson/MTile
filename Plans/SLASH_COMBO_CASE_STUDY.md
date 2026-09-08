# Rabbit ground combo: presentation pass

The three ground slash clips now form a downward cut, upward return, and stronger
downward finisher. Most changes are authored poses and effect timing in
`SkeletonStates/biped_rabbit/groundslash{1,2,3}.json`.

## Gameplay timing preserved

No action-state or combat code was changed. Startup, active windows, action
durations, recovery, damage, hit shapes, and combo eligibility retain their values.
Clip Duration, SettleShare, Region, skeleton, and attachment bones are also unchanged.

| Move | Action duration | Startup at 60 Hz | Active | Recovery if not chained |
| --- | --- | --- | --- | --- |
| Slash 1 | 0.20 s | 5 frames | 3 frames | 0.167 s / 10 frames |
| Slash 2 | 0.18 s | 4 frames | 3 frames | 0.167 s / 10 frames |
| Slash 3 | 0.27 s | 8 frames | 4 frames | 0.30 s / 18 frames |

The action's reported progress maps onto `[0, 1 - SettleShare]` in its clip.
Thus the active windows land approximately at clip times 0.267–0.427,
0.289–0.506, and 0.346–0.519. The new cuts cross the forward space in those
windows, rather than holding overhead during contact.

## Motion and effects

Slash 1 prepares above and in front of the shoulder, cuts down through forward
extension, and finishes low. Slash 2 starts in that low pose and cuts upward.
It finishes high, matching slash 3's opening pose. Slash 3 braces briefly, then
uses a larger downward cut and torso follow-through. Supporting-arm and head
motion follow the torso without changing the lower-body animation mask.

Both handoffs are held before the action boundary so discrete frame sampling does
not depend on reaching exactly progress 1. If a follow-up is not taken, the
settle tail returns the upper body to neutral within the existing recovery.

The existing doubled-size PNG and forearm alignment are retained. The blade forms
before contact, stays on its two full-blade cells through the hit window, then
dissolves during follow-through. Trail emission is confined to the cut with a
small boundary allowance. Following the wider-streak reference, the full blade
length now paints the trail, at 80% of the existing opacity; its 65 ms lifetime
is unchanged.

Small optional authoring controls were added to the attachment model/renderer:
`FrameTimes`, `TrailStart`, `TrailEnd`, `TrailWidth`, and `TrailOpacity`. Defaults
preserve the old behavior for the other clips. A trail discontinuity threshold
also accounts for the rendered blade span so zoomed exporter views do not
mistake a fast swing for a teleport. There was no rewrite of the animation solver,
overlay blending, or gameplay action logic.

## Review and checks

Local review artifacts are in `.probe/slash-combo/`: `preview.html`, full runtime
animator sequences facing right/left, and slash 1 played alone through recovery.
The HTML offers the actual 60 Hz clock, slow playback, and individual frames.
The pre-edit clip files are retained in that directory's `before/` folder.

The exporter has two additional rehearsal scenarios:

```sh
dotnet run --project MTile.Demo -- --strip /tmp/combo.png --scenario slash-combo --usebind rabbit --frames 64 --columns 8 --size 320 --trails
dotnet run --project MTile.Demo -- --strip /tmp/solo.png --scenario slash1 --usebind rabbit --frames 28 --columns 7 --size 320 --trails
```

These run the real CharacterAnimator, including its pose blending and settle,
with action progress obtained from the action classes. They rehearse an immediate
stationary chain, with the existing recovery counts. They do not simulate input
buffering, opponents, hitstop, or movement; those remain in-game playtest concerns.

40 focused tests passed: attachment behavior, existing overlay/binding contracts,
unchanged action/startup/active durations, solid blade frames throughout contact,
matching handoff poses, and alternating runtime cut directions in both facings.
Editor/exporter build succeeded. Ground combo, mirrored combo, and standalone
slash-1 recovery renders were visually inspected.
