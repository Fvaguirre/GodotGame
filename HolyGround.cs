using Godot;

// HolyGround.cs — the consecrated strip left by the Holy right-click. A divine ray descends and SWEEPS forward
// over ~SweepDur seconds; the strip reveals along that sweep (searing enemies + mending allies/minions as the ray
// reaches them), then LINGERS for Dur seconds before fading. Ground visual is a DECAL that projects onto the
// terrain, so it conforms to hills and never clips. Credits its CASTER's combo at the reduced drip rate.
public partial class HolyGround : Node3D
{
    public Vector3 Origin, Dir;     // strip start + horizontal forward
    public float Length = 20f, HalfW = 1.2f;
    public float Dur = 6f, MaxDur = 6f;   // linger time AFTER the sweep finishes
    public float SweepDur = 0f;           // (NEW) time for the ray to sweep from start to full length (0 = instant)
    public float EnemyDmg = 6f;     // per second (lingering light sear)
    public float HealPerSec = 2f;   // per second to allies/self/minions
    public Player Caster;           // (NEW) who cast it — the leading-edge hit routes combo/weave/refund through her
    public float SweepDmg = 0f;     // (NEW) the real per-foe hit the sweep's leading edge deals
    public bool SweepCrit = false;  // (NEW)
    public bool FullCharge = false; // (NEW) full-charge cast → fire charged modifiers at the first foe reached
    public float BlessDur = 0f;     // (NEW) full-charge only: seconds of Blessed granted to allies/minions the SWEEP passes over
    public bool Remote = false;     // (NEW) visual-only ally copy: shows the strip decal, runs no damage/heal/bless

    private Decal _decal;
    private OmniLight3D _light;
    private float _tick = 0f, _swept = 0f;
    private Color _col;
    private readonly System.Collections.Generic.HashSet<Enemy> _sweptHit = new();   // (NEW) each foe takes the leading-edge hit once
    private bool _modsFired = false;   // (NEW) full-charge modifiers fire once, at the first foe reached
    private readonly System.Collections.Generic.HashSet<Thornling> _sweptBlessMinions = new();   // (NEW) each swept minion blessed once
    private readonly System.Collections.Generic.HashSet<long> _sweptBlessPeers = new();           // (NEW) each swept ally peer blessed once

    public override void _Ready()
    {
        _col = DamageTypes.Col(DamageType.Holy);
        _decal = new Decal
        {
            TextureAlbedo = Game.ScorchTex(),
            TextureEmission = Game.ScorchTex(),
            EmissionEnergy = 2.2f,
            Modulate = new Color(_col.R, _col.G, _col.B, 0.85f),
            Size = new Vector3(HalfW * 2f, 24f, 0.2f)   // Y projection depth covers hilly ground; Z grows as it sweeps
        };
        AddChild(_decal);
        _decal.RotationDegrees = new Vector3(0, Mathf.RadToDeg(Mathf.Atan2(Dir.X, Dir.Z)), 0);   // align length with Dir

        var mid = Origin + Dir * (Length * 0.5f);
        float my = Game.I != null ? Game.I.SurfaceHeight(mid, mid.Y) : Origin.Y;
        _light = new OmniLight3D { OmniRange = Mathf.Max(HalfW, Length * 0.3f), LightColor = _col, LightEnergy = 1.0f };
        AddChild(_light);
        _light.Position = new Vector3(mid.X, my + 1.5f, mid.Z);   // node sits at origin, so local == world
    }

