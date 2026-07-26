using Godot;

// Pedestal.cs — a persistent stone dais scattered at map load to ELEVATE some effigies onto a raised, staircase-flanked
// platform (so the world isn't all flat). Solid + walkable via a PersistentDeck that Game registers (survives chunk
// streaming). This class is purely the visual; the deck + the effigy-on-top placement live in Game.
public partial class Pedestal : Node3D
{
    public int NetId = 0;
    public bool Remote = false;
    public const float Half = 2.6f;    // top footprint half-extent (matches the registered Deck) — small enough that the effigy at its centre is within hold-E reach from the ground
    public const float TopH = 1.3f;    // platform height above its base — kept under the 1.6u step so foes STEP up onto it from any side (no wall-scaling, no pathfinding needed)

    public override void _Ready()
    {
        var stone = Game.Toon(new Color(0.19f, 0.18f, 0.22f), 0.9f, 0.22f, 0.03f);
        var cap = Game.Toon(new Color(0.27f, 0.26f, 0.31f), 0.9f, 0.2f, 0.03f);

        var slab = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(Half * 2f, TopH, Half * 2f) }, MaterialOverride = stone };
        slab.Position = new Vector3(0, TopH / 2f, 0); AddChild(slab);
        var top = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(Half * 2f + 0.4f, 0.3f, Half * 2f + 0.4f) }, MaterialOverride = cap };
        top.Position = new Vector3(0, TopH, 0); AddChild(top);

        // a flight of steps up the +Z face (the staircase)
        int steps = 4;
        for (int i = 0; i < steps; i++)
        {
            float sy = TopH * (i + 1) / (float)(steps + 1);
            float sz = Half + 0.4f + (steps - 1 - i) * 0.55f;
            var st = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(2.8f, sy, 0.55f) }, MaterialOverride = stone };
            st.Position = new Vector3(0, sy / 2f, sz); AddChild(st);
        }
        // short broken corner posts for a ruin silhouette
        for (int i = 0; i < 4; i++)
        {
            float a = i / 4f * Mathf.Tau + 0.78f;
            float ph = 0.7f + (i % 2) * 0.6f;
            var post = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.5f, ph, 0.5f) }, MaterialOverride = stone };
            post.Position = new Vector3(Mathf.Cos(a) * (Half - 0.5f), TopH + ph / 2f, Mathf.Sin(a) * (Half - 0.5f)); AddChild(post);
        }
    }
}
