using Godot;
using System.Collections.Generic;

// A jagged arcane chain-lightning that REVEALS itself jump-by-jump (so you see it travel + chain outward) and re-randomizes
// its jaggedness every frame (writhe), then fades. Damage is applied instantly by FireArcaneChain — this is the visual only,
// slowed down so the chain reads. Rebuilds its little cylinder segments each frame (immediate Free) — short-lived, one at a time.
public partial class ArcaneLightning : Node3D
{
    private Vector3[] _pts;
    private float _charge, _t;
    private const float RevealDur = 0.42f, Hold = 0.16f, Fade = 0.22f;

    public void Init(List<Vector3> pts, float charge) { _pts = pts.ToArray(); _charge = charge; }

    public override void _Process(double delta)
    {
        if (_pts == null || _pts.Length < 2) { QueueFree(); return; }
        _t += (float)delta;
        if (_t >= RevealDur + Hold + Fade) { QueueFree(); return; }

        // clear last frame's segments (immediate, so there's no one-frame doubling)
        var kids = new List<Node>();
        foreach (var c in GetChildren()) kids.Add(c);
        foreach (var c in kids) c.Free();

        int jumps = _pts.Length - 1;
        float visible = Mathf.Clamp(_t / RevealDur, 0f, 1f) * jumps;   // how many jumps are drawn (fractional leading jump = travel)
        float fade = _t <= RevealDur + Hold ? 1f : 1f - Mathf.Clamp((_t - RevealDur - Hold) / Fade, 0f, 1f);
        var col = DamageTypes.Col(DamageType.Arcane).Lerp(Colors.White, 0.3f);
        float thick = (0.07f + _charge * 0.1f) * (0.6f + 0.4f * fade);
        for (int j = 0; j < jumps; j++)
        {
            if (visible <= j) break;
            float legFrac = Mathf.Clamp(visible - j, 0f, 1f);
            Vector3 a = _pts[j];
            Vector3 b = legFrac < 1f ? _pts[j].Lerp(_pts[j + 1], legFrac) : _pts[j + 1];   // the leading jump grows out
            DrawJaggedLeg(a, b, thick, col, fade);
        }
    }

    private void DrawJaggedLeg(Vector3 a, Vector3 b, float thick, Color col, float fade)
    {
        float len = (b - a).Length(); if (len < 0.1f) return;
        Vector3 dir = (b - a) / len;
        var perp = dir.Cross(Vector3.Up); if (perp.LengthSquared() < 1e-4f) perp = Vector3.Right; perp = perp.Normalized();
        var perp2 = dir.Cross(perp).Normalized();
        int n = Mathf.Clamp(Mathf.RoundToInt(len / 1.3f), 2, 12);
        Vector3 prev = a;
        for (int s = 1; s <= n; s++)
        {
            Vector3 p = s == n ? b : a + dir * (len * s / n) + perp * ((GD.Randf() - 0.5f) * 1.0f) + perp2 * ((GD.Randf() - 0.5f) * 1.0f);
            Seg(prev, p, thick, col, fade); prev = p;
        }
    }

    private void Seg(Vector3 a, Vector3 b, float thick, Color col, float fade)
    {
        var d = b - a; float len = d.Length(); if (len < 0.02f) return;
        var dir = d / len;
        var node = new Node3D(); AddChild(node);
        node.GlobalPosition = (a + b) * 0.5f;
        node.LookAt(node.GlobalPosition + dir, Mathf.Abs(dir.Y) > 0.98f ? Vector3.Forward : Vector3.Up);
        var mi = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = thick * 1.4f, BottomRadius = thick * 1.4f, Height = 1f, RadialSegments = 6 }, MaterialOverride = Game.ArcaneEnergyMat(), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        mi.RotationDegrees = new Vector3(90, 0, 0); mi.Scale = new Vector3(1f, len, 1f);
        node.AddChild(mi);
    }
}
