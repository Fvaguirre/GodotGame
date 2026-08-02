using Godot;

// Procedural "ouch" flinch for the authored bipeds — a quick additive recoil of the torso + head layered over whatever they're
// doing (walk/run/idle), so a DIRECT hit reads as a wince WITHOUT touching movement or the AI. Driven by a single Wince amount
// (1 on impact, decays to 0 in ~0.25s) and a Variant so different hits flinch differently (recoil back / twist L / twist R / hunch).
public partial class WinceMod : SkeletonModifier3D
{
    public int Spine = -1, Chest = -1, Head = -1;   // lower spine, upper chest, head bones
    public float Wince = 0f;                          // 0..1 impulse
    public int Variant = 0;                           // 0 recoil back, 1 twist left, 2 twist right, 3 hunch down

    public override void _ProcessModificationWithDelta(double delta)
    {
        if (Wince <= 0.001f) return;
        var sk = GetSkeleton();
        if (sk == null) return;
        float w = Wince;
        // per-variant torso recoil (radians): X = pitch (back/forward), Y = twist, Z = side-lean
        Vector3 spine, head;
        switch (Variant)
        {
            case 1:  spine = new Vector3(-0.10f, 0.28f, 0.16f) * w;  head = new Vector3(-0.12f, 0.30f, 0.10f) * w; break;   // twist/flinch to the left
            case 2:  spine = new Vector3(-0.10f, -0.28f, -0.16f) * w; head = new Vector3(-0.12f, -0.30f, -0.10f) * w; break;  // flinch to the right
            case 3:  spine = new Vector3(0.34f, 0.06f, 0f) * w;      head = new Vector3(0.30f, 0.05f, 0f) * w; break;        // hunch forward over the wound
            default: spine = new Vector3(-0.30f, 0f, 0f) * w;        head = new Vector3(-0.34f, 0f, 0f) * w; break;          // snap back from the hit
        }
        if (Spine >= 0) sk.SetBonePoseRotation(Spine, sk.GetBonePoseRotation(Spine) * Quaternion.FromEuler(spine));
        if (Chest >= 0) sk.SetBonePoseRotation(Chest, sk.GetBonePoseRotation(Chest) * Quaternion.FromEuler(spine * 0.6f));
        if (Head >= 0)  sk.SetBonePoseRotation(Head, sk.GetBonePoseRotation(Head) * Quaternion.FromEuler(head));
    }
}
