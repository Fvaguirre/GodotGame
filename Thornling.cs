using Godot;

// Thornling.cs — the Verdant Witch's tree-ent minion (her main damage source). OWNER-SIMULATED AI:
// PickTarget prefers poisoned foes, else nearest, else returns to the witch's side; melee deals
// MinionDamage() Nature + a brief entangle (Root) + a little Poison, and Detonate() (triggered by her
// full-charge thorn passing through) bursts for MinionBurst(). MULTIPLAYER: each Verdant player
// broadcasts its ents via Net.MinionSnapshot; everyone else renders Ghost=true copies (no AI/damage)
// that follow the synced transform and play the attack lunge. AnimateBody() is shared by both.
public partial class Thornling : Node3D
{
    public Player Caster;
    public int Slot = 0;
    public float BodyYaw => _body != null ? _body.Rotation.Y : 0f;
    private float _atkCd = 0f;
    private float _phase = 0f;
    private float _retarget = 0f;
    private Enemy _tgt;
    private Node3D _body;
    private MeshInstance3D _thorns;   // Barkskin thorn shell — shown while the owning witch has Barkskin up
    private float _vy = 0f;
    public bool Ghost = false;        // network copy on an ally's screen: follows synced transform, no AI/damage
    public float AtkPulse = 0f;       // briefly >0 after an attack (sent over the wire)
    public float Fuse = 0f;           // Wild Swarm: >0 = auto-detonate when it reaches 0
    private float _atkAnim = 0f;      // drives the lunge
    private Vector3 _gpos; private float _gyaw = 0f; private bool _gInit = false;
    public void SetGhost(Vector3 pos, float yaw, bool atk)
    {
        _gpos = pos; _gyaw = yaw;
        if (!_gInit) { GlobalPosition = pos; _gInit = true; }
        if (atk) _atkAnim = 0.32f;
    }

    // --- ally-unit stats: ents are now real units that take damage, heal, and can die ---
    public float Hp = 0f, MaxHp = 0f;        // owner-sim; scaled to a fraction of the witch's HP on spawn
    public float GhostHpFrac = 1f;           // ghosts render their bar from this synced fraction
    public float HpFrac => MaxHp > 0f ? Mathf.Clamp(Hp / MaxHp, 0f, 1f) : 1f;
    private bool _dead = false;
    private float _slowT = 0f;               // negative status (enemy contact slows the ent)
    private float _blessT = 0f;              // (NEW) blessed: a friendly holy blessing that slowly mends the ent
    private float _windBoonT = 0f;           // Eyewall: move/attack-speed buff while in an ally's hurricane (NEW)
    public void GrantWindBoon(float dur) { if (!Ghost) _windBoonT = Mathf.Max(_windBoonT, dur); }   // (NEW)
    private float _contactCd = 0f;           // throttles incoming enemy contact damage
    private float _hurtPunch = 0f;           // brief squash on taking a hit
    private Node3D _hpBar; private MeshInstance3D _hpFill; private const float HpBarW = 0.95f;

    private const float Speed = 7.5f;
    private const float Reach = 2.2f;
    private const float SightR = 30f;

