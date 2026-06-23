# Animation TODO

Small follow-ups that don't warrant a plan doc yet.

## Editor WYSIWYG: editor still samples linearly

The runtime (`CharacterAnimator`) now samples clips with C1 Catmull-Rom interpolation
(`AnimationSampler.SampleSmooth` / `SampleAngularVelocity`), but the editor still scrubs
and plays through the old linear path (`AnimationSampler.SampleNormalized` / `SampleAtTime`).

Consequence: in-between (non-keyframe) poses look slightly different in the editor than in
game — the editor shows linear blends, the game shows the smooth spline. Keyframes themselves
are identical in both, so authoring is unaffected; this only bites when judging in-between
timing/overshoot in the editor.

Fix when it matters: point the editor scrub/playback at `SampleSmooth` (needs the 4-pose
keyframe quad scratch instead of the current 2). Low priority — flagged so it isn't a surprise.
