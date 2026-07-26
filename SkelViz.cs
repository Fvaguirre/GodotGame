using Godot;
using System.Collections.Generic;

// SkelViz.cs — dev skeleton visualizer. Draws a fixed-WORLD-size bright dot at every bone's true world position each
// frame (TopLevel so it ignores the model's scale — which is what made scale-attached dots vanish). Bones drawn on top of
// the mesh (no depth test) so the whole rig is visible through the robe. Created by ModelAssets.ShowSkeleton.
//
// It also HIDES the model's meshes while active (so the bright bones read cleanly with no robe/hat clutter) and restores
// them when the overlay is toggled off (freed → _ExitTree).
public partial class SkelViz : Node3D
{
    private Skeleton3D _skel;
    private MeshInstance3D[] _dots;
    private StandardMaterial3D _mat;
    private float _t;
    private readonly List<MeshInstance3D> _hidden = new();

    // Hide every mesh under `modelRoot` (remember which we hid so we can restore only those on toggle-off).
    public void HideMesh(Node modelRoot)
    {
        CollectMeshes(modelRoot);
        foreach (var m in _hidden) m.Visible = false;
    }

    private void CollectMeshes(Node n)
    {
        if (n is MeshInstance3D mi && mi.Visible) _hidden.Add(mi);
        foreach (var c in n.GetChildren()) CollectMeshes(c);
    }

    public override void _ExitTree()
    {
        foreach (var m in _hidden)
            if (GodotObject.IsInstanceValid(m)) m.Visible = true;   // restore the model when the overlay turns off
    }

    public void Init(Skeleton3D skel)
    {
        _skel = skel;
        int n = skel.GetBoneCount();
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = new Color(1f, 0.12f, 0.9f),
            EmissionEnabled = true, Emission = new Color(1f, 0.12f, 0.9f), EmissionEnergyMultiplier = 2f,
            NoDepthTest = true,
        };
        _mat = mat;
        var mesh = new SphereMesh { Radius = 0.07f, Height = 0.14f, RadialSegments = 6, Rings = 4 };
        _dots = new MeshInstance3D[n];
        for (int i = 0; i < n; i++)
        {
            var d = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, TopLevel = true };
            AddChild(d);
            d.AddChild(new Label3D
            {
                Text = i.ToString(),
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                NoDepthTest = true,
                FontSize = 40,
                PixelSize = 0.006f,
                Modulate = new Color(1f, 1f, 0.35f),
                OutlineModulate = new Color(0f, 0f, 0f, 1f),
                OutlineSize = 14,
                RenderPriority = 12,
                Position = new Vector3(0f, 0.12f, 0f),
            });
            _dots[i] = d;
        }
    }

    public override void _Process(double delta)
    {
        if (_skel == null || !GodotObject.IsInstanceValid(_skel) || _dots == null) return;
        _t += (float)delta;
        if (_mat != null) _mat.EmissionEnergyMultiplier = 2f + Mathf.Sin(_t * 6f) * 1.5f;   // pulse / light up
        var g = _skel.GlobalTransform;
        for (int i = 0; i < _dots.Length; i++)
            _dots[i].GlobalPosition = (g * _skel.GetBoneGlobalPose(i)).Origin;   // TopLevel → world pos, fixed size, scale-proof
    }
}
