using System;
using System.Collections.Generic;

namespace MTile;

// Static structure of a rig: one entry per bone, parents always stored before
// their children (topological order) so a single forward pass resolves world
// transforms. Immutable once built — the per-frame moving parts live in
// SkeletonPose. Render-only.
public readonly struct Bone
{
    public readonly string        Name;
    public readonly int           Parent;   // index into Skeleton.Bones, or -1 for a root
    public readonly BoneTransform Bind;     // rest-pose local transform
    public readonly float         Length;   // drawn length along local +X (leaf orientation ticks)

    public Bone(string name, int parent, BoneTransform bind, float length)
    {
        Name = name; Parent = parent; Bind = bind; Length = length;
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
    private readonly Dictionary<string, int> _byName;

    public Skeleton(string name, Bone[] bones)
    {
        Name  = name  ?? throw new ArgumentNullException(nameof(name));
        Bones = bones ?? throw new ArgumentNullException(nameof(bones));
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
    public Skeleton WithBone(string name, int parentIndex, BoneTransform bind, float length = 0f)
    {
        if (parentIndex >= Bones.Length)
            throw new ArgumentOutOfRangeException(nameof(parentIndex), "Parent must be an existing bone.");
        var arr = new Bone[Bones.Length + 1];
        Array.Copy(Bones, arr, Bones.Length);
        arr[Bones.Length] = new Bone(name, parentIndex, bind, length);
        return new Skeleton(Name, arr);
    }
}

// Convenience builder: add bones by name and get back their index to use as a
// parent for later bones. Enforces parent-before-child ordering as you go.
public sealed class SkeletonBuilder
{
    private readonly List<Bone> _bones = new();
    private readonly string     _name;

    public SkeletonBuilder(string name) { _name = name; }

    public int Add(string name, int parent, BoneTransform bind, float length = 0f)
    {
        if (parent >= _bones.Count)
            throw new ArgumentOutOfRangeException(nameof(parent),
                "A parent bone must be added before its children.");
        _bones.Add(new Bone(name, parent, bind, length));
        return _bones.Count - 1;
    }

    // A root bone (no parent).
    public int AddRoot(string name, BoneTransform bind, float length = 0f)
        => Add(name, -1, bind, length);

    public Skeleton Build() => new(_name, _bones.ToArray());
}
