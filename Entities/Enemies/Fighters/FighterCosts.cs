using System;
using System.Text.Json;

namespace MTile;

// The k_* coefficients of the physics-derived cost model (Plans/FIGHTER_DESIGN_PLAN.md
// §3.2, §3.3), with C# defaults and an optional JSON overlay from
// configs/fighter_costs.json. Loaded ONCE at boot (Game1, next to ImpactProfiles /
// MaterialStrengths): the coefficients decide every compiled fighter's Mass, so a
// mid-match reload would desync rollback peers. No hot reload, by design.
//
// ── Where the starting numbers come from ──────────────────────────────────────
// They are the campaign's first guess, back-solved from existing enemies so the stock
// roster lands at masses the sim is already tuned around. Phase 5's arena is what
// corrects them; do not hand-balance one against another.
//
// MASS ANCHOR — a Brute-like body must compile to the Brute's Mass 1.2:
//   Radius 12, Health 3, one stock melee (Damage 1.0, Reach 22), a stock-feeling walk
//   (GroundPower 100 at GroundDrag 0.02 → top speed √(100/0.02) ≈ 70 px/s) and the
//   stock jump (≈ 260 px/s launch at that mass → JumpImpulse ≈ 260 × 1.2 = 312).
//   Split the 1.2 roughly 30 / 25 / 10 / 15 / 20 across body / health / weapon / legs /
//   jump:
//     body   k_body · R²          = 0.0025 · 144   = 0.360
//     health k_hp · Health        = 0.10 · 3       = 0.300
//     melee  k_a · Damage · Reach = 0.005 · 1 · 22 = 0.110
//     walk   k_gp · GroundPower   = 0.002 · 100    = 0.200
//     jump   k_j · JumpImpulse    = 0.00075 · 312  = 0.234
//                                           total ≈ 1.204  ✓ (walk accel 83 px/s², jump 259 px/s)
//
// FLIGHT ANCHOR — a Bird-like body must be able to hover. EnemyFlyState holds altitude
//   only while Thrust / Mass > g (600 px/s²). With M = m0 + k_t·T that needs
//   T > 600·m0 / (1 − 600·k_t), so k_t must stay well under 1/600 or nothing flies.
//   k_t = 0.0005 ⇒ T > 857·m0. Bird body: 0.0025·81 + 0.10·2 + contact (0.005·0.4·11)
//   = 0.42 → Thrust 700 gives M = 0.77 (the Bird's hand-set 0.8) and T/M ≈ 905 — the
//   stock fly state's 900 px/s² at Mass 1. A 20-HP brick would need T > 2000 and a
//   mass over any budget: "can't fly because it's heavy" falls out of the numbers.
//
// ENERGY — meter units, the same units as BuildMeters / material_strengths.json.
//   k_shot 0.002 ⇒ the stock energy ball (500 px/s × 1.0 dmg) is 1.0 per shot;
//   k_tile 0.5 ⇒ the stock rail bolt is 0.002·1500·1.5 + 0.5·3 = 6.0 per shot;
//   k_dash 0.002 ⇒ the stock lunge (260 px/s) is 0.52 per dash;
//   c_hover 0.001 ⇒ Thrust 700 drains 0.7 units/s airborne.
//   Reserve and regen weigh something (k_e 0.03 per unit, k_r 0.15 per unit/s), which
//   is how the MASS budget bounds energy (energy has no compile-time cap, §3.4).
//
// FLOORS — a_min 40 px/s² is a DESIGN floor, not a physics one: EnemyChaseState
//   pre-compensates the floor's Coulomb brake, so any positive power walks eventually;
//   40 is "reaches 70 px/s in under two seconds". v_min 150 px/s is the launch that
//   clears a one-tile (11 px) step with margin (v²/2g ≈ 19 px).
//
// Actions without a reach (Slam, Ranged, RailShot, PounceSlam) are priced as a
// stock-reach melee of the same damage: k_a · Damage · ReachRef, ReachRef = 22 (the
// stock melee Reach). The plan's `k_a · Damage` for those kinds was unit-inconsistent
// with the melee row; the reference length makes one coefficient serve both.
public sealed class FighterCostConfig
{
    // ── Mass (§3.2) ─────────────────────────────────────────────────────────
    public float KBody        { get; set; } = 0.0025f;    // per px² of radius
    public float KHp          { get; set; } = 0.10f;      // per HP
    public float KStr         { get; set; } = 0.40f;      // per unit of Strength above 1
    public float KArm         { get; set; } = 0.50f;      // per unit of Armor
    public float KE           { get; set; } = 0.03f;      // per unit of energy reserve
    public float KR           { get; set; } = 0.15f;      // per unit/s of energy regen
    public float KGp          { get; set; } = 0.002f;     // per unit of GroundPower
    public float KJ           { get; set; } = 0.00075f;   // per unit of JumpImpulse
    public float KT           { get; set; } = 0.0005f;    // per unit of Thrust
    public float KCling       { get; set; } = 0.25f;      // fixed, if Cling is bought
    public float KA           { get; set; } = 0.005f;     // per damage·px of reach
    public float ReachRef     { get; set; } = 22f;        // px — reach-less kinds priced at this

