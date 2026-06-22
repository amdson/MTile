# Game1 Refactor Plan

`Game1.cs` is 1029 lines. Its current comment says it should be "a thin shell" (gather input → Step → render), but ~800 of those lines are rendering, HUD, debug visualization, and cosmetic update logic that have never been extracted. This plan catalogs the opportunities in rough priority order.

---

## 1. DebugOverlayRenderer — extract all debug draw methods

**Lines affected:** `DrawHitbox` (973), `DrawHurtbox` (1003), `DrawForceField` (912), `DrawConstraintArrow` (931), `DrawSteeringRamp` (946), `DrawPolygon` (819), `DrawLine` (1021), `DrawEntityHealthBar` (829)

These 8 methods share only `_spriteBatch`, `_pixel`, and `_draw`. They have no dependency on sim-specific types beyond the structs they receive as arguments. Extract to `Drawing/DebugOverlayRenderer.cs` — takes `DrawContext` + `SpriteBatch` + `Texture2D pixel` in ctor, exposes the same methods. `Game1.Draw` shrinks by ~120 lines and the conditioned debug blocks become one-liners:

```csharp
if (_config.DebugDrawHitboxes)
    foreach (var hb in _sim.Hitboxes.All) _debugOverlay.DrawHitbox(hb);
```

---

## 2. HudRenderer — extract all screen-space HUD draws

**Lines affected:** `DrawBlockPickerHud` (843), `DrawPlayerHealthBar` (884), `DrawPercentHud` (900), plus the debug-text `DrawString` block at line 575–581

These all open a second screen-space `SpriteBatch.Begin()` pass and need only `_spriteBatch`, `_pixel`, `_debugFont`, viewport width, and a few sim queries. Extract to `Drawing/HudRenderer.cs`. Callers become:

```csharp
_hud.Draw(_sim, _animator, _config);
```

---

## 3. DevDemoRenderer — extract the three dev preview methods

**Lines affected:** `DrawPrimitiveDemo` (620), `DrawDensityDemo` (647), `DrawMetaballDemo` (664), `DrawGlowDemo` (766)

These are entirely self-contained dev visualizations, each gated by a `DebugDraw*` flag. They carry their own mutable state (`_metaballDemoBones`, `_glowDemoTrail`, `_glowDemoT`). Extract to `Drawing/DevDemoRenderer.cs` — takes the three subsystem renderers in ctor, exposes a single `Draw(Matrix cam, Vector2 anchor, GameConfig cfg)`. The three state fields leave `Game1` entirely.

---

## 4. AttackGlowSystem — extract knife-trail + glow render cluster

**Lines affected:** `UpdateKnifeTrail` (692), `TryAttackKnifePos` (705), `RigRoot` (716), `AttackGlowColor` (732), `RenderActionGlow` (744), plus the `_knifeTrail`, `_knifeFieldPrev`, `_knifeFieldActive` fields

These five methods form a tight cluster: they read the animator's live pose, extract the knife bone position, feed a Trail, and drive the GlowRenderer or GlowTrailField. The only Game1 state they need is `_animator`, `_glow`, `_glowField`, and the two field trackers. Extract to `Drawing/AttackGlowSystem.cs`. `Game1.Draw`'s glow block (lines 535–564) collapses to:

```csharp
_attackGlow.Update(player, dt);
_attackGlow.Draw(camTransform, _sim, _config);
```

`RigRoot` is also used by `CharacterAnimator.Draw` calls — keep it as a static helper on `AttackGlowSystem` or promote to a `CharacterAnimator` extension.

---

## 5. CosmeticUpdateSystem — consolidate the cosmetic Update block

**Lines affected:** Update lines 296–355 — sprite sync, animator updates, secondary-animator growth, knife trail update, particle update, camera track, landing puff

This block already has the comment "Cosmetic-only systems below; they read sim state but never write it." It's a natural unit. Extract to `Drawing/CosmeticUpdateSystem.cs` with an `Update(Simulation sim, GameTime gameTime, GameConfig config)` method. It owns `_particles`, `_cursorTrail`, `_camera`, `_wasGroundedLastFrame`, `_secondaryAnimators`, `_animator`. `Game1.Update` becomes: gather input → step sim → cosmetic.Update → base.Update.

---

## 6. ScreenshotSystem — isolate capture logic

**Lines affected:** Initialize (lines 123–126), Update (lines 249–258), Draw (lines 364–375, 587–603); fields `_autoShotPath`, `_frameCount`, `_shotPending`, `_exitAfterShot`, `_prevShotKey`, `_autoShotFrame`

The screenshot mechanism is woven through all three lifecycle methods. Extract to `Drawing/ScreenshotSystem.cs` with `Initialize()`, `Update(KeyboardState, GraphicsDevice)`, `BeginCapture(GraphicsDevice) → RenderTarget2D?`, and `EndCapture(RenderTarget2D, SpriteBatch, GraphicsDevice) → bool exitNow`. `Game1.Draw` wraps the scene in the target only when `BeginCapture` returns non-null.

---

## 7. screenCenter caching

**Lines affected:** Update line 262, Draw line 380 (computed independently each frame from `GraphicsDevice.Viewport`)

Trivial: make `_screenCenter` a field, recompute it once per frame at the top of `Update`, read it in `Draw`. Eliminates the minor duplication and makes it clear the value is consistent within a frame.

---

## 8. DrawChunk — move to Chunk or a ChunkRenderer

**Lines affected:** `DrawChunk` (779) — iterates `Chunk.Tiles`, reads `_sim.Chunks.Damage`, calls `GetTileBaseColor`

`DrawChunk` and `GetTileBaseColor` together are ~30 lines with no dependency on Game1 fields beyond `_spriteBatch`, `_pixel`, and `_sim.Chunks.Damage`. They belong in `Drawing/ChunkRenderer.cs` alongside the sprout drawing (lines 396–422). The entire world-geometry pass in `Game1.Draw` (lines 387–422) then delegates to a single `_chunkRenderer.Draw(chunks, spriteBatch)`.

---

## Suggested extraction order

1. **DebugOverlayRenderer** — highest line count, zero coupling to other extractions
2. **HudRenderer** — self-contained, separate SpriteBatch pass
3. **DevDemoRenderer** — removes three state fields, zero gameplay coupling
4. **ChunkRenderer** — isolated tile-draw pass
5. **ScreenshotSystem** — removes cross-method state
6. **AttackGlowSystem** — more coupling (animator + glow renderers), do after the above
7. **CosmeticUpdateSystem** — largest refactor; own all cosmetic fields
8. **screenCenter caching** — trivial, do whenever

After all extractions, Game1 should own: `_sim`, `_config`, `_net`/`_session`/`_botInput`, `_graphics`, `_spriteBatch`, `_pixel`, `_debugFont`, `_draw`, `_prims` (until moved), and a reference to each extracted system. `Update` and `Draw` become orchestration only.
