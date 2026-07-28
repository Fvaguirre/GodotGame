using Godot;

// Pedestal.cs — a persistent stone dais scattered at map load to ELEVATE some effigies onto a raised, staircase-flanked
// platform (so the world isn't all flat). Solid + walkable via a PersistentDeck that Game registers (survives chunk
// streaming). This class is purely the visual; the deck + the effigy-on-top placement live in Game.
public partial class Pedestal : Node3D
{
    public int NetId = 0;
    public bool Remote = false;
    public const float Half = 2.6f;    // (legacy) — the real footprint now comes from DaisR (the model's measured radius)
    public const float TopH = 1.3f;    // platform height above its base — kept under the 1.6u step so foes STEP up onto it from any side (no wall-scaling, no pathfinding needed)
    public const float DaisScale = TopH * 1.55f;   // (renamed from Scale to not hide Node3D.Scale)
    // the dais's REAL top-surface radius (the model's XZ half-extent × scale) — the Deck + rim collision size to this
    public static float DaisR => DaisScale * Mathf.Max(PropGlb.NormExtents("platform").X, PropGlb.NormExtents("platform").Y);

    public override void _Ready()
    {
        // (MESHY) a real runed stone dais. The walkable top (Deck, over the whole footprint) + the rim-block collision are
        // registered by Game.SpawnPedestals; this node is purely the visual.
        var dais = PropGlb.Instance("platform", DaisScale, seed: NetId);
        AddChild(dais);
    }
}
