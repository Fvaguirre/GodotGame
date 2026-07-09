using Godot;
using System.Collections.Generic;

// Bolt.cs — the universal projectile (every witch bolt, Lunar crescent, Holy mote, Verdant needle &
// wood spike). _Ready builds the visual by DType and by Style (0 normal sphere, 1 purple needle,
// 2 knotted wood spike, oriented to Vel). _Process moves/homes/grows, checks collisions, and on hit
// calls Enemy.Hurt then applies ON-HIT RIDERS: Poison (additive), RootOnHit, and DetonatesEnts
// (scans Src.Ents in flight). Remote=true is a visual-only ghost spawned from BroadcastPBolt.
//
// To add an on-hit effect: add a field, set it via a new SpawnBolt parameter, apply it next to the
// e.Hurt(...) call, and if it's visual thread it through BroadcastPBolt/ReceivePBolt (as Style is).
// Crit is rolled in Player.SpawnBolt, not here. See DEV_GUIDE.md §6.4.
public partial class Bolt : Node3D
{
    public Vector3 Vel;
    public float Life = 1.6f;
    public float Dmg = 10f;
    public bool Crit = false;
    public float Radius = 0.5f;
    public Color Tint = Palette.Lunar;
    public int Pierce = 0;
    public bool FrostSpear = false, FrostSpearFull = false;   // (NEW) frost witch icicle spear: seeds freeze; full charge shatters frozen
    public float FreezeOnHit = 0f;
    public bool Normal = true;
    public bool Charged = false;
    public bool ComboShot = true;
    public bool Homing = false;
    public bool Full = false;
    private bool _modsFired = false;   // (FIX) charged modifiers fire only on the first foe hit
    public bool FromCombo = false;
    public float Turn = 6f;
    public float HomeSpeed = 0f;          // if >0, homing maintains this speed
    public float HomeDelay = 0f;          // ballistic arch before homing engages
    public float Gravity = 0f;            // applied during the arch
    public Enemy Target = null;           // latched individual target
    public bool SeekLockedOnly = false;   // only home toward the latched Target; if it's gone, fly straight (no re-acquire)
    public Vector3 AimFallback = Vector3.Zero;
    public Player Src;
    public DamageType DType = DamageType.Lunar;
    public bool Remote = false;   // client visual copy: travels + animates, no collision/damage
    public bool Horizontal = false;   // crescent lies flat (Lunar full-charge sweep)
    public float Grow = 0f;           // Radius (and visual) expands per second
    public float Poison = 0f, PoisonDur = 2.5f;   // poison-on-hit (Verdant thorns)
    public int Style = 0;             // 0 normal, 1 purple needle, 2 knotted wood spike
    public float RootOnHit = 0f;      // root duration applied to enemies hit (Verdant full-charge thorn)
    public bool DetonatesEnts = false;// passes through her own ents and blows them up
    private bool _entRefunded = false;// a charged detonation refunds at most one ent
    public float SpeedMul = 1f;       // applied to homing cruise speed (initial Vel is pre-scaled by ProjSpeed)
    public bool Forked = false;       // a Twin Light fork — won't fork again
    public bool RadiantHeal = false;  // (NEW) Divine Radiant Ascension: mend allies this mote passes through (once each)
    public float HealAmt = 0f;
    private bool _didHeal = false;
    private Node3D _crescent;
    private float _baseRadius = 0.5f;
    private bool _holyRay = false;    // (NEW) charged Holy right-click: drop a flickering ground scorch as it flies
    private float _scorchT = 0f;      // (NEW) scorch-drop throttle
    private float _age = 0f;          // (NEW) time alive — arms ground contact so a bolt can't self-mark at the muzzle

    private readonly HashSet<ulong> _hit = new();

