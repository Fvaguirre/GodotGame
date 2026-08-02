using Godot;

// Procedural "zombie grab" arms-forward pose for the authored Taker. A SkeletonModifier3D runs AFTER the locomotion mixer,
// so it layers a raised, reaching pose on BOTH arms on top of the playing walk/run without fighting it. Driven by a single
// Reach amount (0 = arms down/normal, 1 = full reach) that the Taker ramps up as its grab telegraphs — readable, not instant.
public partial class ZombieReachMod : SkeletonModifier3D
{
    public int ArmL = -1, ArmR = -1, ForeL = -1, ForeR = -1;   // both upper-arm + forearm bones
    public float Reach = 0f;   // 0..1 blend into the forward-grab pose

    public override void _ProcessModificationWithDelta(double delta)
    {
        if (Reach <= 0.001f) return;
        var sk = GetSkeleton();
        if (sk == null) return;
        // Bring each arm up from hanging-down to reaching forward: the arm bone's local Z is the down/forward "claw" axis
        // (same axis the slash chops on), so a negative Z pitch raises the arm toward horizontal-forward. A small outward
        // yaw (Side*Y) keeps the two arms from crossing; the forearm bends slightly so the hands lead the grab.
        Pose(sk, ArmL, ForeL, -1f);
        Pose(sk, ArmR, ForeR, 1f);
    }

    private void Pose(Skeleton3D sk, int arm, int fore, float side)
    {
        if (arm < 0) return;
        var upper = Quaternion.FromEuler(new Vector3(0f, side * Reach * 0.28f, -Reach * 1.35f));   // raise forward (Z) + spread out (Y)
        sk.SetBonePoseRotation(arm, sk.GetBonePoseRotation(arm) * upper);
        if (fore >= 0) sk.SetBonePoseRotation(fore, sk.GetBonePoseRotation(fore) * Quaternion.FromEuler(new Vector3(0f, 0f, -Reach * 0.45f)));
    }
}