    public override void _Process(double delta)
    {
        var g = Game.I;
        if (g == null || !g.SimActive) return;
        float dt = (float)delta;
        _tick -= dt;

        bool sweeping = _swept < SweepDur;
        if (sweeping) _swept += dt;
        float rl = SweepDur > 0f ? Mathf.Clamp(Length * (_swept / SweepDur), 0.2f, Length) : Length;   // revealed length

        if (_decal != null)
        {
            var rmid = Origin + Dir * (rl * 0.5f);
            float my = g.SurfaceHeight(rmid, rmid.Y);
            _decal.Size = new Vector3(HalfW * 2f, 24f, rl);
            _decal.GlobalPosition = new Vector3(rmid.X, my, rmid.Z);   // grows forward from the start as the ray sweeps
            float f = sweeping ? 1f : Mathf.Clamp(Dur / Mathf.Max(0.01f, MaxDur), 0f, 1f);
            _decal.Modulate = new Color(_col.R, _col.G, _col.B, 0.12f + 0.73f * f);
            _decal.EmissionEnergy = 0.6f + 1.8f * f;
        }

        if (!sweeping) Dur -= dt;   // only start expiring once the sweep has finished

        if (Remote) { if (!sweeping && Dur <= 0f) QueueFree(); return; }   // (NEW) ally copy is decal-only — no sweep hits/heal/bless/mods

        // (NEW) LEADING-EDGE SWEEP HIT: as the ray reaches each foe, it takes one real sear hit. This is what
        // carries combo/weave and the charged-cast mana refund (routed through the caster's OnHitDirect).
        if (SweepDmg > 0f)
        {
            foreach (var e in g.Enemies.ToArray())
            {
                if (e == null || e.Dead || !GodotObject.IsInstanceValid(e) || _sweptHit.Contains(e)) continue;
                if (Inside(e.GlobalPosition, e.Radius, rl))
                {
                    _sweptHit.Add(e);
                    e.Hurt(SweepDmg, DamageType.Holy, true, SweepCrit);
                    if (Caster != null)
                    {
                        Caster.OnHitDirect(e, e.Dead, SweepDmg, DamageType.Holy);
                        Caster.ProcFlash = 0.2f;
                        if (FullCharge && !_modsFired) { Caster.ApplyChargedMods(e.GlobalPosition); _modsFired = true; }   // (NEW) mods erupt AT the first foe reached
                    }
                }
            }
        }
        if (!sweeping && FullCharge && !_modsFired && Caster != null) { Caster.ApplyChargedMods(Origin + Dir * Length); _modsFired = true; }   // (NEW) swept nothing → fire at the ray's far end

        // (NEW) BLESS allies/minions the leading edge sweeps over — ONLY while the ray is actively sweeping.
        // Standing in the lingering strip afterwards heals but never blesses. Non-stacking (t.Bless / Max).
        if (sweeping && BlessDur > 0f)
        {
            if (Caster != null)
                foreach (var t in Caster.Ents.ToArray())
                {
                    if (t == null || !GodotObject.IsInstanceValid(t) || _sweptBlessMinions.Contains(t)) continue;
                    if (Inside(t.GlobalPosition, 0.6f, rl)) { _sweptBlessMinions.Add(t); t.Bless(BlessDur); }
                }
            g.NetMgr?.BlessSweptAllies(Origin, Dir, HalfW, rl, BlessDur, _sweptBlessPeers);
        }

        if (_tick <= 0f)
        {
            _tick = 0.25f;
            var healMid = Origin + Dir * (rl * 0.5f);
            bool touched = false;   // (NEW) only credit combo when it actually affects a live target
            foreach (var e in g.Enemies.ToArray())   // lingering light sear within the swept-so-far length
            {
                if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
                if (Inside(e.GlobalPosition, e.Radius, rl)) { e.Hurt(EnemyDmg * 0.25f, DamageType.Holy, true); touched = true; }
            }
            g.DamageWorld(healMid, Mathf.Max(HalfW, rl * 0.5f), EnemyDmg * 0.25f);   // (NEW) the holy charge breaks props under its strip
            var p = g.Player;
            if (p != null && Inside(p.GlobalPosition, 0.5f, rl) && p.Hp < p.S.MaxHp) { p.Heal(HealPerSec * 0.25f); touched = true; }   // heal self only if hurt
            if (p != null && p.HealOwnMinions(HealPerSec * 0.25f)) touched = true;   // heal minions only if any were hurt
            g.NetMgr?.HealAlliesNear(healMid, Mathf.Max(HalfW, rl * 0.5f), HealPerSec * 0.25f);   // this self-credits combo iff it heals an ally
            if (touched) p?.ComboFromDot();   // (NEW) credit only on a real sear/self/minion heal — not for empty ground
        }

        if (!sweeping && Dur <= 0f) QueueFree();
    }

    private bool Inside(Vector3 pos, float radius, float len)
    {
        var rel = pos - Origin; rel.Y = 0;
        float along = rel.Dot(Dir);
        if (along < -radius || along > len + radius) return false;
        float perp = (rel - Dir * along).Length();
        return perp < HalfW + radius;
    }
}