    public override void _Ready()
    {
        if (Style == 3)   // (NEW) icy ballista arrow: long shaft, faceted head, fletching at the tail
        {
            var holder = new Node3D(); AddChild(holder);
            if (Vel.LengthSquared() > 0.001f) { var v = Vel.Normalized(); holder.Rotation = new Vector3(-Mathf.Asin(Mathf.Clamp(v.Y, -1f, 1f)), Mathf.Atan2(v.X, v.Z), 0f); }
            var ice = Game.Emissive(new Color(0.72f, 0.9f, 1f), 2.6f);
            float s = 0.6f + Radius * 1.3f;   // charge → bigger bolt
            var shaft = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.05f * s, BottomRadius = 0.06f * s, Height = 2.4f * s, RadialSegments = 6 }, MaterialOverride = ice };
            shaft.RotationDegrees = new Vector3(90, 0, 0); shaft.Position = new Vector3(0, 0, -0.1f * s); holder.AddChild(shaft);
            var head = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.2f * s, Height = 0.7f * s, RadialSegments = 4 }, MaterialOverride = ice };
            head.RotationDegrees = new Vector3(-90, 0, 0); head.Position = new Vector3(0, 0, -1.55f * s); holder.AddChild(head);   // point faces forward (-Z)
            for (int i = 0; i < 3; i++)   // fletching splayed around the tail
            {
                var pivot = new Node3D { Rotation = new Vector3(0, 0, i / 3f * Mathf.Tau) }; holder.AddChild(pivot);
                var fin = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.03f * s, 0.4f * s, 0.45f * s) }, MaterialOverride = ice };
                fin.Position = new Vector3(0.16f * s, 0f, 0.95f * s); pivot.AddChild(fin);
            }
            holder.AddChild(new OmniLight3D { OmniRange = 4.5f, LightColor = new Color(0.7f, 0.9f, 1f), LightEnergy = 1.6f });
            return;
        }
        if (Style == 1 || Style == 2)
        {
            var holder = new Node3D();
            AddChild(holder);
            // orient the spike along its travel direction
            if (Vel.LengthSquared() > 0.001f)
            {
                var v = Vel.Normalized();
                float yaw = Mathf.Atan2(v.X, v.Z);
                float pitch = -Mathf.Asin(Mathf.Clamp(v.Y, -1f, 1f));
                holder.Rotation = new Vector3(pitch, yaw, 0);
            }
            if (Style == 1)   // purple poison needle — thin, long, reads as a straight line in flight
            {
                var pur = new Color(0.62f, 0.26f, 0.85f);
                var nmat = Game.ElementBoltMat(pur, DamageType.Arcane);   // (NEW) shader surface (Arcane sparkle) on the same needle — colour/shape unchanged
                var needle = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.028f, Height = 1.5f }, MaterialOverride = nmat };
                needle.RotationDegrees = new Vector3(90, 0, 0);          // lie along local -Z (forward)
                needle.Position = new Vector3(0, 0, -0.25f);
                holder.AddChild(needle);
                AddChild(new OmniLight3D { OmniRange = 3f, LightColor = pur, LightEnergy = 1.1f });
            }
            else   // (NEW look) living ROOT-LANCE: a writhing root spearing forward — a tapered shaft with tendrils
            {      // corkscrewing around it (spun in flight so it reads as alive) and a couple of gnarled root-nodes.
                var woodMat = Game.ElementBoltMat(new Color(0.42f, 0.3f, 0.17f), DamageType.Nature);   // Nature shader surface
                var glowCol = new Color(0.4f, 0.85f, 0.4f);
                float s = 0.5f + Radius * 0.2f;
                var parts = new System.Collections.Generic.List<MeshInstance3D>();
                var shaft = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.34f * s, Height = 2.0f * s }, MaterialOverride = woodMat };
                shaft.RotationDegrees = new Vector3(90, 0, 0);        // point along local -Z (travel)
                shaft.Position = new Vector3(0, 0, -0.25f);
                shaft.Transparency = 1f;                             // fades in from the muzzle
                holder.AddChild(shaft); parts.Add(shaft);
                var spinner = new Node3D();                          // tendrils spin as a group = writhing corkscrew
                holder.AddChild(spinner);
                for (int i = 0; i < 4; i++)   // writhing tendrils splayed around the shaft, angled toward the tail
                {
                    var tend = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.09f * s, Height = (1.1f + GD.Randf() * 0.5f) * s }, MaterialOverride = woodMat };
                    float a = i / 4f * Mathf.Tau;
                    tend.RotationDegrees = new Vector3(60f + GD.Randf() * 15f, Mathf.RadToDeg(a), 0f);
                    tend.Position = new Vector3(Mathf.Cos(a) * 0.16f * s, Mathf.Sin(a) * 0.16f * s, (0.35f + GD.Randf() * 0.3f) * s);
                    tend.Transparency = 1f;
                    spinner.AddChild(tend); parts.Add(tend);
                }
                for (int i = 0; i < 2; i++)   // gnarled root-nodes
                {
                    var knot = new MeshInstance3D { Mesh = new SphereMesh { Radius = (0.12f + GD.Randf() * 0.08f) * s, Height = 0.24f * s }, MaterialOverride = woodMat };
                    float a = GD.Randf() * Mathf.Tau;
                    knot.Position = new Vector3(Mathf.Cos(a) * 0.24f * s, Mathf.Sin(a) * 0.24f * s, (0.2f - GD.Randf() * 0.7f) * s);
                    knot.Transparency = 1f;
                    spinner.AddChild(knot); parts.Add(knot);
                }
                holder.AddChild(new OmniLight3D { OmniRange = 5f, LightColor = glowCol, LightEnergy = 1.4f });
                var spin = spinner.CreateTween(); spin.SetLoops();   // corkscrew the tendrils around the shaft in flight
                spin.TweenProperty(spinner, "rotation", new Vector3(0, 0, Mathf.Tau), 0.9f);
                var ft = CreateTween(); ft.SetParallel(true);        // fade each part in via instance transparency
                foreach (var mpart in parts) ft.TweenProperty(mpart, "transparency", 0f, 0.12f).SetDelay(0.1f);
            }
            AddChild(Game.MakeCometTrail(Tint));   // (NEW) comet tail for needle/wood too
            return;
        }
        if (DType == DamageType.Lunar)
        {
            // RAZOR-THIN GHOSTLY CRESCENT: a flat curved blade with sharp tips that flies straight and (full charge)
            // widens as it travels. Lays flat/parallel to the sky when Horizontal. (NEW — replaced the sphere-blob)
            float scale = 0.30f + Radius * 0.28f;
            var holder = new Node3D();
            AddChild(holder);
            _crescent = holder;
            _baseRadius = Mathf.Max(0.1f, Radius);
            if (Horizontal) holder.RotationDegrees = new Vector3(90, 0, 0);   // lay flat — parallel to the sky
            var blade = new MeshInstance3D
            {
                Mesh = Game.CrescentBladeMesh(),
                MaterialOverride = Game.CrescentBladeMat(Tint.Lerp(Colors.White, 0.7f)),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Scale = new Vector3(scale, scale, scale)
            };
            holder.AddChild(blade);
            var ctrail = Game.MakeCometTrail(new Color(1f, 1f, 1f));   // (NEW) bright white trail, beefed up for the big blade
            ctrail.Amount = 30; ctrail.Lifetime = 0.36; ctrail.ScaleAmountMin = 1.3f; ctrail.ScaleAmountMax = 2.3f;
            AddChild(ctrail);
        }
        else if (DType == DamageType.Holy)
        {
            if (Charged)
            {
                // HOLY RAY FROM ABOVE (right-click): a warm beam descends from the sky and sweeps along the GROUND in
                // the cast direction, searing a flickering trail. (NEW — now travels at ground level, so it hits ground foes.)
                _holyRay = true;
                if (new Vector2(Vel.X, Vel.Z).Length() > 0.01f)
                    Vel = new Vector3(Vel.X, 0f, Vel.Z).Normalized() * Vel.Length();   // flatten to a horizontal sweep
                var beam = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.28f, BottomRadius = 0.5f, Height = 30f } };   // slight flare, base kept clear of the ground
                beam.MaterialOverride = Game.HolyRayMat();
                beam.Position = new Vector3(0, 15.7f, 0);   // base sits ~0.7 above the ground so hills don't cut through the beam
                beam.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
                AddChild(beam);
                var rayCore = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.5f, Height = 1.0f } };   // bright impact flare at the ground
                rayCore.MaterialOverride = Game.ElementBoltMat(Tint.Lerp(Colors.White, 0.7f), DamageType.Holy);
                AddChild(rayCore);
            }
            else
            {
                // soft light mote (primary homing mote) — a bright core wrapped in a faint halo
                float core = 0.22f + Radius * 0.18f;
                var hmat = Game.ElementBoltMat(Tint.Lerp(Colors.White, 0.5f), DamageType.Holy);
                var inner = new MeshInstance3D { Mesh = new SphereMesh { Radius = core, Height = core * 2f } };
                inner.MaterialOverride = hmat;
                AddChild(inner);
                var halo = new MeshInstance3D { Mesh = new SphereMesh { Radius = core * 2.1f, Height = core * 4.2f } };
                var hm = new StandardMaterial3D
                {
                    AlbedoColor = new Color(Tint.R, Tint.G, Tint.B, 0.25f),
                    EmissionEnabled = true, Emission = Tint, EmissionEnergyMultiplier = 0.8f,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
                };
                halo.MaterialOverride = hm;
                AddChild(halo);
            }
        }
        else
        {
            var mi = new MeshInstance3D();
            mi.Mesh = new SphereMesh { Radius = 0.26f + Radius * 0.2f, Height = 0.52f + Radius * 0.4f };
            mi.MaterialOverride = Game.ElementBoltMat(Tint, DType);   // (NEW) per-element animated shader (was flat ToonEmissive)
            AddChild(mi);
        }
        AddChild(new OmniLight3D { OmniRange = 5f, LightColor = Tint, LightEnergy = 1.4f });
        if (DType != DamageType.Lunar) AddChild(Game.MakeCometTrail(Tint));   // Lunar crescent adds its own bigger white trail above (NEW)
    }

    public override void _Process(double delta)
    {
        if (Game.I == null || Game.I.State != GameState.Playing) return;
        float dt = (float)delta;
        _age += dt;

        // holy descending ray: lay down a flickering warm scorch on the ground it passes over (cosmetic) (NEW)
        if (_holyRay)
        {
            float g0 = Game.I.SurfaceHeight(GlobalPosition, GlobalPosition.Y);
            GlobalPosition = new Vector3(GlobalPosition.X, g0 + 0.1f, GlobalPosition.Z);   // hug the ground — the ray sweeps along it
            _scorchT -= dt;
            if (_scorchT <= 0f)
            {
                _scorchT = 0.05f;
                var d = Game.MakeHolyScorch();
                float sz = 1.1f + GD.Randf() * 0.9f;
                d.Size = new Vector3(sz, 4f, sz);
                float baseE = 2.4f + GD.Randf() * 1.6f;
                d.EmissionEnergy = baseE;
                Game.I.AddChild(d);
                // centre the decal box on the ground; it projects DOWN onto the terrain, conforming to any slope (no clipping)
                d.GlobalPosition = new Vector3(GlobalPosition.X, g0 + 1.5f, GlobalPosition.Z);
                var flick = d.CreateTween().SetLoops();   // candle flicker on the glow
                flick.TweenProperty(d, "emission_energy", baseE * 0.55f, 0.09f);
                flick.TweenProperty(d, "emission_energy", baseE * 1.15f, 0.07f);
                var fade = d.CreateTween();               // fade the mark out, then free (also kills the loop above)
                fade.TweenProperty(d, "modulate:a", 0f, 0.8f);
                fade.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(d)) d.QueueFree(); }));
            }
        }

        if (HomeDelay > 0f)
        {
            HomeDelay -= dt;
            if (Gravity != 0f) Vel = new Vector3(Vel.X, Vel.Y - Gravity * dt, Vel.Z);
        }
        else if (Homing)
        {
            float spd = HomeSpeed > 0 ? HomeSpeed * SpeedMul : Vel.Length();

            // keep our latched target if it's still alive, else grab the nearest
            Enemy best = (Target != null && GodotObject.IsInstanceValid(Target) && !Target.Dead) ? Target : null;
            if (best == null && SeekLockedOnly)
            {
                Homing = false;   // locked target is gone (or none was ever set) — continue straight
            }
            else
            {
                if (best == null)
                {
                    float bd = 1e9f;
                    foreach (var e in Game.I.Enemies)
                    {
                        if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
                        float d = GlobalPosition.DistanceTo(e.GlobalPosition);
                        if (d < bd) { bd = d; best = e; }
                    }
                    Target = best;
                }

                Vector3 want;
                if (best != null)
                {
                    var to = best.GlobalPosition - GlobalPosition;
                    want = to.LengthSquared() > 0.04f ? to.Normalized() * spd : (Vel.LengthSquared() > 0.01f ? Vel.Normalized() : Vector3.Forward) * spd;
                }
                else if (AimFallback != Vector3.Zero) want = AimFallback.Normalized() * spd;
                else want = (Vel.LengthSquared() > 0.01f ? Vel.Normalized() : Vector3.Forward) * spd;
                Vel = Vel.Lerp(want, Mathf.Clamp(Turn * dt, 0, 1));
            }
        }

        GlobalPosition += Vel * dt;
        Game.I.SmashNear(GlobalPosition, Radius + 0.8f);   // player bolts shatter pumpkins they pass through (NEW)
        if (GD.Randf() < 0.25f) { Game.I.GlowFlowersNear(GlobalPosition, Radius + 1.5f); Game.I.WaterTouch(GlobalPosition, 0f); }   // light flowers + ripple water beneath the spell (throttled) (NEW)
        Life -= dt;

        if (Grow > 0f)
        {
            Radius = Mathf.Min(Radius + Grow * dt, 5.5f);   // widen as it travels (catches more foes); capped
            if (_crescent != null) { float k = Radius / _baseRadius; _crescent.Scale = new Vector3(k, k, k); }
        }

        if (!Remote)
        {
            if (DetonatesEnts && Src != null && GodotObject.IsInstanceValid(Src) && Src.VerdantWitch)
            {
                int dk = 0;
                foreach (var t in Src.Ents.ToArray())
                    if (t != null && GodotObject.IsInstanceValid(t) && GlobalPosition.DistanceTo(t.GlobalPosition) < 1.9f) dk += t.Detonate();
                if (dk > 0 && !_entRefunded) { _entRefunded = true; Src.RefundEnt(); }   // a charged detonation that scores a kill (incl. chain) refunds ONE ent
            }
            if (RadiantHeal && !_didHeal && Src != null && Game.I.NetMgr != null && Game.I.NetMgr.Active)   // (NEW) Radiant Ascension: mend allies this mote flies through, once each, then carry on to the foe behind
                if (Game.I.NetMgr.HealAlliesNear(GlobalPosition, 1.7f, HealAmt)) _didHeal = true;
            var enemies = Game.I.Enemies.ToArray();   // (FIX) snapshot — a hit can kill/spawn and mutate the live list
            for (int i = enemies.Length - 1; i >= 0; i--)
            {
                var e = enemies[i];
                if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
                if (_hit.Contains(e.GetInstanceId())) continue;
                bool hitE;
                if (e.IsBoss)   // tall bosses: hittable up the whole body (cylinder), so the head + shoulder goblins can be struck
                {
                    float flat = new Vector2(GlobalPosition.X - e.GlobalPosition.X, GlobalPosition.Z - e.GlobalPosition.Z).Length();
                    float dy = GlobalPosition.Y - e.GlobalPosition.Y;
                    hitE = flat < e.Radius + Radius + 0.6f && dy > -(e.Radius + 0.6f) && dy < e.Radius * 3f;
                }
                else hitE = e.HitBy(GlobalPosition, Radius + 0.4f);   // (FIX) capsule spanning the whole body — was a sphere at the feet, so tall foes could only be hit low
                if (hitE)
                {
                    _hit.Add(e.GetInstanceId());
                    bool bcrit = Crit; float bdmg = Dmg;
                    if (!bcrit && Src != null && e.IsCritZone(GlobalPosition)) { bdmg *= Src.CritMult(); bcrit = true; }   // (NEW) head / shoulder-goblin hits always crit THE HOLLOW MOON
                    if (bcrit) e.CritHitReact(GlobalPosition);   // boss/goblin yelp (crit ping is played centrally in Hurt)
                    if (FrostSpear && FrostSpearFull && e.Frozen) { e.ShatterInstant(); Game.I.MyStats.Highlight++; }   // (NEW) full-charge / Glacial Impaler shatters a frozen foe (+ Frost highlight)
                    else e.Hurt(bdmg, DType, FromCombo, bcrit);   // the spear just damages — the BEAM does the freezing
                    if (Vel.LengthSquared() > 0.01f) e.HitFrom(GlobalPosition - Vel.Normalized() * 25f);   // (NEW) idle zombie turns + investigates up the shot line
                    {   // (NEW) impact mark ON the enemy surface, facing outward, parented so it moves with them
                        var mn = GlobalPosition - e.GlobalPosition; mn.Y *= 0.35f;
                        mn = mn.LengthSquared() > 0.0001f ? mn.Normalized() : Vector3.Up;
                        Game.I.SpawnImpactMark(e.GlobalPosition + mn * (e.Radius * 0.9f), mn, e, DType, Radius);
                    }
                    if (Style == 2) Game.I.SpawnBrambleBurst(e.GlobalPosition, 0.8f, 3);   // (NEW) speared foes sprout brambles
                    if (Poison > 0f) e.Poison(Poison, PoisonDur);   // Verdant thorns: additive poison + slow
                    if (RootOnHit > 0f) e.Root(RootOnHit);          // full-charge thorn roots
                    Src?.OnHit(e, e.Dead, this);
                    if (Full && !_modsFired) { Src?.ApplyChargedMods(GlobalPosition); _modsFired = true; }   // (FIX) mods trigger once, on the first foe hit — not per pierced enemy
                    if (Pierce > 0) { Pierce--; }
                    else { Game.I.Sfx?.Impact(DType); QueueFree(); return; }
                }
            }
        }

        // (NEW) ground contact — a flat mark where the bolt meets the terrain, then it disappears (the cosmetic
        // holy ray is exempt; a short arming window stops self-marking at the muzzle). No exceptions for the crescent.
        if (!_holyRay && _age > 0.05f)
        {
            float gy = Game.I.SurfaceHeight(GlobalPosition, GlobalPosition.Y);
            if (GlobalPosition.Y <= gy + 0.05f)
            {
                Game.I.SpawnImpactMark(new Vector3(GlobalPosition.X, gy + 0.02f, GlobalPosition.Z), Vector3.Up, null, DType, Radius);
                if (Style == 2) Game.I.SpawnBrambleBurst(new Vector3(GlobalPosition.X, gy, GlobalPosition.Z), 1.1f, 6);   // (NEW) landing erupts a bramble ring
                QueueFree(); return;
            }
        }

        foreach (var bl in Game.I.Blockers)
            if (new Vector2(GlobalPosition.X - bl.Pos.X, GlobalPosition.Z - bl.Pos.Z).Length() < bl.Radius)
            {   // mark on the structure/tree surface, then disappear
                var bn = new Vector3(GlobalPosition.X - bl.Pos.X, 0f, GlobalPosition.Z - bl.Pos.Z);
                bn = bn.LengthSquared() > 0.0001f ? bn.Normalized() : Vector3.Back;
                Game.I.SpawnImpactMark(new Vector3(bl.Pos.X + bn.X * bl.Radius, GlobalPosition.Y, bl.Pos.Z + bn.Z * bl.Radius), bn, null, DType, Radius);
                if (Style == 2) Game.I.SpawnBrambleBurst(GlobalPosition, 1.0f, 5);   // (NEW) brambles climb the struck surface
                QueueFree(); return;
            }

        var pl = Game.I.Player;
        float far = pl != null ? GlobalPosition.DistanceTo(pl.GlobalPosition) : 0f;
        if (Life <= 0 || far > 95f)
            QueueFree();
    }
}
