using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// Entity art: what an entity is DOING, described once in a renderer-neutral vocabulary
// (VisualState), and any number of ways to draw it that consume that description
// (IStateDrivenVisual). Sprite sheets are the first; an MLS-skinned figure (the player's
// deformation pipeline) or a vector figure can implement the same interface later and be
// picked per entity in the same catalog. Everything here is render-side: it reads public
// entity state after the sim has stepped and writes only Sprite objects.

public enum VisualPhase { None, Windup, Active, Recovery }

public struct VisualState
{
    public string Action;           // the running action's name ("Lunge"), null when idle
    public VisualPhase Phase;
    public float PhaseProgress;     // 0..1 through the current phase
    public float ActionProgress;    // 0..1 through the whole action
    public int Facing;              // ±1
    public bool Moving;
    public bool Hurt;               // staggered
}

public interface IStateDrivenVisual
{
    void SetState(in VisualState state);
}

// Picks a sheet tag from a VisualState and keeps its clock. Pure (no textures) so the
// tag fallback rules are unit-testable. Candidates, first tag the sheet has wins:
//   "<action>_<phase>"  e.g. "lunge_windup"   — timed across that phase
//   "<action>"          e.g. "lunge"          — timed across the whole action
//   "<phase>"           windup | attack | recover — timed across that phase
//   "hurt", "move", "idle"                     — loop on their authored durations
// A sheet with none of them plays every frame on a loop. Phase-timed tags stretch to the
// gameplay timing, so an enemy's art can never drift from its hitbox.
public sealed class SheetAnimator
{
    private enum Timing { Loop, Phase, Action }

    private readonly SpriteSheetData _sheet;
    private string _tag;
    private int[] _sequence;
    private Timing _timing;
    private float _clock, _progress;

    public SheetAnimator(SpriteSheetData sheet)
    {
        _sheet = sheet;
        _sequence = sheet.Sequence(null);
    }

    public string Tag => _tag;

    public void Apply(in VisualState s)
    {
        var (tag, timing) = Resolve(in s);
        if (tag != _tag)
        {
            _tag = tag; _timing = timing; _clock = 0f;
            _sequence = _sheet.Sequence(tag);
        }
        _progress = timing == Timing.Action ? s.ActionProgress : s.PhaseProgress;
    }

    public void Advance(float dt) => _clock += dt;

    public int Frame => _timing == Timing.Loop
        ? _sheet.FrameAt(_sequence, _clock, loop: true)
        : _sheet.FrameAtProgress(_sequence, _progress);

    private (string, Timing) Resolve(in VisualState s)
    {
        var tags = _sheet.Tags;
        if (s.Action != null && s.Phase != VisualPhase.None)
        {
            string phase = s.Phase switch
            {
                VisualPhase.Windup => "windup", VisualPhase.Active => "attack", _ => "recover",
            };
            string action = s.Action.EndsWith("Action", StringComparison.Ordinal)
                ? s.Action[..^"Action".Length] : s.Action;
            if (tags.ContainsKey(action + "_" + phase)) return (action + "_" + phase, Timing.Phase);
            if (tags.ContainsKey(action)) return (action, Timing.Action);
            if (tags.ContainsKey(phase)) return (phase, Timing.Phase);
        }
        if (s.Hurt && tags.ContainsKey("hurt")) return ("hurt", Timing.Loop);
        if (s.Moving && tags.ContainsKey("move")) return ("move", Timing.Loop);
        if (tags.ContainsKey("idle")) return ("idle", Timing.Loop);
        return (null, Timing.Loop);
    }
}

// A sprite drawn from a sheet. Wraps the entity's original sprite rather than replacing
// it, so entity code holding its own reference (Prism's figure) keeps working; the
// original is simply not drawn. Draws inside the world SpriteBatch pass like any Sprite.
public sealed class SheetSprite : Sprite, IStateDrivenVisual
{
    public readonly Sprite Original;
    private readonly SpriteSheet _sheet;
    private readonly SheetAnimator _anim;
    private readonly float _pixelSize;
    private readonly bool _flipWithFacing;
    private readonly Vector2 _offset;
    private int _facing = 1;

    public SheetSprite(SpriteSheet sheet, float pixelSize, bool flipWithFacing, Sprite original,
                       Vector2 offset = default)
    {
        _sheet = sheet;
        _anim = new SheetAnimator(sheet.Data);
        _pixelSize = pixelSize;
        _flipWithFacing = flipWithFacing;
        _offset = offset;
        Original = original;
        if (original != null)
        {
            Position = original.Position; Rotation = original.Rotation;
            Scale = original.Scale; Tint = original.Tint;
        }
    }

    public string CurrentTag => _anim.Tag;

    public void SetState(in VisualState state)
    {
        _anim.Apply(in state);
        if (state.Facing != 0) _facing = state.Facing;
    }

    public override void Update(float dt) => _anim.Advance(dt);