    public override void _Ready()
    {
        var bark = Game.ToonEmissive(new Color(0.42f, 0.30f, 0.18f), 0.4f, 0.03f);
        var leaf = Game.ToonEmissive(new Color(0.30f, 0.72f, 0.34f), 0.7f, 0.04f);
        var glow = Game.ToonEmissive(new Color(0.55f, 1f, 0.5f), 1.6f, 0.02f);
        _body = new Node3D();
        AddChild(_body);
        void Add(Node3D p, Mesh m, Material mat, Vector3 pos, Vector3 rotDeg = default)
        { var mi = new MeshInstance3D { Mesh = m, MaterialOverride = mat }; mi.Position = pos; mi.RotationDegrees = rotDeg; p.AddChild(mi); }
        Add(_body, new CylinderMesh { TopRadius = 0.22f, BottomRadius = 0.3f, Height = 1.0f }, bark, new Vector3(0, 0.5f, 0));   // trunk
        Add(_body, new SphereMesh { Radius = 0.55f, Height = 1.1f }, leaf, new Vector3(0, 1.25f, 0));                            // canopy
        Add(_body, new SphereMesh { Radius = 0.36f, Height = 0.72f }, leaf, new Vector3(0.32f, 1.5f, 0.1f));
        Add(_body, new SphereMesh { Radius = 0.32f, Height = 0.64f }, leaf, new Vector3(-0.3f, 1.45f, -0.1f));
        Add(_body, new SphereMesh { Radius = 0.06f, Height = 0.12f }, glow, new Vector3(0.12f, 0.95f, 0.28f));                   // eyes
        Add(_body, new SphereMesh { Radius = 0.06f, Height = 0.12f }, glow, new Vector3(-0.12f, 0.95f, 0.28f));
        // stubby branch arms
        _armL = new Node3D { Position = new Vector3(-0.28f, 0.75f, 0) }; _body.AddChild(_armL);
        Add(_armL, new CylinderMesh { TopRadius = 0.06f, BottomRadius = 0.08f, Height = 0.5f }, bark, new Vector3(0, -0.2f, 0), new Vector3(0, 0, 35));
        _armR = new Node3D { Position = new Vector3(0.28f, 0.75f, 0) }; _body.AddChild(_armR);
        Add(_armR, new CylinderMesh { TopRadius = 0.06f, BottomRadius = 0.08f, Height = 0.5f }, bark, new Vector3(0, -0.2f, 0), new Vector3(0, 0, -35));
        AddChild(new OmniLight3D { Position = new Vector3(0, 1.2f, 0), OmniRange = 4f, LightColor = new Color(0.4f, 0.9f, 0.4f), LightEnergy = 0.7f });

        // Barkskin thorn shell (matches the player/avatar version, scaled to the ent). Hidden until the owner barks.
        _thorns = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.85f, Height = 1.7f } };
        _thorns.Position = new Vector3(0, 0.95f, 0);
        _thorns.MaterialOverride = new StandardMaterial3D {
            AlbedoColor = new Color(0.30f, 0.85f, 0.40f, 0.18f),
            EmissionEnabled = true, Emission = new Color(0.35f, 0.95f, 0.45f), EmissionEnergyMultiplier = 1.3f,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };
        var spikeMat = new StandardMaterial3D {
            AlbedoColor = new Color(0.22f, 0.55f, 0.26f),
            EmissionEnabled = true, Emission = new Color(0.30f, 0.80f, 0.35f), EmissionEnergyMultiplier = 0.8f
        };
        int spikes = 11;
        for (int i = 0; i < spikes; i++)
        {
            float u = (i + 0.5f) / spikes;
            float theta = u * Mathf.Tau * 3f;
            float yy = 1f - 2f * u;
            float ring = Mathf.Sqrt(Mathf.Max(0f, 1f - yy * yy));
            var dir = new Vector3(Mathf.Cos(theta) * ring, yy, Mathf.Sin(theta) * ring);
            var spike = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.08f, Height = 0.34f }, MaterialOverride = spikeMat };
            spike.Position = dir * 0.85f;
            var axis = Vector3.Up.Cross(dir);
            float ang = Mathf.Acos(Mathf.Clamp(Vector3.Up.Dot(dir), -1f, 1f));
            spike.Basis = axis.LengthSquared() > 1e-5f ? new Basis(axis.Normalized(), ang) : Basis.Identity;
            _thorns.AddChild(spike);
        }
        _thorns.Visible = false;
        AddChild(_thorns);
        Game.AddFriendlySilhouette(this, new Color(0.4f, 0.95f, 0.45f), 0.5f, 1.5f, 0.95f);   // readable through walls like allies
        if (!Ghost && Caster != null && GodotObject.IsInstanceValid(Caster))
        { MaxHp = Caster.S.MaxHp * 0.28f; Hp = MaxHp; }   // a fraction of the witch's HP — re-summoned ents scale as she levels
        BuildHpBar();
        if (!Ghost && Game.I != null && GD.Randf() < 0.12f) Say("LEEEEROOOOY JENKINS!", 3, new Color(1f, 0.85f, 0.4f));   // rare spawn warcry
    }

    // a small billboarded health bar above the canopy (shown once damaged; ghosts use the synced fraction)
    private void BuildHpBar()
    {
        _hpBar = new Node3D { Position = new Vector3(0, 2.4f, 0), Visible = false };
        AddChild(_hpBar);
        Material Bar(Color c) => new StandardMaterial3D
        {
            AlbedoColor = c, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha, RenderPriority = 9
        };
        var bg = new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(HpBarW, 0.13f) }, MaterialOverride = Bar(new Color(0, 0, 0, 0.7f)) };
        _hpBar.AddChild(bg);
        _hpFill = new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(HpBarW, 0.11f) }, MaterialOverride = Bar(new Color(0.45f, 0.9f, 0.4f, 0.95f)) };
        _hpFill.Position = new Vector3(0, 0, 0.001f);
        _hpBar.AddChild(_hpFill);
    }

    // left-anchored shrink + green→red tint; hides at full health
    private void UpdateHpBar(float frac)
    {
        if (_hpBar == null) return;
        bool show = frac < 0.999f && frac > 0f;
        _hpBar.Visible = show;
        if (!show) return;
        _hpFill.Scale = new Vector3(Mathf.Max(0.0001f, frac), 1f, 1f);
        _hpFill.Position = new Vector3(-HpBarW * 0.5f * (1f - frac), 0, 0.001f);
        var mat = (StandardMaterial3D)_hpFill.MaterialOverride;
        mat.AlbedoColor = new Color(Mathf.Lerp(0.95f, 0.45f, frac), Mathf.Lerp(0.25f, 0.9f, frac), 0.35f, 0.95f);
    }
    private Node3D _armL, _armR;

    public void SetThorns(bool on) { if (_thorns != null && _thorns.Visible != on) _thorns.Visible = on; }

    public override void _Process(double delta)
    {
        if (Game.I == null || Game.I.State != GameState.Playing) return;   // freeze while paused (NEW)
        float dt = (float)delta;
        if (!Ghost && Fuse > 0f)   // Wild Swarm: count down and blow up on its own (unless detonated sooner)
        {
            Fuse -= dt;
            if (Fuse <= 0f) { Detonate(); return; }
        }
        if (_atkAnim > 0f) _atkAnim -= dt;
        if (AtkPulse > 0f) AtkPulse -= dt;

        if (Ghost)   // network copy: follow synced transform, play lunge from synced attacks
        {
            GlobalPosition = GlobalPosition.Lerp(_gpos, Mathf.Clamp(dt * 12f, 0f, 1f));
            _phase += dt * 6f;
            _body.Rotation = new Vector3(0, Mathf.LerpAngle(_body.Rotation.Y, _gyaw, dt * 10f), 0);
            AnimateBody();
            UpdateHpBar(GhostHpFrac);
            return;
        }

        if (Caster == null || !GodotObject.IsInstanceValid(Caster)) return;
        SetThorns(Caster.BarkActive);   // live ent: mirror the owner's Barkskin (ghosts are driven from MinionSnapshot)
        if (!Game.I.WorldRunning) return;
        if (_atkCd > 0f) _atkCd -= dt;
        _retarget -= dt;
        var prevTgt = _tgt;
        if (_retarget <= 0f || _tgt == null || !GodotObject.IsInstanceValid(_tgt) || _tgt.Dead) { _retarget = 0.35f; _tgt = PickTarget(); }
        if (!Ghost && _tgt != null && _tgt != prevTgt && GodotObject.IsInstanceValid(_tgt) && !_tgt.Dead && GD.Randf() < 0.18f)
            Say(GD.Randf() < 0.5f ? "For Motherrr!" : "Kill kill!", 1, new Color(0.6f, 0.95f, 0.5f));

        Vector3 goal;
        bool combat = _tgt != null && GodotObject.IsInstanceValid(_tgt) && !_tgt.Dead;
        if (combat) goal = _tgt.GlobalPosition;
        else
        {
            // no foes — drift back to the witch's side
            float a = Slot * 2.3f;
            goal = Caster.GlobalPosition + new Vector3(Mathf.Cos(a) * 2.6f, 0, Mathf.Sin(a) * 2.6f);
        }

        if (_slowT > 0f) _slowT -= dt;
        if (_blessT > 0f) _blessT -= dt;   // (FIX) bless no longer direct-heals — it only amplifies healing RECEIVED (see Heal below)
        if (_windBoonT > 0f) _windBoonT -= dt;   // Eyewall buff decays (NEW)
        if (_hurtPunch > 0f) _hurtPunch -= dt;
        Vector3 to = goal - GlobalPosition; to.Y = 0f;
        float dist = to.Length();
        float stop = combat ? Reach : 0.4f;
        float spd = Speed * (_slowT > 0f ? 0.5f : 1f) * (_windBoonT > 0f ? 1.35f : 1f);   // Eyewall move-speed buff (NEW)
        if (dist > stop)
        {
            var dir = to.Normalized();
            GlobalPosition += dir * spd * dt;
            float yaw = Mathf.Atan2(dir.X, dir.Z);
            _body.Rotation = new Vector3(0, Mathf.LerpAngle(_body.Rotation.Y, yaw, dt * 9f), 0);
            _phase += dt * 9f;
        }
        else _phase += dt * 2f;

        // follow the ground
        float gy = Game.I.SurfaceHeight(GlobalPosition, GlobalPosition.Y);
        GlobalPosition = new Vector3(GlobalPosition.X, Mathf.MoveToward(GlobalPosition.Y, gy, 12f * dt), GlobalPosition.Z);

        AnimateBody();

        if (combat && dist <= stop + _tgt.Radius && _atkCd <= 0f)
        {
            _atkCd = Mathf.Clamp(0.8f * (Caster.S.FireCd / 0.28f), 0.4f, 1.1f) * (_windBoonT > 0f ? 0.7f : 1f);   // attack speed tracks the witch's cast rate; Eyewall hastens it (NEW)
            _atkAnim = 0.32f; AtkPulse = 0.2f;                 // lunge (synced to allies)
            float dmg = Caster.MinionStrike(Caster.MinionDamage(), out bool crit);
            _tgt.Hurt(dmg, DamageType.Nature, true, crit); _tgt.HitFrom(GlobalPosition);
            _tgt.Root(0.5f);                                   // entangle on hit
            _tgt.Poison(Caster.PoisonDps() * 0.5f, 3f);          // a touch of poison too
            Caster.ComboFromSource();
            Game.I.VfxRing(_tgt.GlobalPosition, new Color(0.4f, 0.85f, 0.4f), 1.6f, 0.25f);
        }

        // ENEMIES ATTACK ENTS: any foe in melee range chips the ent on a cooldown (owner-authoritative,
        // so it works for the host's ents and every client's ents alike — each owner sims its own pack)
        if (_contactCd > 0f) _contactCd -= dt;
        if (!_dead && MaxHp > 0f && _contactCd <= 0f)
        {
            Enemy hitter = null; float best = float.MaxValue;
            foreach (var e in Game.I.Enemies.ToArray())
            {
                if (e == null || e.Dead || !GodotObject.IsInstanceValid(e) || e.Dmg <= 0f) continue;
                float reach = Reach + e.Radius + 0.5f;
                float d = (e.GlobalPosition - GlobalPosition).LengthSquared();
                if (d < reach * reach && d < best) { best = d; hitter = e; }
            }
            if (hitter != null)
            {
                _contactCd = 0.7f;
                Hurt(hitter.Dmg * 0.6f);
                if (hitter.Elite || hitter.Affix > 0) Slow(1.0f);   // tougher foes also briefly hobble the ent (negative affix effect)
            }
        }
        UpdateHpBar(HpFrac);
    }

    // shared bob + attack-lunge animation (used by owner and ghost copies)
    private void AnimateBody()
    {
        float bob = Mathf.Abs(Mathf.Sin(_phase)) * 0.12f;
        float lunge = _atkAnim > 0f ? Mathf.Sin((1f - _atkAnim / 0.32f) * Mathf.Pi) : 0f;   // 0→1→0 thrust
        if (_body != null) _body.Position = new Vector3(0, bob, -lunge * 0.55f);             // lunge forward (-Z)
        if (_armL != null) _armL.Rotation = new Vector3(Mathf.Sin(_phase) * 0.5f - lunge * 1.3f, 0, 0.3f);
        if (_armR != null) _armR.Rotation = new Vector3(-Mathf.Sin(_phase) * 0.5f - lunge * 1.3f, 0, -0.3f);
    }

    private Enemy PickTarget()
    {
        Enemy poisoned = null, nearest = null; float pd = SightR * SightR, nd = SightR * SightR;
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            float d = (e.GlobalPosition - GlobalPosition).LengthSquared();
            if (e.IsPoisoned && d < pd) { pd = d; poisoned = e; }
            if (d < nd) { nd = d; nearest = e; }
        }
        return poisoned ?? nearest;
    }

    // her full-charge thorn passing through this ent blows it up for a strong Nature burst
    public int Detonate()
    {
        if (_detonated) return 0;          // guard: chain reactions can call this more than once
        if (Caster != null && GodotObject.IsInstanceValid(Caster) && Caster.BarkActive) return 0;   // can't detonate ents during Barkskin
        _detonated = true;
        Caster?.Ents.Remove(this);         // leave the grove now so a recount/refund this frame is accurate
        if (!Ghost && Game.I != null && GD.Randf() < 0.4f) Say(GD.Randf() < 0.5f ? "Booomm!" : "Oooowww!", 2, new Color(0.5f, 0.9f, 0.5f));
        float dmg = Caster != null ? Caster.MinionBurst() : 60f;
        float rad = 5.5f * (Caster != null ? Caster.S.SpellArea : 1f);   // minion blast grows with spell area
        var col = new Color(0.4f, 0.85f, 0.4f);
        int kills = 0;
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            if (new Vector2(e.GlobalPosition.X - GlobalPosition.X, e.GlobalPosition.Z - GlobalPosition.Z).Length() < rad + e.Radius)
            {
                float hit = dmg; bool dcrit = false;
                if (Caster != null && GodotObject.IsInstanceValid(Caster)) hit = Caster.MinionStrike(dmg, out dcrit);   // crit + lifesteal per foe caught
                e.Hurt(hit, DamageType.Nature, true, dcrit); e.HitFrom(GlobalPosition); e.Poison(Caster != null ? Caster.PoisonDps() : 4f, 4f); e.Root(1.6f); Caster?.ComboFromSource();
                if (e.Remote ? e.Hp <= hit : e.Dead) kills++;   // host: real death; client: estimate from the synced HP (so a client-Verdant's charged detonation also refunds)
            }   // the blast roots + feeds combo
        }
        // legendary "Wildfire Bloom": each blast sets off nearby ents (the guard keeps it finite); chain kills count too
        if (Caster != null && GodotObject.IsInstanceValid(Caster) && Caster.MinionChain)
            foreach (var t in Caster.Ents.ToArray())
                if (t != null && t != this && GodotObject.IsInstanceValid(t) && GlobalPosition.DistanceTo(t.GlobalPosition) < rad + 0.5f) kills += t.Detonate();
        Game.I.DamageWorld(GlobalPosition, rad, dmg);   // (FIX) the ent blast breaks props in its radius too
        Game.I.VfxRing(GlobalPosition, col, rad + 0.5f, 0.4f);
        var v = new Vfx(); Game.I.AddChild(v); v.GlobalPosition = GlobalPosition + Vector3.Up * 0.6f;
        v.Init(new SphereMesh { Radius = 2.8f, Height = 5.6f }, col, 0.4f, 6f);
        QueueFree();
        return kills;
    }

    // --- ally-unit damage/heal API (owner-side; ghosts ignore — their HP is driven by the synced fraction) ---
    public void Hurt(float dmg)
    {
        if (Ghost || _dead || MaxHp <= 0f || dmg <= 0f) return;
        if (Caster != null && GodotObject.IsInstanceValid(Caster) && Caster.BarkActive) return;   // Barkskin shields the whole grove
        Hp -= dmg; _hurtPunch = 0.15f;
        if (Hp <= 0f) Die();
    }
    public void Heal(float amt)
    {
        if (Ghost || _dead || MaxHp <= 0f || amt <= 0f) return;
        Hp = Mathf.Min(MaxHp, Hp + amt * (_blessT > 0f ? 1.6f : 1f));   // (NEW) bless amplifies healing received
    }
    public void Slow(float dur) { if (!Ghost) _slowT = Mathf.Max(_slowT, dur); }
    public void Bless(float dur) { if (!Ghost) _blessT = Mathf.Max(_blessT, dur); }   // (NEW) friendly holy blessing
    private void Die()
    {
        if (_dead) return; _dead = true;
        var col = new Color(0.5f, 0.78f, 0.42f);
        float drad = 3.0f * (Caster != null ? Caster.S.SpellArea : 1f);
        // death throes: a WEAK pop on its own, deliberately a fraction of a charged detonation and with
        // no root/chain/crit/lifesteal — so actively detonating ents (full-charge right-click) stays the
        // real payoff (~5x the damage + utility), not something you get for free by letting them die.
        if (!Ghost && !_detonated && Game.I != null)
        {
            float dpop = (Caster != null && GodotObject.IsInstanceValid(Caster)) ? Caster.MinionBurst() * 0.22f : 10f;
            foreach (var e in Game.I.Enemies.ToArray())
            {
                if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
                if (new Vector2(e.GlobalPosition.X - GlobalPosition.X, e.GlobalPosition.Z - GlobalPosition.Z).Length() < drad + e.Radius)
                { e.Hurt(dpop, DamageType.Nature, true); e.HitFrom(GlobalPosition); e.Poison(Caster != null ? Caster.PoisonDps() * 0.5f : 2f, 3f); }
            }
        }
        Game.I?.VfxRing(GlobalPosition, col, drad - 0.6f, 0.3f);            // smaller, dimmer than a detonation nova
        var v = new Vfx(); Game.I?.AddChild(v); v.GlobalPosition = GlobalPosition + Vector3.Up * 0.6f;
        v.Init(new SphereMesh { Radius = 1.1f, Height = 2.2f }, col, 0.3f, 5f);
        if (!Ghost && Game.I != null && GD.Randf() < 0.5f) Say(GD.Randf() < 0.5f ? "Auuugh!" : "For... the grove...", 2, col);
        QueueFree();   // the witch's CountEnts prunes the freed ent next snapshot
    }
    private bool _detonated = false;

    private static ulong _lastSayMs = 0;
    // owner-side: throttle, speak locally, and broadcast so allies see/hear it on the synced ghost
    private void Say(string text, int voiceKind, Color col)
    {
        if (Ghost || Game.I == null) return;
        ulong now = Time.GetTicksMsec();
        if (now - _lastSayMs < 750) return;
        _lastSayMs = now;
        SpeakAt(GlobalPosition, text, voiceKind, col);
        Game.I.NetMgr?.BroadcastMinionSay(GlobalPosition, text, voiceKind, col);
    }

    // renders the floating speech line + plays the squeaky voice at a world position (called locally AND from the say RPC)
    public static void SpeakAt(Vector3 pos, string text, int voiceKind, Color col)
    {
        if (Game.I == null) return;
        Game.I.Sfx?.Minion(voiceKind);
        var lbl = new Label3D
        {
            Text = text, FontSize = 64, PixelSize = 0.0045f,
            Modulate = col, OutlineSize = 14, OutlineModulate = new Color(0, 0, 0, 0.95f),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true, RenderPriority = 10
        };
        Game.I.AddChild(lbl);
        lbl.GlobalPosition = pos + new Vector3(0, 2.1f, 0);
        var tw = lbl.CreateTween(); tw.SetParallel(true);
        tw.TweenProperty(lbl, "global_position", lbl.GlobalPosition + new Vector3(0, 1.0f, 0), 1.2f);
        tw.TweenProperty(lbl, "modulate:a", 0f, 1.2f).SetDelay(0.45f);
        tw.SetParallel(false);
        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(lbl)) lbl.QueueFree(); }));
    }
}
