using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;

namespace MTile;

// A saved fight. Not a recording: the sim is deterministic, so a bout is a pure
// function of its inputs — terrain, the specs, spawn positions, teams, the frame cap,
// the cost config — and this file stores exactly those, plus the result it produced
// and a per-frame checksum stream. Viewing a fight is re-simulating it
// (Game1 `--fight <file>`, FightRecord.ToStage); verifying one is re-simulating it and
// comparing checksums (Replay). A few hundred bytes of JSON per fight, and the same
// object the bout cache in Plans/FIGHTER_DESIGN_PLAN.md §11.1 keys on.
//
// Plain DTOs, no XNA types in the serialized shape (like AnimTake), so the file does not
// depend on which framework variant wrote it.
public sealed class FightRecord
{
    public int    Version    { get; set; } = 1;
    // Free text naming the code that produced this (a git hash when the writer knows it).
    // A replay that diverges under a different SimVersion is expected; under the same
    // one it is a determinism bug.
    public string SimVersion { get; set; } = "";
    public string Notes      { get; set; } = "";

    public TerrainDto        Terrain     { get; set; } = new();
    public List<EntryDto>    Entries     { get; set; } = new();
    public float             PlayerX     { get; set; }
    public float             PlayerY     { get; set; }
    public int               MaxFrames   { get; set; } = FighterArena.DefaultMaxFrames;

    // What the run produced. Null until recorded.
    public ResultDto         Result      { get; set; }
    // Simulation.Checksum() after every step, frame 1 first. Empty ⇒ unverified.
    public List<ulong>       Checksums   { get; set; } = new();

    public sealed class TerrainDto
    {
        public string Name        { get; set; } = "";   // "flat" / "corridor" / "hills" / "custom"
        public string Ascii       { get; set; } = "";
        public int    OriginTileX { get; set; }
        public int    OriginTileY { get; set; }
    }

    public sealed class EntryDto
    {
        public FighterSpecDto Spec { get; set; }
        public float          X    { get; set; }
        public float          Y    { get; set; }
        public int            Team { get; set; }
    }

    public sealed class ResultDto
    {
        public int     WinnerTeam  { get; set; }
        public int     Frames      { get; set; }
        public float[] HealthLeft  { get; set; }
        public float[] DamageDealt { get; set; }
    }

    // ── Build / run ──────────────────────────────────────────────────────────

    public static FightRecord Describe(string terrainName, IReadOnlyList<ArenaEntry> entries, Vector2 playerSpawn,
                                       int maxFrames = FighterArena.DefaultMaxFrames)
    {
        var r = new FightRecord
        {
            Terrain   = new TerrainDto { Name = terrainName, Ascii = FighterArena.TerrainAscii(terrainName) },
            PlayerX   = playerSpawn.X,
            PlayerY   = playerSpawn.Y,
            MaxFrames = maxFrames,
        };
        foreach (var e in entries)
            r.Entries.Add(new EntryDto { Spec = FighterSpecDto.From(e.Spec), X = e.Pos.X, Y = e.Pos.Y, Team = e.Team });
        return r;
    }

    // Run the fight, filling Result and Checksums. Returns the result. `instrument` sees
    // the entries just before the run — the league uses it to wrap each brain in a
    // TimedController (tool-side measurement that does not change the fight).
    public ArenaResult Record(ICostModel model = null, Action<IReadOnlyList<ArenaEntry>> instrument = null)
    {
        var sums    = new List<ulong>();
        var entries = BuildEntries();
        instrument?.Invoke(entries);
        var res  = FighterArena.Run(BuildTerrain(), entries, PlayerSpawn, MaxFrames, model, sums);
        Result    = new ResultDto { WinnerTeam = res.WinnerTeam, Frames = res.Frames,
                                    HealthLeft = res.HealthLeft, DamageDealt = res.DamageDealt };
        Checksums = sums;
        return res;
    }

    // Re-run and compare against the stored checksums. FirstDivergence is -1 when the
    // replay matched frame for frame (and the result too).
    public ReplayReport Replay(ICostModel model = null)
    {
        var sums = new List<ulong>();
        var res  = FighterArena.Run(BuildTerrain(), BuildEntries(), PlayerSpawn, MaxFrames, model, sums);
        int first = -1;
        int n = Math.Min(sums.Count, Checksums.Count);
        for (int i = 0; i < n; i++)
            if (sums[i] != Checksums[i]) { first = i + 1; break; }
        if (first < 0 && sums.Count != Checksums.Count) first = n + 1;
        bool resultSame = Result != null && Result.WinnerTeam == res.WinnerTeam && Result.Frames == res.Frames;
        return new ReplayReport(res, first, resultSame && first < 0);
    }

    public readonly record struct ReplayReport(ArenaResult Result, int FirstDivergence, bool Identical);

