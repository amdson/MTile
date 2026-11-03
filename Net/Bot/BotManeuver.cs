namespace MTile.Net;

// One bot behavior: a multi-frame input script ("tap LMB on the target", "hold + swipe
// a stab", "stack blocks and climb"). The brain (BrawlerBotInputSource) is a utility
// selector over a list of these:
//
//   * While no maneuver runs, it calls Score on every maneuver whose cooldown is up and
//     Begins the highest positive one.
//   * Each frame the brain first writes default locomotion (chase/back off) and the
//     cursor (on the perceived target) into `input`, then hands it to the running
//     maneuver's Step, which overwrites whatever it needs. A maneuver with
//     OwnsLocomotion = true gets a blank input instead (cursor still on the target).
//   * Step returns false when the maneuver is done; whatever it wrote on that frame is
//     still emitted. Cooldown then starts.
//
// A maneuver instance belongs to one bot and may keep any private state it likes
// (frame counters, chosen cells, aim offsets) — bots are outside the sim, nothing here
// is snapshotted. Randomness only through v.Rng, so a seeded bot is reproducible.
//
// Maneuvers express attacks the way a human does: through raw PlayerInput (held keys,
// LMB/RMB, Shift, the cursor). InputParser decodes them; nothing is injected.
public abstract class BotManeuver
{
    public virtual string Name => GetType().Name;

    // Frames after this maneuver finishes before it may be Scored again.
    public virtual int Cooldown => 30;

    // True ⇒ the brain applies no chase/backoff while this runs; the maneuver writes
    // all movement keys itself.
    public virtual bool OwnsLocomotion => false;

    // Utility of starting this maneuver now. ≤ 0 ⇒ not applicable. Rough scale: 1 is a
    // stock "good idea" (an in-range slash); the brain takes the max. Must not mutate
    // state the Step relies on — Begin does setup.
    public abstract float Score(BotView v);

    public virtual void Begin(BotView v) { }

    // Write this frame's input on top of `input`. Return false when finished.
    public abstract bool Step(BotView v, ref PlayerInput input);

    // Called when the brain abandons a running maneuver early (self got hit, target
    // died). Default: nothing.
    public virtual void Abort(BotView v) { }

    // Per-bot bookkeeping owned by the brain.
    internal int CooldownLeft;
}
