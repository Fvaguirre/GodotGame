using Godot;

// Procedural gesture overlay for THE HOLLOW MOON — the one attack the authored clip set doesn't cover.
//   MINES : the right arm alone goes straight up (PointUp), then chops down flat to the front — the "GO!" (PointFwd).
// (The rock throw used to be procedural too — an overhead two-hand lift — but it read badly against the authored
// animation, so it now rides the grip-and-throw clip like pestilence does.)
// A SkeletonModifier3D runs AFTER the AnimationMixer, so this layers on top of whatever locomotion clip is playing
// instead of fighting it (same pattern as GoblinSlashMod / ZombieReachMod).
public partial class BossGestureMod : SkeletonModifier3D
{
    public int ArmL = -1, ArmR = -1, ForeL = -1, ForeR = -1, Spine = -1;
    public float PointUp = 0f, PointFwd = 0f;   // one-hand raise → forward signal

    // The arcane hand orbs ride here rather than in Creature's per-frame tick: this modifier is the LAST thing in the
    // skeleton's stack, so the bone poses it reads are the final ones for the frame — including the arm lift above.
    // Reading them from Enemy's tick instead gave the pre-modifier pose, and the orbs sat frozen at his chest.
    public Node3D GlowL, GlowR;
    public int HandL = -1, HandR = -1;
    public float PalmOffset = 0f;        // how far PAST the wrist bone to push the orb, so it swallows the hand, not the cuff

    public override void _ProcessModificationWithDelta(double delta)
    {
        var sk = GetSkeleton();
        if (sk == null) return;
        // The arm bone's local Z is the down→forward "claw" pitch (the axis ZombieReachMod raises on): ~−1.35 rad reaches
        // horizontal forward, ~−2.70 straight overhead.
        if (PointUp > 0.001f)
            Pose(sk, ArmR, ForeR, 1f, PointUp * -2.70f + PointFwd * 1.35f, PointUp * 0.10f, PointUp * -0.30f + PointFwd * 0.22f);
        PinToHands(sk);
    }

    // The hand bone sits at the WRIST. Continuing along the forearm→hand direction puts the orb over the palm and fingers,
    // which is what should be obscured — anchoring on the bone itself left the glow riding high on the wrist.
    private void PinToHands(Skeleton3D sk)
    {
        if ((GlowL == null || !GlowL.Visible) && (GlowR == null || !GlowR.Visible)) return;
        var g = sk.GlobalTransform;
        Place(sk, g, GlowL, HandL, ForeL);
        Place(sk, g, GlowR, HandR, ForeR);
    }

    private void Place(Skeleton3D sk, Transform3D g, Node3D orb, int hand, int fore)
    {
        if (orb == null || !orb.Visible || hand < 0) return;
        Vector3 h = g * sk.GetBoneGlobalPose(hand).Origin;
        if (fore >= 0 && PalmOffset > 0f)
        {
            Vector3 f = g * sk.GetBoneGlobalPose(fore).Origin;
            Vector3 d = h - f;
            if (d.LengthSquared() > 0.0001f) h += d.Normalized() * PalmOffset;
        }
        orb.GlobalPosition = h;
    }

    private static void Pose(Skeleton3D sk, int arm, int fore, float side, float pitch, float spread, float forePitch)
    {
        if (arm < 0) return;
        sk.SetBonePoseRotation(arm, sk.GetBonePoseRotation(arm) * Quaternion.FromEuler(new Vector3(0f, side * spread, pitch)));
        if (fore >= 0) sk.SetBonePoseRotation(fore, sk.GetBonePoseRotation(fore) * Quaternion.FromEuler(new Vector3(0f, 0f, forePitch)));
    }
}
