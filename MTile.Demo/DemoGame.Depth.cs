using System;
using ImGuiNET;
using Microsoft.Xna.Framework;
using MTile;
using NVec2 = System.Numerics.Vector2;

namespace MTileDemo;

// THE DEPTH VIEW — a top-down look at the selected bone (PoseDepth). The panel's horizontal
// axis is the bone's own screen direction, its vertical axis is depth: the screen plane is
// the horizontal line through the joint, and the viewer stands BELOW the panel (down =
// toward the viewer, up = away). The bone is drawn from the joint at its true length, so its
// tip rides the circle; dragging the tip round the circle sets its tilt, which is written as
// this keyframe's Stretch (= cos tilt) and DepthSide (the sign of sin tilt).
//
// The descendants are drawn too, projected into the same plane, so you can see what the rest
// of the limb is doing in depth. "Turn with children" makes a drag carry them along as one
// rigid limb (PoseDepth.Turn); off, only the selected bone tilts and its children keep their
// own foreshortening. Either way the data stays flat per-bone Stretch + side.
//
// Edits apply to the active keyframe only; on an interpolated frame the view is read-only.
public sealed partial class DemoGame
{
    private bool _showDepth = true;
    private bool _depthTurnChildren = true;
    private bool _depthDragging;

    private const float DepthPanel = 220f;

