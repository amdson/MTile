using System;
using System.Collections.Generic;

namespace MTile;

// Static structure of a rig: one entry per bone, parents always stored before
// their children (topological order) so a single forward pass resolves world
// transforms. Render-only.
public readonly struct Bone
{
    public readonly string        Name;
    public readonly int           Parent;   // index into Skeleton.Bones, or -1 for a root
    public readonly float         Rotation; // rest-pose local rotation
    public readonly float         Length;   // drawn length along local +X (leaf orientation ticks)
    public readonly float?        MinRotation, MaxRotation; // optional local joint interval, radians

    public Bone(string name, int parent, float rotation, float length,
                float? minRotation = null, float? maxRotation = null)
    {
        if (minRotation.HasValue != maxRotation.HasValue || (minRotation.HasValue &&
            (!float.IsFinite(minRotation.Value) || !float.IsFinite(maxRotation.Value) ||
             minRotation.Value > maxRotation.Value || maxRotation.Value - minRotation.Value > MathF.Tau)))
            throw new ArgumentException($"Bone '{name}' needs a finite, ordered joint interval of at most one turn.");
        Name = name; Parent = parent; Rotation = rotation; Length = length; 
        MinRotation = minRotation; MaxRotation = maxRotation;
    }

    // The interval on the same angular branch as the pose. Clips may serialize equivalent
    // angles several turns apart; that must not change the joint's permitted bend.
    public bool RotationBounds(float angle, out float min, out float max)
    {
        min = float.NegativeInfinity; max = float.PositiveInfinity;
        if (!MinRotation.HasValue) return false;
        float mid = (MinRotation.Value + MaxRotation.Value) * .5f;
        float turn = MathF.Round((angle - mid) / MathF.Tau) * MathF.Tau;
        min = MinRotation.Value + turn; max = MaxRotation.Value + turn;
        return true;
    }

    public bool IsRoot => Parent < 0;
}

public sealed class Skeleton
{
    // Logical rig name (matches the Skeletons/<Name>.json file an authored rig was
    // loaded from). Animation clips reference rigs by this name, and CharacterAnimator
    // refuses clips whose AnimationDocument.Skeleton doesn't match the rig it owns.
    public readonly string Name;
    public readonly Bone[] Bones;
    // Shared anatomical NAMED POINTS (EndpointResolver): stable ids + roles for locations on
    // the rig (the left/right support points at the lower legs' ends). Empty when none.
    public readonly IReadOnlyList<NamedPoint> Points;
    private readonly Dictionary<string, int> _byName;

    public Skeleton(string name, Bone[] bones) : this(name, bones, null) { }

    public Skeleton(string name, Bone[] bones, IReadOnlyList<NamedPoint> points)
    {
        Name  = name  ?? throw new ArgumentNullException(nameof(name));
        Bones = bones ?? throw new ArgumentNullException(nameof(bones));
        Points = points ?? Array.Empty<NamedPoint>();
        _byName = new Dictionary<string, int>(bones.Length);
        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i].Parent >= i)
                throw new ArgumentException(
                    $"Bone '{bones[i].Name}' (index {i}) has parent {bones[i].Parent}; " +
                    "parents must be ordered before their children.");
            if (bones[i].Name != null) _byName[bones[i].Name] = i;
        }
    }

    public int Count => Bones.Length;

    // Returns the bone index, or -1 if no bone has that name.
    public int IndexOf(string name)
        => _byName.TryGetValue(name, out int i) ? i : -1;

    public SkeletonPose CreatePose() => new(this);

    // Return a new Skeleton with one bone appended. The parent already exists (so its
    // index precedes the new one), keeping the parent-before-child invariant; the Bones
    // array is readonly, so growing the rig means building a fresh Skeleton. Existing
    // poses must be recreated against the result (they're sized to the old bone count);
    // clips reference bones by name, so they're unaffected (the new bone sits at bind).
    public Skeleton WithBone(string name, int parentIndex, float rotation, float length = 0f,
                             float? minRotation = null, float? maxRotation = null)
    {
        if (parentIndex >= Bones.Length)
            throw new ArgumentOutOfRangeException(nameof(parentIndex), "Parent must be an existing bone.");
        var arr = new Bone[Bones.Length + 1];
        Array.Copy(Bones, arr, Bones.Length);
        arr[Bones.Length] = new Bone(name, parentIndex, rotation, length, minRotation, maxRotation);
        return new Skeleton(Name, arr, Points);
    }
}

// Convenience builder: add bones by name and get back their index to use as a
// parent for later bones. Enforces parent-before-child ordering as you go.
public sealed class SkeletonBuilder
{
    private readonly List<Bone> _bones = new();
    private readonly List<NamedPoint> _points = new();
    private readonly string     _name;

    public SkeletonBuilder(string name) { _name = name; }

    public void AddPoint(NamedPoint p) => _points.Add(p);

    public int Add(string name, int parent, float rotation, float length = 0f,
                   float? minRotation = null, float? maxRotation = null)
    {
        if (parent >= _bones.Count)
            throw new ArgumentOutOfRangeException(nameof(parent),
                "A parent bone must be added before its children.");
        _bones.Add(new Bone(name, parent, rotation, length, minRotation, maxRotation));
        return _bones.Count - 1;
    }

    // A root bone (no parent).
    public int AddRoot(string name, float rotation, float length = 0f,
                       float? minRotation = null, float? maxRotation = null)
        => Add(name, -1, rotation, length, minRotation, maxRotation);

    public Skeleton Build() => new(_name, _bones.ToArray(), _points.Count > 0 ? _points.ToArray() : null);
}
