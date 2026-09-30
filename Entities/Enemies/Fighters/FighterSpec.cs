using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// A fighter as a DESIGN DOCUMENT (Plans/FIGHTER_DESIGN_PLAN.md §2): attributes you buy,
// the actions you carry, and the brain that ships with it. FighterCompiler prices it
// through an ICostModel and, if it validates, turns it into the EnemyBlueprint the sim
// actually spawns. Specs are plain C# object initialisers, like blueprints.
//
// There is deliberately no Mass field. Mass is derived by the cost model from what the
// spec buys — a designer never types a mass, they buy things that weigh something.
public sealed class FighterSpec
{
    public required string     Name;
    public required EntityKind Kind;                 // one per fighter; see EntityKind

    // ── Attributes (§3.2) — each is a buy with a physical consequence ──────────
    public float Health        = 3f;
    public float Strength      = 1f;   // damage scale on every action's hitbox
    public float Armor         = 0f;   // knockback divisor beyond mass
    public float EnergyReserve = 0f;   // EnemyEntity.EnergyMax
    public float EnergyRegen   = 0f;   // units per second
    public float GroundPower   = 0f;   // walk accel = GroundPower / Mass (0 = cannot walk)
    public float JumpImpulse   = 0f;   // launch v   = JumpImpulse / Mass (0 = cannot jump)
    public float Thrust        = 0f;   // fly accel  = Thrust / Mass      (0 = cannot fly)
    public bool  Cling         = false;
    public bool  TargetMemory  = false;
    public bool  Rooted        = false;
    public float Radius        = 12f;
    public int   Sides         = 6;
    // Which side it fights for (plan §5.3). Every stock enemy is team 2; the arena
    // assigns others. Copied onto EnemyBlueprint.Team once the phase-4 targeting work
    // is merged into this branch.
    public int   Team          = 2;

    // ── Look (free) ───────────────────────────────────────────────────────────
    public Color Color = new(150, 30, 30);
    // Null ⇒ the compiler picks a stock sprite from the body's shape.
    public Func<float, Sprite> Sprite;

    // ── Actions (§4) — the brain addresses them by index into this list ───────
    public List<ActionSpec> Actions = new();

    // ── Brain (§5) ────────────────────────────────────────────────────────────
    // A factory so the brain can copy the spec's config below into itself. The result
    // must be a stateless flyweight: every piece of per-entity memory in
    // ctx.Self.Scratch, no System.Random, no statics, no wall clock.
    public required Func<FighterSpec, EnemyController> Brain;

    // BrainConfig (§8.3): the floats a bundled brain reads, so a designer — human or
    // search — can vary behaviour inside a brain it cannot rewrite. Not every brain
    // reads every field; FighterBrains.cs documents which.
    //
    // Stop closing in once this near (centre to centre). For a melee kit the compiler
    // enforces effective reach > trigger MaxRange > EngageRange (§4).
    public float EngageRange        = 26f;
    // Kiter: back away from the target inside this distance.
    public float StandoffRange      = 0f;
    // Hover brain: altitude held above the target before a dive.
    public float HoverHeight        = 70f;
    // Ignore the target entirely beyond this — no move, no attack. Keeps a row of
    // fighters on a stage from all converging the moment the match starts.
    public float AlertRange         = 260f;
    // Break off and retreat for a beat while Health / MaxHealth is below this (0 = never).
    public float RetreatBelowHealth = 0f;
    // Action index the brain asks for when it is in that action's band; -1 = no preference.
    public int   PreferredAction    = -1;
}