    private void DepthWindow(float menuH, float screenW)
    {
        if (!_showDepth) return;
        ImGui.SetNextWindowPos(new NVec2(screenW - RightW - DepthPanel - 40f, menuH + 10f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Depth view", ref _showDepth, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        { ImGui.End(); return; }

        int bone = _selectedEndpoint;
        var doc = Doc;
        if (doc == null || bone < 0 || bone >= _skeleton.Count || _skeleton.Bones[bone].Parent < 0)
        {
            ImGui.TextDisabled("click a joint to pick its bone");
            ImGui.End(); _depthDragging = false; return;
        }
        var b = _skeleton.Bones[bone];
        if (b.StretchLength <= 1e-3f)
        {
            ImGui.TextDisabled($"{b.Name}: zero length, nothing to tilt");
            ImGui.End(); _depthDragging = false; return;
        }

        bool editable = _activeKey >= 0 && !_playing;
        int?[] sides = SidesForPose();
        float s = PoseDepth.StretchOf(_pose, bone);
        float tilt = PoseDepth.Tilt(s, sides[bone]);

        ImGui.TextUnformatted(b.Name);
        ImGui.SameLine();
        ImGui.TextDisabled(editable ? "" : "(interpolated — select a key to edit)");
        ImGui.Checkbox("turn with children", ref _depthTurnChildren);

        // ── the panel ──
        var dl = ImGui.GetWindowDrawList();
        ImGui.InvisibleButton("##depth", new NVec2(DepthPanel, DepthPanel));
        bool hovered = ImGui.IsItemHovered();
        bool active  = ImGui.IsItemActive();
        var lo = ImGui.GetItemRectMin();
        var hi = ImGui.GetItemRectMax();
        var c  = new NVec2((lo.X + hi.X) * 0.5f, (lo.Y + hi.Y) * 0.5f);
        float R = DepthPanel * 0.36f;
        float px = R / b.StretchLength;   // panel pixels per rig unit

        dl.PushClipRect(lo, hi, true);
        dl.AddRectFilled(lo, hi, Col(26, 28, 34));
        dl.AddLine(new NVec2(lo.X + 6f, c.Y), new NVec2(hi.X - 6f, c.Y), Col(70, 75, 88), 1f);   // the screen plane
        dl.AddText(new NVec2(lo.X + 6f, c.Y - 16f), Col(90, 96, 110), "screen");
        dl.AddText(new NVec2(lo.X + 6f, lo.Y + 4f), Col(90, 96, 110), "away");
        dl.AddText(new NVec2(lo.X + 6f, hi.Y - 18f), Col(150, 160, 180), "toward viewer");
        dl.AddCircle(c, R, Col(60, 66, 80), 48, 1f);
        // The rest view: where the drawn Length sits (the art's own foreshortening).
        if (b.TrueLength.HasValue)
        {
            float rest = b.Length / b.StretchLength * R;
            dl.AddLine(new NVec2(c.X + rest, c.Y - 5f), new NVec2(c.X + rest, c.Y + 5f), Col(120, 130, 150), 1f);
        }

        // The bone's neighbourhood in 3D, in this plane: e1 = its screen axis, depth = z.
        var joints = PoseDepth.Joints(_pose, sides);
        float[] axis = PoseDepth.AxisAngles(_pose);
        var e1 = new Vector3(MathF.Cos(axis[bone]), MathF.Sin(axis[bone]), 0f);
        Vector3 origin = joints[b.Parent];
        NVec2 P(Vector3 q) { var r = q - origin; return new NVec2(c.X + Vector3.Dot(r, e1) * px, c.Y + r.Z * px); }

        // Parent (dim), then descendants (faint), then the bone itself.
        var pb = _skeleton.Bones[b.Parent];
        Vector3 grand = pb.Parent >= 0 ? joints[pb.Parent] : Vector3.Zero;
        dl.AddLine(P(grand), P(origin), Col(80, 86, 100), 2f);
        for (int i = bone + 1; i < _skeleton.Count; i++)
            if (PoseDepth.IsDescendant(_skeleton, i, bone))
            {
                dl.AddLine(P(joints[_skeleton.Bones[i].Parent]), P(joints[i]), Col(120, 170, 210, 170), 2f);
                dl.AddCircleFilled(P(joints[i]), 2.5f, Col(120, 170, 210, 170));
            }
        var tip = P(joints[bone]);
        dl.AddLine(c, tip, Col(255, 200, 120), 3f);
        dl.AddCircleFilled(c, 3f, Col(200, 205, 215));
        dl.AddCircleFilled(tip, active ? 6f : 5f, hovered || active ? Col(255, 235, 170) : Col(255, 200, 120));
        dl.PopClipRect();

        // ── drag: tilt = the cursor's angle about the joint ──
        if (active && editable)
        {
            var m = ImGui.GetIO().MousePos;
            float dx = m.X - c.X, dy = m.Y - c.Y;
            if (dx * dx + dy * dy > 4f)
            {
                _depthDragging = true;
                PoseDepth.Turn(_pose, bone, MathF.Atan2(dy, dx), _depthTurnChildren, sides);
                WriteDepthKey(sides);
            }
        }
        else _depthDragging = false;

        // ── readout ──
        s = PoseDepth.StretchOf(_pose, bone);
        tilt = PoseDepth.Tilt(s, sides[bone]);
        string side = sides[bone] == 1 ? "toward viewer" : sides[bone] == -1 ? "away" : "in plane";
        ImGui.TextUnformatted($"stretch {s:0.000}   tilt {MathHelper.ToDegrees(tilt):0}°   {side}");
        if (MathF.Abs(s) > 1.001f)
            ImGui.TextColored(Rgba(255, 140, 90), "over-length key (|stretch| > 1): no depth angle — a drag replaces it");
        if (editable)
        {
            if (ImGui.Button("In plane"))
            {
                PoseDepth.Turn(_pose, bone, s < 0f ? MathF.PI : 0f, _depthTurnChildren, sides);
                WriteDepthKey(sides);
            }
            ImGui.SameLine();
            if (ImGui.Button("Flip side") && sides[bone] != null)
            {
                PoseDepth.Turn(_pose, bone, -tilt, _depthTurnChildren, sides);
                WriteDepthKey(sides);
            }
        }
        ImGui.End();
    }

    // DepthSide per bone index for the pose on screen: the active key's, else the key in
    // effect at the playhead (sides snap at keys; they don't blend).
    private int?[] SidesForPose()
    {
        var sides = new int?[_skeleton.Count];
        var doc = Doc;
        if (doc == null) return sides;
        var kf = _activeKey >= 0 ? doc.Keyframes[_activeKey]
                                 : doc.Keyframes.FindLast(k => k.Time <= _scrubT + 1e-5f) ?? (doc.Keyframes.Count > 0 ? doc.Keyframes[0] : null);
        if (kf?.Bones == null) return sides;
        foreach (var e in kf.Bones)
        {
            int i = _skeleton.IndexOf(e.Bone);
            if (i >= 0) sides[i] = e.DepthSide;
        }
        return sides;
    }

    // Re-capture the active key from the pose, with the edited sides.
    private void WriteDepthKey(int?[] sides)
    {
        var kf = Doc.Keyframes[_activeKey];
        var bones = PoseData.Capture(_pose);
        foreach (var e in bones)
        {
            int i = _skeleton.IndexOf(e.Bone);
            // A bone back in the plane (no Stretch) has no side to keep.
            if (i >= 0) e.DepthSide = e.Stretch.HasValue ? sides[i] : null;
        }
        kf.Bones = bones;
        _dirty = true;
    }
}
