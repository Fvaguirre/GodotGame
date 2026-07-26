using Godot;

// Pumpkin.cs — a smashable patch pumpkin. Player attacks within range shatter it with a crunch + pulp burst,
// and there's a rare chance it coughs up gold, a shield refill, or (super rare) ult charge. The decorative
// world is built locally per machine, so pumpkins are local props: each player smashes their own and gets
// their own drop — the correct co-op behavior here, and it can't desync. (NEW)
public partial class Pumpkin : Node3D
{
    private bool _smashed = false;
    public float Hp = 1f;   // (NEW) hidden health — any damage source that reaches this shatters it (world-damageable prop)
    protected float _size = 0.8f;
    protected MeshInstance3D _body, _stem;   // (protected so PepperBush can supply its own visual and still reuse Smash)
    protected Color _col;

    public void Init(float size, bool lit, ulong seed)
    {
        _size = size;
        var rng = new RandomNumberGenerator(); rng.Seed = seed;
        BuildVisual(lit, rng);
    }

    // Override to give a subclass (PepperBush) its own look. Set _col/_body(/_stem); the smash reuses them.
    protected virtual void BuildVisual(bool lit, RandomNumberGenerator rng)
    {
        _col = new Color(0.85f, 0.35f, 0.05f).Lerp(new Color(0.7f, 0.45f, 0.08f), rng.Randf());

        _body = new MeshInstance3D { Mesh = new SphereMesh { Radius = _size, Height = _size * 1.3f } };
        _body.MaterialOverride = lit ? Game.ToonEmissive(_col, 0.7f) : Game.Toon(_col, 0.9f, 0.22f, 0.03f);
        _body.Position = new Vector3(0, _size * 0.55f, 0);
        _body.Scale = new Vector3(1f, 0.8f, 1f);
        AddChild(_body);

        _stem = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.05f, BottomRadius = 0.08f, Height = 0.3f } };
        _stem.MaterialOverride = Game.Toon(new Color(0.12f, 0.16f, 0.08f), 0.95f, 0.22f, 0.03f);
        _stem.Position = new Vector3(0, _size * 1.0f, 0);
        AddChild(_stem);
    }

    public void TakeDamage(float dmg)   // (NEW) world objects break when their hidden HP is depleted by ANY damage source
    {
        if (_smashed || dmg <= 0f) return;
        Hp -= dmg;
        if (Hp <= 0f) Smash();
    }

    public void Smash(bool loot = true, bool broadcast = true)
    {
        if (_smashed) return;
        _smashed = true;
        if (broadcast) Game.I?.NetMgr?.BroadcastSmashPumpkin(GlobalPosition);   // (NEW) shared prop — everyone loses this pumpkin
        Game.I?.Smashables.Remove(this);
        Game.I?.Sfx?.CrunchAt(GlobalPosition);

        if (GodotObject.IsInstanceValid(_body)) _body.Visible = false;
        if (GodotObject.IsInstanceValid(_stem)) _stem.Visible = false;

        // pulp shards flung outward: a quick pop up-and-out, then they arc down (gravity-ish), spinning
        var pulpMat = Game.Toon(_col.Darkened(0.1f), 0.9f, 0.22f, 0f);
        for (int i = 0; i < 8; i++)
        {
            float a = i / 8f * Mathf.Tau + GD.Randf() * 0.5f;
            var outv = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
            var shard = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(_size * 0.32f, _size * 0.22f, _size * 0.34f) },
                MaterialOverride = pulpMat
            };
            AddChild(shard);
            Vector3 start = new Vector3(0, _size * 0.5f, 0);
            shard.Position = start;
            shard.Rotation = new Vector3(GD.Randf() * 6f, GD.Randf() * 6f, GD.Randf() * 6f);
            Vector3 apex = start + outv * (_size * 1.3f) + new Vector3(0, _size * (1.0f + GD.Randf() * 0.8f), 0);
            Vector3 land = start + outv * (_size * (1.8f + GD.Randf() * 1.6f)) + new Vector3(0, -_size * 0.45f, 0);
            var pos = shard.CreateTween();
            pos.TweenProperty(shard, "position", apex, 0.16f).SetEase(Tween.EaseType.Out);
            pos.TweenProperty(shard, "position", land, 0.42f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
            var spin = shard.CreateTween();
            spin.TweenProperty(shard, "rotation", shard.Rotation + new Vector3(GD.Randf() * 10f - 5f, GD.Randf() * 6f - 3f, GD.Randf() * 10f - 5f), 0.58f);
        }

        // a flat pulp splat left on the ground
        var splat = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = _size * 1.3f, BottomRadius = _size * 1.3f, Height = 0.04f, RadialSegments = 14 },
            MaterialOverride = pulpMat
        };
        splat.Position = new Vector3(0, 0.03f, 0);
        AddChild(splat);

        if (loot) RollDrop();   // (NEW) only the player who broke it gets the reward; networked smashes don't

        var done = CreateTween();
        done.TweenInterval(1.4f);
        done.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(this)) QueueFree(); }));
    }

    // rare reward table. Nothing most of the time; gold uncommon; shield rare; ult charge super rare.
    private void RollDrop()
    {
        var p = Game.I?.Player;
        float r = GD.Randf();
        if (r < 0.02f)            // super rare: ult charge
        {
            if (p != null && p.Ult != Player.UltKind.None && !p.UltActive)
            { p.UltCharge = Mathf.Min(1f, p.UltCharge + 0.5f); Game.I?.Hud?.Banner("the pumpkin held ult charge!"); }
            else p?.AddMana(3f);   // no ult equipped/active → a little mana instead, so it's never wasted
            DropBurst(DamageTypes.Col(DamageType.Arcane));
        }
        else if (r < 0.07f)       // rare: shield refill
        {
            if (p != null) { p.Shield = p.MaxShield; Game.I?.Hud?.Banner("shield restored!"); }
            DropBurst(new Color(0.6f, 0.85f, 1f));
        }
        else if (r < 0.18f)       // uncommon: a little gold
        {
            int amt = 8 + (int)(GD.Randf() * 14f);
            if (Game.I != null) { Game.I.Gold += amt; Game.I.GoldFlash = 3f; Game.I.SaveGold(); }
            DropBurst(new Color(1f, 0.84f, 0.3f));
        }
    }

    // a bright mote that floats up + fades, color-coded to the reward
    private void DropBurst(Color col)
    {
        var orb = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.32f, Height = 0.64f } };
        var m = Game.ToonEmissive(col, 2.4f, 0f);
        m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        orb.MaterialOverride = m;
        AddChild(orb);
        orb.Position = new Vector3(0, _size * 0.6f, 0);
        var tw = orb.CreateTween(); tw.SetParallel(true);
        tw.TweenProperty(orb, "position", new Vector3(0, _size * 2.4f, 0), 0.75f).SetEase(Tween.EaseType.Out);
        tw.TweenProperty(orb, "transparency", 1f, 0.75f);
    }
}
