using Godot;

// Procedural melee-slash overlay for the authored goblin. A SkeletonModifier3D runs AFTER the walk AnimationMixer each frame,
// so it layers an additive arm swing on top of the playing walk without fighting it. The goblin drives one arm at a time
// (Creature picks + mirrors + randomizes L/R per strike); the other arm keeps its walk motion untouched.
public partial class GoblinSlashMod : SkeletonModifier3D
{
    public int Arm = -1, Fore = -1, Spine = -1;   // slashing arm's upper-arm + forearm, and a spine bone for the body lean
    public float SwingRad = 0f;       // signed pitch: + rears the arm back (wind-up), − chops it forward (strike)
    public float Side = 1f;           // +1 right arm, −1 left arm — controls the across-the-body yaw so it slashes inward
    public float SpineLean = 0f;      // torso commitment: bows forward + twists toward the slashing side on the chop

    public override void _ProcessModificationWithDelta(double delta)
    {
        var sk = GetSkeleton();
        if (sk == null) return;
        if (Arm >= 0 && Mathf.Abs(SwingRad) > 0.001f)
        {
            // chop on the arm's local Z (a downward/forward claw), crossing inward across the body (Side); forearm follows through.
            var upper = Quaternion.FromEuler(new Vector3(0f, Side * Mathf.Max(0f, -SwingRad) * 0.4f, SwingRad));
            sk.SetBonePoseRotation(Arm, sk.GetBonePoseRotation(Arm) * upper);
            if (Fore >= 0) sk.SetBonePoseRotation(Fore, sk.GetBonePoseRotation(Fore) * Quaternion.FromEuler(new Vector3(0f, 0f, SwingRad * 0.5f)));
        }
        if (Spine >= 0 && Mathf.Abs(SpineLean) > 0.001f)
            // whole body leans into the swing — bow forward (X) + twist toward the slashing side (Y)
            sk.SetBonePoseRotation(Spine, sk.GetBonePoseRotation(Spine) * Quaternion.FromEuler(new Vector3(SpineLean, Side * SpineLean * 0.7f, 0f)));
    }
}