    public override void Draw(DrawContext ctx)
    {
        if (!Visible) return;
        int frame = _anim.Frame;
        float unit = _pixelSize / _sheet.Data.Scale * Scale;
        bool flip = _flipWithFacing && _facing < 0;
        float c = MathF.Cos(Rotation), s = MathF.Sin(Rotation);
        var off = new Vector2(flip ? -_offset.X : _offset.X, _offset.Y) * Scale;
        var at = Position + new Vector2(off.X * c - off.Y * s, off.X * s + off.Y * c);
        SheetBatch.Draw(ctx.SpriteBatch, _sheet, frame, at, Rotation, unit, flip, Tint);
        // Hit flash: the frame's white silhouette over it, so the blend is a true lerp to
        // white (premultiplied: white·a·k + dst·(1 − a·k)) — a multiply tint can't brighten.
        if (Flash > 0f)
            SheetBatch.Draw(ctx.SpriteBatch, _sheet, frame, at, Rotation, unit, flip,
                            Color.White * MathHelper.Clamp(Flash, 0f, 1f), _sheet.Silhouette);
    }
}

// One catalog entry: which renderer an entity kind uses, and its parameters.
public sealed class EntityArtDefinition
{
    public string Type { get; set; } = "Sheet";   // the only renderer so far
    public string Sheet { get; set; }
    public float PixelSize { get; set; } = 0.1f;  // world px per ORIGINAL art pixel
    public bool FlipWithFacing { get; set; } = true;   // art is drawn facing right
    // World px from the entity's position (its body center) to the sheet's pivot; X
    // mirrors with facing. A walker's sheet pivots at its feet: OffsetY = half-height.
    public float OffsetX { get; set; }
    public float OffsetY { get; set; }
}

// Swaps catalogued entity kinds onto their art and feeds every state-driven visual its
// VisualState each rendered frame. Catalog: Assets/Sprites/entities.json, keyed by
// EntityKind name. A kind with no entry, or whose art fails to load, keeps its
// procedural sprite — so art can land one enemy at a time.
public sealed class EntityArtSystem
{
    public const string AssetDirectory = "Assets/Sprites";

    private readonly SpriteSheetLibrary _sheets;
    private readonly Dictionary<string, EntityArtDefinition> _catalog;
    private readonly HashSet<string> _warned = new();

    public EntityArtSystem(GraphicsDevice device, string directory = AssetDirectory)
    {
        _sheets = new SpriteSheetLibrary(device, directory);
        _catalog = LoadCatalog(Path.Combine(directory, "entities.json"));
    }

    public static Dictionary<string, EntityArtDefinition> ParseCatalog(string json)
        => new(JsonSerializer.Deserialize<Dictionary<string, EntityArtDefinition>>(json, new JsonSerializerOptions
           {
               ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
           }) ?? new(), StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, EntityArtDefinition> LoadCatalog(string path)
    {
        try
        {
            using var stream = TitleContent.TryOpenRead(path);
            if (stream == null) return new(StringComparer.OrdinalIgnoreCase);
            using var reader = new StreamReader(stream);
            return ParseCatalog(reader.ReadToEnd());
        }
        catch (Exception e)
        {
            Console.WriteLine($"Entity art catalog '{path}': {e.Message}");
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    // After the entities' own SyncSprite, before Sprite.Update.
    public void Sync(Entity e)
    {
        if (_catalog.Count == 0) return;
        if (e.Sprite is not IStateDrivenVisual) TryWrap(e);
        if (e.Sprite is IStateDrivenVisual v) v.SetState(Describe(e));
    }

    private void TryWrap(Entity e)
    {
        string kind = e.Kind.ToString();
        if (!_catalog.TryGetValue(kind, out var def)) return;
        if (!string.Equals(def.Type, "Sheet", StringComparison.OrdinalIgnoreCase))
        {
            if (_warned.Add(kind)) Console.WriteLine($"Entity art '{kind}': unknown Type '{def.Type}'.");
            return;
        }
        var sheet = _sheets.Get(def.Sheet);   // logs its own failure once
        if (sheet == null || !(def.PixelSize > 0f)) return;
        // Wrapped sprites start where the entity is; SyncSprite moves them from here on.
        e.Sprite = new SheetSprite(sheet, def.PixelSize, def.FlipWithFacing, e.Sprite,
                                   new Vector2(def.OffsetX, def.OffsetY)) { Position = e.Body.Position };
    }

    // The renderer-neutral read of an entity. Enemies report their action phase off the
    // Windup/Active/Recovery durations every EnemyActionState stamps into its vars.
    public static VisualState Describe(Entity e)
    {
        var s = new VisualState { Facing = 1, Moving = MathF.Abs(e.Body.Velocity.X) > 15f };
        if (e is not EnemyEntity enemy) return s;
        s.Facing = enemy.Facing == 0 ? 1 : enemy.Facing;
        s.Hurt = enemy.IsStaggered;
        if (enemy.CurrentActionIndex < 0) return s;
        s.Action = enemy.CurrentActionName;
        ref readonly var v = ref enemy.CurrentActionVars;
        float t = enemy.CurrentActionTime;
        float w = v.WindupDuration, a = v.ActiveDuration, r = v.RecoveryDuration, total = w + a + r;
        if (!(total > 0f)) return s;
        s.ActionProgress = Math.Clamp(t / total, 0f, 1f);
        (s.Phase, s.PhaseProgress) =
            t < w     ? (VisualPhase.Windup,   t / w) :
            t < w + a ? (VisualPhase.Active,   (t - w) / a) :
                        (VisualPhase.Recovery, r > 0f ? Math.Clamp((t - w - a) / r, 0f, 1f) : 1f);
        return s;
    }
}
