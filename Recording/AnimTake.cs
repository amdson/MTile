using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Xna.Framework;

namespace MTile;

// A recorded gameplay "take" persisted to disk for offline animation inspection
// (Plans/ANIM_TAKE_VIEWER_PLAN.md). It does NOT store poses or sim state — it stores the
// per-frame CharacterAnimSample stream (the animator's complete one-way input boundary)
// plus drawable terrain context, and the viewer RE-RUNS a CharacterAnimator over it.
// That sidesteps SimSnapshot serialization entirely and lets the viewer expose any
// solver internal (and re-solve the same take under an edited anim_solver_config.json).
//
// Plain DTOs with no XNA types in the serialized shape, so the format doesn't depend on
// which framework variant (DesktopGL/KNI) wrote it.
public sealed class AnimTake
{
    public int   Version       { get; set; } = 1;
    public float SkeletonScale { get; set; }
    public float PlayerRadius  { get; set; }
    public List<SampleDto>      Frames        { get; set; } = new();
    // Sparse non-empty tile lists, stored only when the terrain actually changed —
    // a typical take has a handful of states (one per broken/placed tile event).
    // Each frame indexes into this via SampleDto.Terrain.
    public List<List<TileCell>> TerrainStates { get; set; } = new();

    public sealed class TileCell
    {
        public int  X { get; set; }   // global tile coords (gtx/gty)
        public int  Y { get; set; }
        public byte S { get; set; }   // TileState
        public byte T { get; set; }   // TileType
    }

    public sealed class PinDto
    {
        public string Bone { get; set; }
        public float  Tx   { get; set; }
        public float  Ty   { get; set; }
    }

    public sealed class SurfaceDto
    {
        public float Px { get; set; }
        public float Py { get; set; }
        public float Nx { get; set; }
        public float Ny { get; set; }
        public float Margin { get; set; }
    }

    // One frame's CharacterAnimSample, field for field.
    public sealed class SampleDto
    {
        public float  Px { get; set; }
        public float  Py { get; set; }
        public float  Vx { get; set; }
        public float  Vy { get; set; }
        public int    Facing   { get; set; }
        public bool   Grounded { get; set; }
        public string State    { get; set; }
        public int    Tag      { get; set; }
        public string Action   { get; set; }
        public float  Dt       { get; set; }
        public float  ActionTime     { get; set; }
        public float  ActionDuration { get; set; }
        public float  MoveProgress   { get; set; }
        public List<PinDto>     Pins     { get; set; }   // null = none
        public List<SurfaceDto> Surfaces { get; set; }   // null = none
        public bool   HasGrip { get; set; }
        public float  Gx { get; set; }
        public float  Gy { get; set; }
        public bool   HasAim { get; set; }
        public float  Ax { get; set; }
        public float  Ay { get; set; }
        public int    Terrain { get; set; }   // index into TerrainStates

        public static SampleDto From(in CharacterAnimSample s, int terrainIndex)
        {
            var d = new SampleDto
            {
                Px = s.Position.X, Py = s.Position.Y, Vx = s.Velocity.X, Vy = s.Velocity.Y,
                Facing = s.Facing, Grounded = s.Grounded, State = s.MovementState,
                Tag = (int)s.Tag, Action = s.Action, Dt = s.Dt,
                ActionTime = s.ActionTime, ActionDuration = s.ActionDuration,
                MoveProgress = s.MovementProgress,
                HasGrip = s.HasGrip, Gx = s.GripTarget.X, Gy = s.GripTarget.Y,
                HasAim = s.HasAim, Ax = s.AimDir.X, Ay = s.AimDir.Y,
                Terrain = terrainIndex,
            };
            if (s.Pins is { Length: > 0 })
            {
                d.Pins = new List<PinDto>(s.Pins.Length);
                foreach (var p in s.Pins)
                    d.Pins.Add(new PinDto { Bone = p.Bone, Tx = p.Target.X, Ty = p.Target.Y });
            }
            if (s.Surfaces is { Length: > 0 })
            {
                d.Surfaces = new List<SurfaceDto>(s.Surfaces.Length);
                foreach (var sf in s.Surfaces)
                    d.Surfaces.Add(new SurfaceDto { Px = sf.Point.X, Py = sf.Point.Y, Nx = sf.Normal.X, Ny = sf.Normal.Y, Margin = sf.Margin });
            }
            return d;
        }