    // ── Energy (§3.3) ───────────────────────────────────────────────────────
    public float KDash        { get; set; } = 0.002f;     // per px/s of lunge speed, per use
    public float KShot        { get; set; } = 0.002f;     // per (px/s · damage), per shot
    public float KTile        { get; set; } = 0.5f;       // per point of rail penetration
    public float CHover       { get; set; } = 0.001f;     // per unit Thrust, per second airborne
    // Block actions (§3.3; wired once ActionKind.PlaceBlock / SpawnBlockInAir are
    // priced): a placed tile costs its material's BuildCost; one conjured in mid-air
    // costs that times this premium.
    public float AirPremium   { get; set; } = 1.5f;

    // ── Floors (§3.2 hard constraints) ──────────────────────────────────────
    public float AMin         { get; set; } = 40f;        // px/s² — min GroundPower / Mass
    public float VMin         { get; set; } = 150f;       // px/s  — min JumpImpulse / Mass
    public float MinStrength  { get; set; } = 0.25f;

    // ── Points (§3.3) ───────────────────────────────────────────────────────
    public int   RootedDiscount     { get; set; } = 2;    // points REFUNDED by Rooted
    public int   TargetMemoryPoints { get; set; } = 1;
    public int   SlamPoints         { get; set; } = 1;    // Slam, PounceSlam (need jump or fly)
    public int   RailPoints         { get; set; } = 2;
    public int   LashPoints         { get; set; } = 1;    // needs cling
    public int   SpawnInAirPoints   { get; set; } = 1;

    // ── Default budget (§3.4) ───────────────────────────────────────────────
    public float MaxMass      { get; set; } = 2.5f;
    public int   MaxSlots     { get; set; } = 4;
    public int   MaxPoints    { get; set; } = 2;

    public FighterBudget Budget => new(MaxMass, MaxSlots, MaxPoints);
}

public static class FighterCosts
{
    // Defaults are in effect from process start, so tests and headless sims run without
    // a Load. The JSON overlay replaces this once, at boot.
    public static FighterCostConfig Current { get; private set; } = new();

    public static FighterBudget DefaultBudget => Current.Budget;

    public static void Load(string path)
    {
        try
        {
            using var stream = TitleContent.TryOpenRead(path);
            if (stream == null)
            {
                // Sim-affecting: every compiled fighter's mass comes from these.
                Console.WriteLine($"[FighterCosts] {path} not found — running on DEFAULT " +
                                  "fighter costs. Desyncs against any peer that loaded it.");
            }
            else
            {
                var opts = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                };
                // Absent keys keep the property initialisers above — a partial file is a
                // partial override, never a reset to zero.
                Current = JsonSerializer.Deserialize<FighterCostConfig>(stream, opts) ?? new();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FighterCosts] Load failed: {ex.Message}");
        }

        // The roster was compiled when EnemyFactory was first touched, which may have
        // been before this ran. Recompile it against the loaded coefficients so the
        // registered blueprints always match the file. Outside the try on purpose: a
        // coefficient change that pushes an archetype over budget must fail loudly.
        FighterRoster.RegisterAll();
    }
}