    // ── Views ────────────────────────────────────────────────────────────────

    public Vector2 PlayerSpawn => new(PlayerX, PlayerY);

    public ChunkMap BuildTerrain()
        => AsciiTerrain.FromAscii(Terrain.Ascii, Terrain.OriginTileX, Terrain.OriginTileY);

    public List<ArenaEntry> BuildEntries()
    {
        var list = new List<ArenaEntry>(Entries.Count);
        foreach (var e in Entries) list.Add(new ArenaEntry(e.Spec.ToSpec(), new Vector2(e.X, e.Y), e.Team));
        return list;
    }

    // A Stage the game can load: same terrain, same spawn, same populate as the arena,
    // so what you watch is the fight that was scored. `bots` receives the spawned
    // fighters in entry order for the fight HUD.
    public Stage ToStage(EnemyEntity[] bots = null, ICostModel model = null)
        => new()
        {
            Name        = "fight",
            Terrain     = BuildTerrain,
            PlayerSpawn = PlayerSpawn,
            Populate    = g => FighterArena.Populate(g, BuildEntries(), model, bots),
        };

    // ── Disk ─────────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    }

    public static FightRecord Load(string path)
        => JsonSerializer.Deserialize<FightRecord>(File.ReadAllText(path), JsonOpts)
           ?? throw new InvalidDataException("Empty fight record: " + path);

    public string Summary()
    {
        var names = new List<string>();
        foreach (var e in Entries) names.Add($"{e.Spec.Name} (team {e.Team})");
        string res = Result == null ? "unrecorded"
            : Result.WinnerTeam < 0 ? $"draw after {Result.Frames} frames"
            : $"team {Result.WinnerTeam} wins after {Result.Frames} frames";
        return $"{string.Join(" vs ", names)} on {Terrain.Name}: {res}";
    }
}

// The data form of a FighterSpec: every field, with the brain named by its ForgeBrain
// enum (a spec holds its brain as a delegate, which cannot be serialized) and the colour
// as bytes. Sprite is not stored — the compiler picks a stock one from the body. A
// package brain (Plans/FIGHTER_PACKAGE_GUIDE.md) is named by its package instead:
// `Package` holds IFighterPackage.Name and ToSpec rebuilds the brain from that package.
// Null for the stock brains, so older files read unchanged.
public sealed class FighterSpecDto
{
    public string Name          { get; set; } = "";
    public float  Health        { get; set; }
    public float  Strength      { get; set; }
    public float  Armor         { get; set; }
    public float  EnergyReserve { get; set; }
    public float  EnergyRegen   { get; set; }
    public float  GroundPower   { get; set; }
    public float  JumpImpulse   { get; set; }
    public float  Thrust        { get; set; }
    public bool   Cling         { get; set; }
    public bool   TargetMemory  { get; set; }
    public bool   Rooted        { get; set; }
    public float  Density       { get; set; } = 1f;
    public int    ReactionFrames { get; set; } = 6;
    public int    Sides         { get; set; }
    public int    Team          { get; set; }
    public byte[] Color         { get; set; } = new byte[3];
    public string Brain         { get; set; } = nameof(ForgeBrain.Closer);
    public string Package       { get; set; }
    public float  EngageRange        { get; set; }
    public float  StandoffRange      { get; set; }
    public float  HoverHeight        { get; set; }
    public float  AlertRange         { get; set; }
    public float  RetreatBelowHealth { get; set; }
    public int    PreferredAction    { get; set; }
    public List<ActionSpecDto> Actions { get; set; } = new();

    public static FighterSpecDto From(FighterSpec s) => new()
    {
        Name = s.Name,
        Health = s.Health, Strength = s.Strength, Armor = s.Armor,
        EnergyReserve = s.EnergyReserve, EnergyRegen = s.EnergyRegen,
        GroundPower = s.GroundPower, JumpImpulse = s.JumpImpulse, Thrust = s.Thrust,
        Cling = s.Cling, TargetMemory = s.TargetMemory, Rooted = s.Rooted,
        Density = s.Density, ReactionFrames = s.ReactionFrames, Sides = s.Sides, Team = s.Team,
        Color = new[] { s.Color.R, s.Color.G, s.Color.B },
        Brain = BrainOf(s).ToString(),
        Package = FighterPackages.BrainOwner(s),
        EngageRange = s.EngageRange, StandoffRange = s.StandoffRange, HoverHeight = s.HoverHeight,
        AlertRange = s.AlertRange, RetreatBelowHealth = s.RetreatBelowHealth, PreferredAction = s.PreferredAction,
        Actions = s.Actions.ConvertAll(ActionSpecDto.From),
    };