        public CharacterAnimSample ToSample()
        {
            ExternalPin[] pins = null;
            if (Pins is { Count: > 0 })
            {
                pins = new ExternalPin[Pins.Count];
                for (int i = 0; i < Pins.Count; i++)
                    pins[i] = new ExternalPin(Pins[i].Bone, new Vector2(Pins[i].Tx, Pins[i].Ty));
            }
            SolverSurface[] surfaces = null;
            if (Surfaces is { Count: > 0 })
            {
                surfaces = new SolverSurface[Surfaces.Count];
                for (int i = 0; i < Surfaces.Count; i++)
                    surfaces[i] = new SolverSurface(new Vector2(Surfaces[i].Px, Surfaces[i].Py),
                                                    new Vector2(Surfaces[i].Nx, Surfaces[i].Ny), Surfaces[i].Margin);
            }
            return new CharacterAnimSample(
                new Vector2(Px, Py), new Vector2(Vx, Vy), Facing, Grounded, State, Action, Dt,
                ActionTime, ActionDuration, MoveProgress, pins, surfaces,
                HasGrip, new Vector2(Gx, Gy), HasAim, new Vector2(Ax, Ay), (AnimTag)Tag);
        }
    }

    // ── building from recorded frames ───────────────────────────────────────────

    // Append one frame: the sample plus this frame's dense terrain, deduped against the
    // last stored terrain state (append a new state only when a cell differs).
    public void AddFrame(in CharacterAnimSample sample, DenseTerrainCapture terrain)
    {
        var tiles = NonEmptyTiles(terrain);
        int idx = TerrainStates.Count - 1;
        if (idx < 0 || !SameTiles(TerrainStates[idx], tiles))
        {
            TerrainStates.Add(tiles);
            idx = TerrainStates.Count - 1;
        }
        Frames.Add(SampleDto.From(sample, idx));
    }

    private static List<TileCell> NonEmptyTiles(DenseTerrainCapture cap)
    {
        var list = new List<TileCell>();
        if (cap?.Chunks == null) return list;
        foreach (var cc in cap.Chunks)
            for (int tx = 0; tx < Chunk.Size; tx++)
                for (int ty = 0; ty < Chunk.Size; ty++)
                {
                    int i = tx * Chunk.Size + ty;
                    if (cc.State[i] == TileState.Empty) continue;
                    list.Add(new TileCell
                    {
                        X = cc.Pos.X * Chunk.Size + tx,
                        Y = cc.Pos.Y * Chunk.Size + ty,
                        S = (byte)cc.State[i],
                        T = (byte)cc.Type[i],
                    });
                }
        return list;
    }

    private static bool SameTiles(List<TileCell> a, List<TileCell> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i].X != b[i].X || a[i].Y != b[i].Y || a[i].S != b[i].S || a[i].T != b[i].T)
                return false;
        return true;
    }

    // ── disk ────────────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions _json = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // No WriteIndented: takes are thousands of frames; keep the file dense.
    };

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        File.WriteAllText(path, JsonSerializer.Serialize(this, _json));
    }

    public static AnimTake Load(string path)
        => JsonSerializer.Deserialize<AnimTake>(File.ReadAllText(path), _json)
           ?? throw new InvalidDataException($"Take file unreadable: {path}");

    // Repo-root Takes/ directory (walks up from the running binary, same sentinel as
    // SkeletonExamples.FindSkeletonsDir); falls back beside the binary.
    public static string DefaultDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            if (File.Exists(Path.Combine(d.FullName, "MTile.sln")))
                return Path.Combine(d.FullName, "Takes");
            d = d.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "Takes");
    }
}