    public FighterSpec ToSpec()
    {
        var brain = Enum.TryParse<ForgeBrain>(Brain, ignoreCase: true, out var b) ? b : ForgeBrain.Closer;
        var s = new FighterSpec
        {
            Name = Name, Kind = EntityKind.FighterSlot0,
            Health = Health, Strength = Strength, Armor = Armor,
            EnergyReserve = EnergyReserve, EnergyRegen = EnergyRegen,
            GroundPower = GroundPower, JumpImpulse = JumpImpulse, Thrust = Thrust,
            Cling = Cling, TargetMemory = TargetMemory, Rooted = Rooted,
            Density = Density, ReactionFrames = ReactionFrames, Sides = Sides, Team = Team,
            Color = Color != null && Color.Length >= 3 ? new Color(Color[0], Color[1], Color[2]) : new Color(150, 30, 30),
            Brain = FighterForge.BrainFactory(brain),
            EngageRange = EngageRange, StandoffRange = StandoffRange, HoverHeight = HoverHeight,
            AlertRange = AlertRange, RetreatBelowHealth = RetreatBelowHealth, PreferredAction = PreferredAction,
        };
        foreach (var a in Actions) s.Actions.Add(a.ToSpec());
        if (!string.IsNullOrEmpty(Package))
        {
            var p = FighterPackages.Find(Package)
                    ?? throw new InvalidOperationException($"Fight file names package '{Package}', which this build does not contain.");
            s.Brain = p.Spec().Brain;
        }
        return s;
    }

    // Which bundled brain a spec's factory makes — instantiate it once and look. Unknown
    // controllers map to Closer, which is also what the forge does for its default.
    public static ForgeBrain BrainOf(FighterSpec s) => TimedController.Unwrap(s.Brain?.Invoke(s)) switch
    {
        FighterKiterBrain     => ForgeBrain.Kiter,
        FighterHoverDiveBrain => ForgeBrain.HoverDive,
        _                     => ForgeBrain.Closer,
    };
}

public sealed class ActionSpecDto
{
    public string  Kind          { get; set; } = "";
    public float   Windup        { get; set; }
    public float   Active        { get; set; }
    public float   Recovery      { get; set; }
    public float   MinRange      { get; set; }
    public float   MaxRange      { get; set; }
    public float   VerticalSlack { get; set; }
    public float   Damage        { get; set; }
    public float   DamageMax     { get; set; }
    public float   KnockbackX    { get; set; }
    public float   KnockbackY    { get; set; }
    public float   KnockbackMax  { get; set; }
    public float   Reach         { get; set; }
    public float   HalfWidth     { get; set; }
    public float   HalfHeight    { get; set; }
    public float   Speed         { get; set; }
    public float   FallSpeedMin  { get; set; }
    public float   FallSpeedRef  { get; set; }
    public int     Penetration   { get; set; }
    public string  Material      { get; set; } = "";
    public float   EnergyCost    { get; set; }
    public int     ActivePriority  { get; set; }
    public int     PassivePriority { get; set; }

    public static ActionSpecDto From(ActionSpec a) => new()
    {
        Kind = a.Kind.ToString(),
        Windup = a.Windup, Active = a.Active, Recovery = a.Recovery,
        MinRange = a.MinRange, MaxRange = a.MaxRange, VerticalSlack = a.VerticalSlack,
        Damage = a.Damage, DamageMax = a.DamageMax,
        KnockbackX = a.Knockback.X, KnockbackY = a.Knockback.Y, KnockbackMax = a.KnockbackMax,
        Reach = a.Reach, HalfWidth = a.HalfWidth, HalfHeight = a.HalfHeight,
        Speed = a.Speed, FallSpeedMin = a.FallSpeedMin, FallSpeedRef = a.FallSpeedRef,
        Penetration = a.Penetration, Material = a.Material.ToString(), EnergyCost = a.EnergyCost,
        ActivePriority = a.ActivePriority, PassivePriority = a.PassivePriority,
    };

    public ActionSpec ToSpec() => new()
    {
        Kind = Enum.TryParse<ActionKind>(Kind, ignoreCase: true, out var k) ? k : ActionKind.Special,
        Windup = Windup, Active = Active, Recovery = Recovery,
        MinRange = MinRange, MaxRange = MaxRange, VerticalSlack = VerticalSlack,
        Damage = Damage, DamageMax = DamageMax,
        Knockback = new Vector2(KnockbackX, KnockbackY), KnockbackMax = KnockbackMax,
        Reach = Reach, HalfWidth = HalfWidth, HalfHeight = HalfHeight,
        Speed = Speed, FallSpeedMin = FallSpeedMin, FallSpeedRef = FallSpeedRef,
        Penetration = Penetration,
        Material = Enum.TryParse<TileType>(Material, ignoreCase: true, out var m) ? m : default,
        EnergyCost = EnergyCost,
        ActivePriority = ActivePriority, PassivePriority = PassivePriority,
    };
}
