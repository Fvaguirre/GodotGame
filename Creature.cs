using Godot;
using System.Collections.Generic;

// Creature.cs — procedural enemy bodies. CreatureKind (below) is the silhouette family; Enemy._Ready
// maps each enemy type string to a kind (e.g. brute/boss -> Orc, flyer/diver -> Mosquito). Add a new
// kind here only if a new enemy needs a distinct body; otherwise reuse an existing one. Handles the
// mesh build + walk/attack animation for the enemy.
public enum CreatureKind { Goblin, Orc, Spider, Mosquito, Bomber, Zapper, Zombie, HollowBoss, Crocodile, Troll, Pigmy, Pterodactyl, Bat, Snake, Taker, Withered }   // Crocodile + jungle set (NEW); Taker = authored kidnapper w/ full action-clip set (NEW); Withered = authored spellcaster (caster/stunner/healer/empowerer) (NEW)

// Procedurally-built, procedurally-animated enemy models (primitive-based, but layered limbs with a
// walk/flap cycle and knee-bend so they read as creatures). Each instance is randomly varied so no
// two goblins/orcs look identical.
public partial class Creature : Node3D
{
    private CreatureKind _kind;
    private float _scale = 1f;
    private float _phase;
    private float _wing;
    private Node3D _body;
    private float _bodyBaseY;
    private readonly List<Node3D> _hips = new();
    private Creature _gobZombie, _gobNormal;   // (NEW) HollowBoss shoulder riders (real Zombie + Goblin creatures)
    private float _gobFireZ = 0f, _gobFireN = 0f;   // brief cast-lunge timers when each goblin fires
    public float StompWind = 0f;               // (NEW) 0..1 leg-lift during the stomp wind-up
    public void FireShoulder(bool zombie) { if (zombie) { _gobFireZ = 1f; _gobZombie?.Strike(); } else { _gobFireN = 1f; _gobNormal?.Strike(); } }
    public Vector3 ShoulderPos(bool zombie) { var g = zombie ? _gobZombie : _gobNormal; return (g != null && GodotObject.IsInstanceValid(g)) ? g.GlobalPosition : GlobalPosition; }
    public void PopGoblins() { if (_gobZombie != null && GodotObject.IsInstanceValid(_gobZombie)) _gobZombie.QueueFree(); if (_gobNormal != null && GodotObject.IsInstanceValid(_gobNormal)) _gobNormal.QueueFree(); }
    public void TopplePose(float pitch) { if (_body != null) _body.RotationDegrees = new Vector3(pitch, _body.RotationDegrees.Y, _body.RotationDegrees.Z); }
    private readonly List<Node3D> _knees = new();
    private readonly List<Vector3> _hipBase = new();
    private readonly List<Vector3> _kneeBase = new();
    private readonly List<Node3D> _arms = new();
    private readonly List<Node3D> _wings = new();
    private Node3D _keg;   // bomber
    private MeshInstance3D _orb;   // zapper focus
    private float _cast, _castTarget;
    private float _swing, _swingTarget, _strike;   // melee wind-up amount (0..1) + brief forward strike impulse

    // ---- authored goblin (GLB mesh + baked walk anim + procedural slash) ----
    private bool _gobAuthored;
    private AnimationPlayer _gobAp; private string _gobWalkKey;
    private Skeleton3D _gobSkel; private GoblinSlashMod _gobSlash, _gobSlash2;   // _gobSlash2 = the second arm for the two-hand zombie chop
    private int _gobArmL = -1, _gobArmR = -1, _gobForeL = -1, _gobForeR = -1;
    private bool _gobSlashLeft;   // which arm the CURRENT strike uses (randomized per Strike, single-arm goblin)
    private bool _gobBothArms;    // (NEW) zombie: chop with BOTH arms at once (no random pick), doubled hit
    private MeshInstance3D _slashVfx, _slashVfx2; private float _slashVfxT; private const float SlashVfxDur = 0.32f;
    // ---- biped action-clip state machine (authored goblin/zombie/ogre/taker share the SAME rig, so one clip library drives all) ----
    private readonly System.Collections.Generic.Dictionary<string, string> _bClip = new();   // canonical name → playable AnimationPlayer key
    private ZombieReachMod _reachMod; private float _bReach, _bReachTarget;   // taker grab-arms telegraph (0..1)
    private WinceMod _winceMod; private float _wince; private int _winceVar;   // procedural hurt flinch on a direct hit (impulse → decays)
    private enum BState { Loco, Climb, Airborne, WallDown, StandUp, Attack }   // Attack = the boss's one-shot attack/death clips
    private BState _bState = BState.Loco;
    private bool _bRun;                 // Loco uses the run clip (taker dash) instead of walk
    private string _bPlaying = "";      // canonical key currently playing (so we only Play() on a change)
    private string _bStandKey = "standup4";   // which get-up clip the pending StandUp will use
    private int _bAirPhase = 0;         // Airborne: 0 = climb-slip lead-in (climbfall), 1 = free-fall loop (fall1)
    private bool _bOneShotDone;         // a WallDown/StandUp one-shot has reached its end
    private float _scream;                          // (NEW) zombie shriek-to-sky overlay (arms up, lean back)
    public void Scream() { _scream = 1f; }
    public int IdlePose = 0;                        // (NEW) 0 stand, 1 lie on floor, 2 slump, 3 snicker (idle swarmers)
    public bool AnimSuspended = false;              // (PERF) set by Enemy when far + off-camera → skip the skeletal pose writes entirely

    private void ZombieIdlePose()   // lie / slump / snicker (idle, non-alerted swarmers)
    {
        if (_body == null) return;
        switch (IdlePose)
        {
            case 1:   // sprawled on the floor
                _body.RotationDegrees = new Vector3(-82f, 90f, Mathf.Sin(_phase * 0.5f) * 2f);
                _body.Position = new Vector3(0, _bodyBaseY * 0.22f, _bodyBaseY * 0.45f);
                for (int i = 0; i < _hips.Count; i++) { _hips[i].RotationDegrees = new Vector3(-78f, 0, 0); if (_knees[i] != null) _knees[i].RotationDegrees = new Vector3(22f, 0, 0); }
                for (int i = 0; i < _arms.Count; i++) _arms[i].RotationDegrees = new Vector3(8f, 0, (i == 0 ? 62f : -62f));
                break;
            case 2:   // slumped against a wall, head hung
                _body.RotationDegrees = new Vector3(-36f, 0, Mathf.Sin(_phase * 0.4f) * 2f);
                _body.Position = new Vector3(0, _bodyBaseY * 0.55f, 0);
                for (int i = 0; i < _hips.Count; i++) { _hips[i].RotationDegrees = new Vector3(-68f, 0, (i == 0 ? 12f : -12f)); if (_knees[i] != null) _knees[i].RotationDegrees = new Vector3(82f, 0, 0); }
                for (int i = 0; i < _arms.Count; i++) _arms[i].RotationDegrees = new Vector3(14f + Mathf.Sin(_phase * 0.5f + i) * 3f, 0, (i == 0 ? 8f : -8f));
                break;
            default:  // 3: standing snicker — shoulders shuddering, head tilted
                float sh = Mathf.Sin(_phase * 14f) * 3f;
                _body.RotationDegrees = new Vector3(22f, 8f, sh);
                _body.Position = new Vector3(0, _bodyBaseY + Mathf.Abs(Mathf.Sin(_phase * 7f)) * _scale * 0.02f, 0);
                for (int i = 0; i < _hips.Count; i++) { _hips[i].RotationDegrees = Vector3.Zero; if (_knees[i] != null) _knees[i].RotationDegrees = new Vector3(4f, 0, 0); }
                for (int i = 0; i < _arms.Count; i++) _arms[i].RotationDegrees = new Vector3(-18f + sh, 0, (i == 0 ? 12f : -12f));
                break;
        }
    }
    public void SetCast(float t) { _castTarget = Mathf.Clamp(t, 0f, 1f); }
    public void SetSwing(float t) { _swingTarget = Mathf.Clamp(t, 0f, 1f); }
    public void Strike()
    {
        _strike = 1f;
        if (_gobAuthored && _gobSlash != null)
        {
            if (_gobBothArms)   // (ZOMBIE) chop with BOTH arms simultaneously — left on mod 1, right on mod 2
            {
                _gobSlash.Arm = _gobArmL; _gobSlash.Fore = _gobForeL; _gobSlash.Side = -1f;
                if (_gobSlash2 != null) { _gobSlash2.Arm = _gobArmR; _gobSlash2.Fore = _gobForeR; _gobSlash2.Side = 1f; }
            }
            else   // (GOBLIN) pick + mirror + RANDOMIZE which single arm chops this strike
            {
                _gobSlashLeft = R(0f, 1f) < 0.5f;
                _gobSlash.Arm = _gobSlashLeft ? _gobArmL : _gobArmR;
                _gobSlash.Fore = _gobSlashLeft ? _gobForeL : _gobForeR;
                _gobSlash.Side = _gobSlashLeft ? -1f : 1f;
            }
            _slashVfxT = SlashVfxDur;   // fire the crescent arc(s)
        }
    }

    public bool IsAuthoredGoblin => _gobAuthored;
    // (harness) deterministically chop so a scenario can capture it: the zombie fires BOTH arms, the goblin the chosen side.
    public void DebugSlash(bool left)
    {
        if (!_gobAuthored || _gobSlash == null) return;
        if (_gobBothArms)
        {
            _gobSlash.Arm = _gobArmL; _gobSlash.Fore = _gobForeL; _gobSlash.Side = -1f;
            if (_gobSlash2 != null) { _gobSlash2.Arm = _gobArmR; _gobSlash2.Fore = _gobForeR; _gobSlash2.Side = 1f; }
        }
        else
        {
            _gobSlashLeft = left;
            _gobSlash.Arm = left ? _gobArmL : _gobArmR;
            _gobSlash.Fore = left ? _gobForeL : _gobForeR;
            _gobSlash.Side = left ? -1f : 1f;
        }
        _slashVfxT = SlashVfxDur;
        _strike = 1f; _swing = 0f; _swingTarget = 0f;
    }

    private const string GoblinGlb = "res://assets/models/enemies/goblin.glb";
    private const string ZombieGlb = "res://assets/models/enemies/zombie.glb";   // (NEW) the rigged zombie-goblin (same Meshy biped skeleton as the goblin)
    private const string OgreGlb = "res://assets/models/enemies/ogre.glb";        // (NEW) the rigged buffoon ogre (same Meshy biped skeleton) — replaces the big procedural Orc
    private const string TakerGlb = "res://assets/models/enemies/taker.glb";      // (NEW) the kidnapper taker — ALSO the shared source of run/fall/climb/stand-up action clips for every biped
    private const string HollowGlb = "res://assets/models/enemies/hollow_man.glb";   // (NEW) THE HOLLOW MOON — same Meshy biped rig, 13 clips merged into one GLB (walk/cast1/cast6/gripthrow/stomp/charge/death/…)
    private const string WitheredGlb = "res://assets/models/enemies/withered_king.glb";   // (NEW) THE WITHERED KING — the grove's spellcaster body; ALSO the shared source of the mage cast clips
    private bool AuthoredGoblin(float s, Material accent) => AuthoredBiped(GoblinGlb, s, accent, false);
    private bool AuthoredZombie(float s, Material accent) => AuthoredBiped(ZombieGlb, s, accent, true);
    // The ogre carries the mini-boss and the sieger, both of which throw projectiles — graft the withered king's mage cast
    // so their bolts have a real wind-up animation instead of firing out of a walk cycle.
    private bool AuthoredOgre(float s, Material accent) => AuthoredBiped(OgreGlb, s, accent, false, graftCast: true);   // single-arm slash, like the plain goblin
    private bool AuthoredTaker(float s, Material accent) => AuthoredBiped(TakerGlb, s, accent, false);  // single-arm punch + the full action set (run/fall/climb/stand-up)
    // The caster family (caster/stunner/healer/empowerer): slighter than a goblin and never size-varied into a giant, and it
    // ships its own cast clips so there's nothing to graft.
    private bool AuthoredWithered(float s, Material accent) => AuthoredBiped(WitheredGlb, s, accent, false, heightMul: 3.0f);
    // The boss: taller than his hitbox radius implies (his head must clear the Radius*1.9 crit band), never size-varied, and he
    // ships his OWN full clip set — so don't pay to load + retarget the taker's shared library mid-fight.
    private bool AuthoredHollow(float s, Material accent) => AuthoredBiped(HollowGlb, s, accent, false, heightMul: 3.6f, vary: false, graftShared: false, hollow: true);

    // Load an authored Meshy-biped GLB (mesh + baked walk), scale to match the old silhouette, play the walk, and set up the
    // procedural-slash modifier(s). bothArms = the zombie's two-hand chop (two slash mods). Returns false (→ procedural fallback)
    // if the asset/skeleton/anim is missing. Reused by both the goblin and the zombie (identical bone names: LeftArm/RightArm/…).
    private bool AuthoredBiped(string glbPath, float s, Material accent, bool bothArms,
                               float heightMul = 2.8f, bool vary = true, bool graftShared = true, bool hollow = false,
                               bool graftCast = false)
    {
        if (!ResourceLoader.Exists(glbPath)) return false;
        var model = ResourceLoader.Load<PackedScene>(glbPath)?.Instantiate<Node3D>();
        if (model == null) return false;
        AddChild(model);
        ModelAssets.Painterlify(model);   // opaque + matte (Meshy imports translucent/glossy)
        _gobSkel = ModelAssets.FindSkeleton(model);
        _gobAp = ModelAssets.FindAnimPlayer(model);
        if (_gobSkel == null || _gobAp == null) { model.QueueFree(); return false; }
        _gobAuthored = true;
        _gobBothArms = bothArms;
        _hollow = hollow;

        // Scale by the mesh's OWN (skin-space) AABB height — NOT the node-hierarchy one. This rig's Armature carries a 0.01
        // node scale that the SKINNED mesh ignores (bones compensate), so FitHeight (which measures through the node scale)
        // would size the goblin ~100× too big. The mesh resource AABB is in true skin space. Target ≈ the intended enemy visual
        // height (feet at −Radius, head ~Radius*1.9 up ⇒ ~Radius*2.9 tall) so the model fills its hitbox/ground-ring.
        float target = s * heightMul * (vary ? R(0.92f, 1.1f) : 1f);
        float nativeH = 1.7f;
        var meshInst = FindMesh(model);
        _gobMesh = meshInst;   // kept so phase 2 can clone it into a body-shaped aura shell
        if (meshInst?.Mesh != null) { float h = meshInst.Mesh.GetAabb().Size.Y; if (h > 0.05f) nativeH = h; }
        model.Scale = Vector3.One * (target / nativeH);
        // facing: the model's +Z (Meshy walk-forward) already aligns with the Creature's +Z (which Enemy yaws toward the target),
        // so NO extra rotation — the goblin faces + walks toward the player.

        // GROUND: the enemy origin sits Radius above the feet (Enemy: feet = GlobalPosition.Y − Radius), and this rig's model
        // origin is BELOW the feet — so shift the model down so its lowest foot bone lands at Creature-local −Radius (the ground).
        int fl = _gobSkel.FindBone("LeftFoot"), fr = _gobSkel.FindBone("RightFoot");
        if (fl >= 0 && fr >= 0)
        {
            var g = _gobSkel.GlobalTransform;
            float footWorld = Mathf.Min((g * _gobSkel.GetBoneGlobalPose(fl).Origin).Y, (g * _gobSkel.GetBoneGlobalPose(fr).Origin).Y);
            float footLocal = footWorld - GlobalPosition.Y;        // foot Y relative to the Creature origin (model currently at 0)
            model.Position = new Vector3(0, -s - footLocal, 0);
        }

        RegisterBipedClips(graftShared, graftCast);   // map this model's own clips + graft the SHARED action library (run/fall/climb/stand-up) so EVERY biped can play them
        if (_bClip.TryGetValue("walk", out var walkKey)) { _gobWalkKey = walkKey; _gobAp.Play(walkKey); }

        _gobArmL = _gobSkel.FindBone("LeftArm"); _gobForeL = _gobSkel.FindBone("LeftForeArm");
        _gobArmR = _gobSkel.FindBone("RightArm"); _gobForeR = _gobSkel.FindBone("RightForeArm");
        int spine = _gobSkel.FindBone("Spine01"); if (spine < 0) spine = _gobSkel.FindBone("Spine");
        _gobSlash = new GoblinSlashMod { Spine = spine };
        _gobSkel.AddChild(_gobSlash);
        if (bothArms)   // (ZOMBIE) a second modifier for the other arm; Spine=-1 so the torso lean isn't applied twice
        {
            _gobSlash2 = new GoblinSlashMod { Spine = -1 };
            _gobSkel.AddChild(_gobSlash2);
        }
        // (TAKER) both-arms-forward grab telegraph — a no-op at Reach 0, so it's harmless on the goblin/zombie/ogre that never grab
        _reachMod = new ZombieReachMod { ArmL = _gobArmL, ForeL = _gobForeL, ArmR = _gobArmR, ForeR = _gobForeR };
        _gobSkel.AddChild(_reachMod);
        // procedural hurt flinch (all bipeds) — recoils the torso/head on a direct hit; no-op at Wince 0
        int chest = _gobSkel.FindBone("Spine02"); if (chest < 0) chest = _gobSkel.FindBone("Spine01");
        int head = _gobSkel.FindBone("Head"); if (head < 0) head = _gobSkel.FindBone("neck");
        _winceMod = new WinceMod { Spine = spine, Chest = chest, Head = head };
        _gobSkel.AddChild(_winceMod);

        // slash-arc VFX: a crescent blade in the Creature's OWN frame (broad face toward the player, who the goblin faces), swept
        // across the front by the strike. Creature-local placement is predictable (unlike an unknown hand-bone axis).
        Color tint = (accent as StandardMaterial3D)?.AlbedoColor ?? Colors.White;
        _slashVfx = new MeshInstance3D
        {
            Mesh = Game.CrescentBladeMesh(),
            MaterialOverride = Game.CrescentBladeMat(tint),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(_slashVfx);
        if (bothArms)   // second crescent for the other hand
        {
            _slashVfx2 = new MeshInstance3D
            {
                Mesh = Game.CrescentBladeMesh(),
                MaterialOverride = Game.CrescentBladeMat(tint),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Visible = false,
            };
            AddChild(_slashVfx2);
        }
        if (_hollow) BuildHollowExtras();
        return true;
    }

    // ---- THE HOLLOW MOON extras: procedural gesture mod, the held boulder, and the arcane hand glow --------------------------
    private bool _hollow;
    private BossGestureMod _gest;
    private MeshInstance3D _heldRock;
    private readonly List<MeshInstance3D> _handGlow = new();
    private readonly List<ShaderMaterial> _handGlowMats = new();
    private int _handL = -1, _handR = -1;
    private float _glow, _glowTarget;
    private string _bAtkClip; private float _bAtkSpeed = 1f;
    private string _locoWalk = "walk";        // his default locomotion clip — phase 2 swaps it to the unsteady walk
    private MeshInstance3D _gobMesh;   // the authored model's skinned mesh — cloned into the phase-2 aura shell
    private Node3D _p2Aura; private readonly List<MeshInstance3D> _p2Flames = new();
    private readonly List<ShaderMaterial> _p2AuraMats = new();
    private float _p2Phase, _p2AuraScale = 1f; private bool _p2Spin;
    public bool IsAuthoredHollow => _hollow;

    // PHASE 2: a roaring column of arcane energy wrapped around him, plus the lurching unsteady walk. The aura is the
    // at-a-glance "this is not the same fight" read, so it's built big and always on — not a per-attack tell.
    public void SetPhase2()
    {
        if (!_hollow || _p2Aura != null) return;
        _locoWalk = _bClip.ContainsKey("walkunsteady") ? "walkunsteady" : "walk";
        _bPlaying = "";
        _p2Aura = new Node3D();
        AddChild(_p2Aura);
        var arc = new Color(0.42f, 0.20f, 0.95f);
        var hot = new Color(0.86f, 0.78f, 1f);

        // The corona is HIS OWN SKINNED MESH, cloned and inflated along its normals by the aura shader — so it has his
        // exact silhouette (horns, ribcage, coat) and deforms with every animation. Two shells at different inflations
        // give it depth. A primitive capsule was tried first and read exactly like what it was: a capsule.
        var sh = ResourceLoader.Load<Shader>("res://shaders/arcane_aura.gdshader");
        float meshH = _gobMesh?.Mesh != null ? _gobMesh.Mesh.GetAabb().Size.Y : 1.7f;
        ShaderMaterial AuraMat(float grow, float speed, float density, float wisp, float intensity, float opacity)
        {
            var m = new ShaderMaterial { Shader = sh };
            m.SetShaderParameter("tint", new Vector3(arc.R, arc.G, arc.B));
            m.SetShaderParameter("hot", new Vector3(hot.R, hot.G, hot.B));
            m.SetShaderParameter("amount", 1f);
            m.SetShaderParameter("grow", grow);
            m.SetShaderParameter("speed", speed);
            m.SetShaderParameter("density", density);
            m.SetShaderParameter("wisp", wisp);
            m.SetShaderParameter("intensity", intensity);
            m.SetShaderParameter("opacity", opacity);
            _p2AuraMats.Add(m);
            return m;
        }
        if (sh != null && _gobMesh?.Mesh != null && _gobMesh.GetParent() is Node3D meshParent)
        {
            // grow is in MESH-LOCAL units (the model node carries the ~8x scale up to gameplay size)
            (float grow, float speed, float density, float wisp, float intensity, float opacity)[] shells =
            {
                (meshH * 0.014f, 1.15f, 1.8f, 0.30f, 0.50f, 0.42f),   // tight, brighter — the licking edge on his skin
                (meshH * 0.048f, 0.75f, 1.0f, 0.70f, 0.30f, 0.22f),   // a looser outer flare, much fainter
            };
            foreach (var s in shells)
            {
                var dup = new MeshInstance3D
                {
                    Mesh = _gobMesh.Mesh,
                    Skin = _gobMesh.Skin,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    MaterialOverride = AuraMat(s.grow, s.speed, s.density, s.wisp, s.intensity, s.opacity),
                };
                meshParent.AddChild(dup);
                dup.Transform = _gobMesh.Transform;
                if (_gobSkel != null) dup.Skeleton = dup.GetPathTo(_gobSkel);   // ride the SAME skeleton → deforms with him
                _p2Flames.Add(dup);
            }
        }
        else if (sh != null)   // fallback only if the authored mesh is missing
        {
            var cap = new MeshInstance3D
            {
                Mesh = new CapsuleMesh { Radius = 0.95f * _scale, Height = 3.6f * _scale, RadialSegments = 24, Rings = 10 },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = AuraMat(0f, 1.15f, 1.2f, 0.4f, 0.6f, 0.5f),
            };
            cap.Position = new Vector3(0, 0.8f * _scale, 0);
            _p2Aura.AddChild(cap);
            _p2Flames.Add(cap);
        }
        _p2Aura.AddChild(new OmniLight3D { Position = new Vector3(0, 0.9f * _scale, 0), OmniRange = 5.5f * _scale, LightColor = arc, LightEnergy = 1.5f });
    }

    // While he SPINS, the model whips fast enough that the missing spin animation can't be read, and the aura tightens
    // into a solid opaque sheath so his silhouette is largely swallowed anyway.
    public void SetSpinning(bool on)
    {
        if (!_hollow) return;
        _p2Spin = on;
        for (int i = 0; i < _p2AuraMats.Count; i++)
        {
            _p2AuraMats[i].SetShaderParameter("speed", on ? 4.5f : (i == 0 ? 1.15f : 0.75f));
            _p2AuraMats[i].SetShaderParameter("opacity", on ? (i == 0 ? 0.9f : 0.7f) : (i == 0 ? 0.42f : 0.22f));   // opaque up only while he's hiding a missing spin pose
        }
    }

    // The shells ride his skeleton now, so there's nothing to animate here beyond easing the spin-up: SetSpinning
    // retargets the shader params and this just keeps the fallback capsule (if any) turning.
    private void UpdatePhase2Aura(float dt)
    {
        if (_p2Aura == null) return;
        _p2Phase += dt;
        if (_p2Aura.GetChildCount() > 1) _p2Aura.RotateY(dt * (_p2Spin ? 9f : 1.6f));
    }

    private void BuildHollowExtras()
    {
        int spine = _gobSkel.FindBone("Spine01"); if (spine < 0) spine = _gobSkel.FindBone("Spine");
        _gest = new BossGestureMod { ArmL = _gobArmL, ForeL = _gobForeL, ArmR = _gobArmR, ForeR = _gobForeR, Spine = spine };
        _gobSkel.AddChild(_gest);
        _handL = _gobSkel.FindBone("LeftHand"); _handR = _gobSkel.FindBone("RightHand");

        // The boulder he tears up during the rock-throw wind-up. It hangs in front of him at chest height and is flung on
        // the grip-and-throw clip's release — Creature-local, so it reads the same from any angle.
        _heldRock = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.42f * _scale, Height = 0.84f * _scale, RadialSegments = 7, Rings = 5 },
            MaterialOverride = Game.RockMat(),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(_heldRock);
        _heldRock.Position = new Vector3(0f, 1.15f * _scale, 1.25f * _scale);

        // Arcane energy swallowing both hands — the universal attack telegraph (fades in on wind-up, holds through the attack).
        // Sized off the HAND, not the hitbox: it must reach past the FINGERTIPS to actually obscure the hand (a wrist-sized
        // orb just looks like a bracelet), but stay well under the two floating beach-balls the first pass produced.
        var sh = ResourceLoader.Load<Shader>("res://shaders/arcane_hands.gdshader");
        for (int i = 0; i < 2; i++)
        {
            var mat = sh != null ? new ShaderMaterial { Shader = sh } : null;
            var mi = new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = 0.235f * _scale, Height = 0.47f * _scale, RadialSegments = 12, Rings = 8 },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Visible = false,
            };
            if (mat != null) { mat.SetShaderParameter("amount", 0f); mi.MaterialOverride = mat; _handGlowMats.Add(mat); }
            else mi.MaterialOverride = Game.Emissive(new Color(0.62f, 0.36f, 1f), 2.4f);
            AddChild(mi);
            _handGlow.Add(mi);
        }
        _gest.GlowL = _handGlow[0]; _gest.GlowR = _handGlow[1];
        _gest.HandL = _handL; _gest.HandR = _handR;
        // Half a hand-length past the wrist bone: the orb then spans wrist→fingertips. Pushing a FULL hand-length out
        // (the first attempt) parked it beyond the fingers, where it read as a ball he was holding, not a burning hand.
        _gest.PalmOffset = 0.11f * _scale;
    }

    // Enemy drives these: an attack clip overrides locomotion until BossEndClip(); the gesture + glow amounts are ramped
    // by the wind-up so both read as part of the telegraph.
    // (WIDENED) any authored biped can drive a one-shot clip over its locomotion, not just the boss — the withered casters
    // ride this same channel for their cast animations, and so do the ogre-bodied bolt throwers via the grafted mage cast.
    public void BossPlay(string canon, float speed = 1f)
    {
        if (!_gobAuthored || !_bClip.ContainsKey(canon)) return;
        _bAtkClip = canon; _bAtkSpeed = speed; _bState = BState.Attack; _bPlaying = ""; _bOneShotDone = false;
    }
    public void BossEndClip() { if (!_gobAuthored) return; _bAtkClip = null; _bState = BState.Loco; _bPlaying = ""; }
    // Readable aliases at the caster call sites — same one-shot channel, different fiction.
    public void CastPlay(string canon, float speed = 1f) => BossPlay(canon, speed);
    public void CastEnd() => BossEndClip();
    public float CastLength(string canon) => BossClipLength(canon);
    public bool Casting => _bAtkClip != null;
    // (HARNESS) what the biped is actually playing right now, and how fast — proves an attack clip really drove the pose
    // rather than the model sitting in its walk while only the VFX fired.
    // (HARNESS) how far the posed model's lowest foot sits from where the feet belong (Creature-local −Radius), in world
    // units. ~0 means grounded. A GRAFTED clip that wasn't retargeted properly shows up here as a constant offset — the
    // source rig's hip translations lifting or sinking a differently-proportioned body.
    public float DebugFootGap
    {
        get
        {
            if (_gobSkel == null) return 0f;
            int fl = _gobSkel.FindBone("LeftFoot"), fr = _gobSkel.FindBone("RightFoot");
            if (fl < 0 || fr < 0) return 0f;
            var g = _gobSkel.GlobalTransform;
            float foot = Mathf.Min((g * _gobSkel.GetBoneGlobalPose(fl).Origin).Y, (g * _gobSkel.GetBoneGlobalPose(fr).Origin).Y);
            return foot - (GlobalPosition.Y - _scale);
        }
    }
    public string DebugPlayingClip => _bPlaying;
    public float DebugPlaySpeed => _gobAp != null ? (float)_gobAp.SpeedScale : 0f;
    public bool DebugApPlaying => _gobAp != null && _gobAp.IsPlaying();

    // Clip length in seconds (0 if this model doesn't have it) — Enemy uses it to stretch each attack clip across its wind-up.
    public float BossClipLength(string canon)
    {
        if (_gobAp == null || !_bClip.TryGetValue(canon, out var key)) return 0f;
        var a = _gobAp.GetAnimation(key);
        return a != null ? (float)a.Length : 0f;
    }
    // Death plays the fall-forward clip directly — Enemy stops ticking Animate() once it's dead, so waiting for the next
    // frame's clip selection would leave him frozen upright.
    public void BossDie()
    {
        if (!_hollow || !_bClip.ContainsKey("death")) return;
        _bAtkClip = "death"; _bAtkSpeed = 1f; _bState = BState.Attack; _bPlaying = "";
        SetHandGlow(0f); ShowHeldRock(false); SetGesture(0f, 0f);
        _glow = 0f;
        foreach (var g in _handGlow) g.Visible = false;
        AnimSuspended = false;
        PlayBiped("death", 1f);
    }
    public void SetHandGlow(float t) { _glowTarget = Mathf.Clamp(t, 0f, 1f); }
    public void SetGesture(float pointUp, float pointFwd)   // the mine-toss signal: arm up, then chopped flat to the front
    {
        if (_gest == null) return;
        _gest.PointUp = pointUp; _gest.PointFwd = pointFwd;
    }
    public void ShowHeldRock(bool on, float grow = 1f)
    {
        if (_heldRock == null) return;
        _heldRock.Visible = on;
        if (on) _heldRock.Scale = Vector3.One * Mathf.Max(0.02f, grow);
    }

    // Per-frame: ride the hand glow toward its target. The ORBS' POSITIONS are set by BossGestureMod (the last skeleton
    // modifier), because only there are the bone poses final — reading them from here gives the pre-modifier pose and the
    // orbs hang motionless at his chest while the arms move.
    private void UpdateHandGlow(float dt)
    {
        if (_handGlow.Count == 0) return;
        _glow = Mathf.MoveToward(_glow, _glowTarget, dt * (_glowTarget > _glow ? 3.2f : 2.2f));
        bool on = _glow > 0.01f;
        for (int i = 0; i < _handGlow.Count; i++)
        {
            var mi = _handGlow[i];
            if (mi.Visible != on) mi.Visible = on;
            if (!on) continue;
            mi.Scale = Vector3.One * (0.85f + 0.15f * _glow);   // barely shrinks on fade-in — it must always cover the fingers
            if (i < _handGlowMats.Count) _handGlowMats[i].SetShaderParameter("amount", _glow);
        }
    }

    // ---- biped action clips ------------------------------------------------------------------------------------------------
    // Every authored biped (goblin/zombie/ogre/taker) rides the SAME Meshy rig, so ONE action library (run/fall/climb/stand-up,
    // authored on the taker) drives them all. Models that only ship a walk get the rest grafted in; the taker already has them.
    // A grafted clip travels with the REST POSE of the rig it was authored on: the bipeds share bone NAMES but not
    // proportions, so RetargetGraftedPositions needs the SOURCE rest to rebase every translation. Each library therefore
    // carries its own (the action set comes off the big taker, the cast set off the slighter withered king).
    private struct GraftSrc
    {
        public Animation Anim;
        public System.Collections.Generic.Dictionary<string, Vector3> Rest;
        public float HipsY;
    }
    private static System.Collections.Generic.Dictionary<string, GraftSrc> _sharedClips;   // taker: run/fall/climb/stand-up
    private static System.Collections.Generic.Dictionary<string, GraftSrc> _castClips;     // withered king: the mage casts

    // Clips that are already named canonically in the source GLB (the hollow-man / withered-king merges write these names
    // directly, so no substring guessing is needed). Anything not in here falls through to the Meshy-name heuristics below.
    private static readonly System.Collections.Generic.HashSet<string> PreCanon = new()
    {
        "walk", "run", "cast1", "cast6", "gripthrow", "stomp", "charge", "death",
        "standup", "walkslow", "walkspear", "walkunsteady", "walkplain",
        "cast", "cast4", "castcharge",   // (NEW) withered king: empower/heal, projectile, stun telegraph
    };

    private static string CanonName(string animName)   // Meshy clip name → our canonical key (order matters: check the longer names first)
    {
        string s = animName.ToLower();
        if (PreCanon.Contains(s)) return s;            // already canonical (merged hollow-man GLB)
        if (s.Contains("walking")) return "walk";
        if (s.Contains("running")) return "run";
        if (s.Contains("climbing_up")) return "climb";
        if (s.Contains("climb_attempt_and_fall_4")) return "climbfall4";
        if (s.Contains("climb_attempt_and_fall")) return "climbfall";
        if (s.Contains("falling_down")) return "falldown";
        if (s.Contains("fall1")) return "fall1";
        if (s.Contains("fall3")) return "fall3";
        if (s.Contains("stand_up2")) return "standup2";
        if (s.Contains("stand_up4")) return "standup4";
        return null;
    }

    private static void SetLoopAndDrift(Animation anim, string canon)
    {
        bool loop = canon == "walk" || canon == "run" || canon == "climb" || canon == "fall1";   // continuous states loop; the rest are one-shots
        anim.LoopMode = loop ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
        for (int i = 0; i < anim.GetTrackCount(); i++)   // freeze Hips horizontal drift — we drive world position ourselves (keep Y so falls/get-ups still move vertically)
        {
            if (anim.TrackGetType(i) != Animation.TrackType.Position3D) continue;
            if (!anim.TrackGetPath(i).ToString().Contains("Hips")) continue;
            int kc = anim.TrackGetKeyCount(i); if (kc == 0) continue;
            var first = (Vector3)anim.TrackGetKeyValue(i, 0);
            for (int k = 0; k < kc; k++) { var v = (Vector3)anim.TrackGetKeyValue(i, k); v.X = first.X; v.Z = first.Z; anim.TrackSetKeyValue(i, k, v); }
        }
    }

    // Load a source GLB ONCE and cache the clips `want` accepts, together with that rig's rest pose, as a graft library.
    private static System.Collections.Generic.Dictionary<string, GraftSrc> LoadGraftLib(string glb, System.Func<string, bool> want)
    {
        var lib = new System.Collections.Generic.Dictionary<string, GraftSrc>();
        if (!ResourceLoader.Exists(glb)) return lib;
        var sc = ResourceLoader.Load<PackedScene>(glb)?.Instantiate<Node3D>();
        if (sc == null) return lib;
        var ap = ModelAssets.FindAnimPlayer(sc);
        var srcSkel = ModelAssets.FindSkeleton(sc);
        var rest = new System.Collections.Generic.Dictionary<string, Vector3>();
        float hipsY = 1f;
        if (srcSkel != null)   // capture the SOURCE rig's REST bone positions — grafted clips carry its translations, which must be retargeted onto each playing rig
        {
            for (int b = 0; b < srcSkel.GetBoneCount(); b++) rest[srcSkel.GetBoneName(b)] = srcSkel.GetBoneRest(b).Origin;
            if (rest.TryGetValue("Hips", out var sh) && Mathf.Abs(sh.Y) > 0.01f) hipsY = sh.Y;
        }
        if (ap != null)
            foreach (StringName l0 in ap.GetAnimationLibraryList())
            {
                var l = ap.GetAnimationLibrary(l0);
                foreach (StringName a in l.GetAnimationList())
                {
                    string canon = CanonName((string)a);
                    if (canon == null || !want(canon)) continue;
                    var anim = (Animation)l.GetAnimation(a).Duplicate(true);
                    for (int i = anim.GetTrackCount() - 1; i >= 0; i--)   // drop SCALE tracks — they carry the source rig's baked bone scales and would squash a different rig
                        if (anim.TrackGetType(i) == Animation.TrackType.Scale3D) anim.RemoveTrack(i);
                    SetLoopAndDrift(anim, canon);
                    lib[canon] = new GraftSrc { Anim = anim, Rest = rest, HipsY = hipsY };
                }
            }
        sc.QueueFree();
        return lib;
    }

    // The taker's non-walk clips (run/fall/climb/stand-up) — every biped gets these (walk stays per-model).
    private static System.Collections.Generic.Dictionary<string, GraftSrc> SharedActionClips()
        => _sharedClips ??= LoadGraftLib(TakerGlb, c => c != "walk");

    // The withered king's mage casts — grafted only onto models that need them (the ogre-bodied bolt throwers).
    private static System.Collections.Generic.Dictionary<string, GraftSrc> SharedCastClips()
        => _castClips ??= LoadGraftLib(WitheredGlb, c => c == "cast" || c == "cast4" || c == "castcharge");

    // Retarget a grafted clip's per-bone TRANSLATION tracks from the taker rig onto THIS rig. The four Meshy bipeds share bone
    // NAMES but were re-rigged at different proportions (Hips rest 82 goblin vs 97 taker; leg offsets differ), so the taker's
    // absolute bone positions would yank a smaller rig's bones out of place → mesh deform. We remap each translated bone's motion
    // as (target rest) + (anim − source rest)·ratio: rigid bones (anim==source rest) snap to the TARGET rest (correct lengths),
    // and the animating root (Hips) keeps its excursion scaled to this rig's height. Rotations transfer untouched (they define the pose).
    private void RetargetGraftedPositions(Animation anim, System.Collections.Generic.Dictionary<string, Vector3> srcRest, float srcHipsY)
    {
        if (_gobSkel == null || srcRest == null) return;
        int hb = _gobSkel.FindBone("Hips");
        float tgtHipsY = hb >= 0 ? _gobSkel.GetBoneRest(hb).Origin.Y : srcHipsY;
        float ratio = Mathf.Abs(srcHipsY) > 0.01f ? tgtHipsY / srcHipsY : 1f;
        for (int i = 0; i < anim.GetTrackCount(); i++)
        {
            if (anim.TrackGetType(i) != Animation.TrackType.Position3D) continue;
            string p = anim.TrackGetPath(i).ToString(); int ci = p.IndexOf(':');
            string bone = ci >= 0 ? p.Substring(ci + 1) : p;
            int tb = _gobSkel.FindBone(bone); if (tb < 0) continue;
            Vector3 tRest = _gobSkel.GetBoneRest(tb).Origin;
            Vector3 sRest = srcRest.TryGetValue(bone, out var sr) ? sr : tRest;
            int kc = anim.TrackGetKeyCount(i);
            for (int k = 0; k < kc; k++)
            {
                var v = (Vector3)anim.TrackGetKeyValue(i, k);
                anim.TrackSetKeyValue(i, k, tRest + (v - sRest) * ratio);
            }
        }
    }

    // Map this model's OWN clips to canonical keys, then graft in any action clips it's missing (the goblin/zombie/ogre only ship a walk).
    private void RegisterBipedClips(bool graftShared = true, bool graftCast = false)
    {
        _bClip.Clear();
        foreach (StringName lib in _gobAp.GetAnimationLibraryList())
        {
            var l = _gobAp.GetAnimationLibrary(lib);
            foreach (StringName a in l.GetAnimationList())
            {
                string key = ((string)lib).Length == 0 ? (string)a : $"{(string)lib}/{(string)a}";
                string canon = CanonName((string)a) ?? "walk";   // a lone unrecognized clip (goblin's baked walk) IS the walk
                SetLoopAndDrift(l.GetAnimation(a), canon);
                if (!_bClip.ContainsKey(canon)) _bClip[canon] = key;
            }
        }
        if (graftShared) Graft(SharedActionClips());
        if (graftCast) Graft(SharedCastClips());
    }

    // Copy a graft library's clips into this model's own AnimationPlayer, each retargeted to THIS rig's proportions.
    private void Graft(System.Collections.Generic.Dictionary<string, GraftSrc> src)
    {
        if (src.Count == 0) return;
        AnimationLibrary act = _gobAp.HasAnimationLibrary("act") ? _gobAp.GetAnimationLibrary("act") : new AnimationLibrary();
        if (!_gobAp.HasAnimationLibrary("act")) _gobAp.AddAnimationLibrary("act", act);
        foreach (var kv in src)
        {
            if (_bClip.ContainsKey(kv.Key)) continue;   // this model authored its own version → keep it
            if (!act.HasAnimation(kv.Key))
            {
                var copy = (Animation)kv.Value.Anim.Duplicate(true);   // per-model copy so the retarget is fitted to THIS rig's proportions
                RetargetGraftedPositions(copy, kv.Value.Rest, kv.Value.HipsY);
                act.AddAnimation(kv.Key, copy);
            }
            _bClip[kv.Key] = $"act/{kv.Key}";
        }
    }

    // Debug/count hook for the harness: how many canonical action clips this biped resolved (10 = full set merged correctly).
    public int BipedClipCount => _bClip.Count;
    public bool HasBipedClip(string canon) => _bClip.ContainsKey(canon);

    // ---- biped state intents (called by Enemy at transitions / per-frame) ----
    // NOTE: the per-frame locomotion drivers (Enemy.AnimStep, the client-proxy path) call this EVERY frame. While the boss
    // is playing an attack/death clip that call must not reset him to walking — it silently ate every attack animation
    // until this guard existed. The attack state is cleared only by BossEndClip().
    public void BipedLoco(bool run) { if (_bAtkClip != null) { _bRun = run; return; } _bState = BState.Loco; _bRun = run; }
    public void BipedClimb() { _bState = BState.Climb; }
    public void BipedAirborne(bool fromClimb) { _bState = BState.Airborne; _bAirPhase = fromClimb && _bClip.ContainsKey("climbfall") ? 0 : 1; _bOneShotDone = false; _bPlaying = ""; }
    public void BipedWallSlam() { _bState = BState.WallDown; _bOneShotDone = false; _bPlaying = ""; }
    public void BipedGetUp(int which = -1)   // which: 2/4 forces that stand-up, else random
    {
        _bStandKey = which == 2 ? "standup2" : which == 4 ? "standup4" : (R(0f, 1f) < 0.5f ? "standup2" : "standup4");
        if (!_bClip.ContainsKey(_bStandKey)) _bStandKey = _bClip.ContainsKey("standup4") ? "standup4" : "standup2";
        _bState = BState.StandUp; _bOneShotDone = false; _bPlaying = "";
    }
    public void BipedReset() { _bState = BState.Loco; _bRun = false; _bReach = 0f; _bReachTarget = 0f; _bOneShotDone = false; }
    public void BipedReach(float target) { _bReachTarget = Mathf.Clamp(target, 0f, 1f); }
    public void Wince(int variant) { _wince = 1f; _winceVar = variant; }   // direct-hit flinch; the Enemy picks the (random) variant + rate-limits
    public bool BipedOneShotDone => _bOneShotDone;   // Enemy polls this to know a WallDown/StandUp clip finished

    // Sweep the crescent across the goblin's front over the strike, then fade + hide. Creature-local: +Z faces the player, so
    // the crescent's broad face reads; it arcs from up-back to down-forward on the slashing side, growing then fading.
    private void UpdateSlashVfx(float dt)
    {
        if (_slashVfx == null) return;
        if (_slashVfxT <= 0f)
        {
            if (_slashVfx.Visible) _slashVfx.Visible = false;
            if (_slashVfx2 != null && _slashVfx2.Visible) _slashVfx2.Visible = false;
            return;
        }
        _slashVfxT -= dt;
        float k = Mathf.Clamp(_slashVfxT / SlashVfxDur, 0f, 1f);   // 1 → 0 over the strike
        void Sweep(MeshInstance3D vfx, float side)
        {
            if (vfx == null) return;
            vfx.Visible = true;
            vfx.Position = new Vector3(side * 0.18f * _scale, 0.55f * _scale, 0.7f * _scale);   // by the slashing arm, in front
            // roll the crescent through the arc (up-back → down-forward); its XY plane already faces the player (+Z)
            vfx.RotationDegrees = new Vector3(0f, 0f, Mathf.Lerp(-70f, 60f, 1f - k) * side);    // 1−k = progress 0→1
            vfx.Scale = Vector3.One * (_scale * (1.0f + (1f - k) * 0.35f));   // grows through the swing
            vfx.Transparency = Mathf.Clamp(1f - k * 1.6f, 0f, 1f);           // bright early, fades out
        }
        if (_gobBothArms) { Sweep(_slashVfx, -1f); Sweep(_slashVfx2, 1f); }   // (ZOMBIE) both hands
        else Sweep(_slashVfx, _gobSlashLeft ? -1f : 1f);
    }

    private static MeshInstance3D FindMesh(Node n)
    {
        if (n is MeshInstance3D mi && mi.Mesh != null) return mi;
        foreach (var c in n.GetChildren()) { var r = FindMesh(c); if (r != null) return r; }
        return null;
    }

    private float _gobForceMove = -1f;   // (harness) ≥0 overrides the movement-derived walk speed so a pinned goblin still strides
    public void DebugWalkSpeed(float m) { _gobForceMove = m; }

    // authored biped per-frame: pick the right action clip for the current state, layer the melee swing + grab reach, sweep the VFX.
    private void AnimateGoblin(float dt, float move)
    {
        if (_gobForceMove >= 0f) move = _gobForceMove;
        if (AnimSuspended) { if (_gobAp != null) _gobAp.SpeedScale = 0f; return; }   // culled → freeze the pose

        // --- which clip + how fast, from the state machine ---
        string canon; float speed;
        switch (_bState)
        {
            case BState.Climb:    canon = "climb";    speed = 1f; break;
            case BState.Airborne: canon = _bAirPhase == 0 ? "climbfall" : "fall1"; speed = 1f; break;
            case BState.WallDown: canon = "falldown"; speed = 1.5f; break;   // slam down a touch snappier so the get-up fits the stun window
            case BState.StandUp:  canon = _bStandKey; speed = 1.3f; break;
            case BState.Attack:   canon = _bAtkClip ?? _locoWalk; speed = _bAtkSpeed; break;   // (BOSS) attack/death clip owns the whole body
            default:              canon = _bRun ? "run" : _locoWalk; speed = _bRun ? 1.25f : (0.35f + move * 1.4f); break;
        }
        PlayBiped(canon, speed);
        if (_hollow) { UpdateHandGlow(dt); UpdatePhase2Aura(dt); }

        // one-shot completion: climb-slip lead-in → free-fall loop; wall-slam/stand-up → flag Enemy that it's done
        if (_gobAp != null && !_gobAp.IsPlaying())   // the current one-shot reached its end
        {
            if (_bState == BState.Airborne && _bAirPhase == 0) { _bAirPhase = 1; PlayBiped("fall1", 1f); }
            else if (_bState == BState.WallDown || _bState == BState.StandUp || _bState == BState.Attack) _bOneShotDone = true;
        }

        // --- grab-arms telegraph (taker): ease toward the target so it reads as a wind-up, not a snap ---
        _bReach = Mathf.MoveToward(_bReach, _bReachTarget, dt * 2.6f);
        if (_reachMod != null) _reachMod.Reach = _bReach;

        // --- hurt flinch: sharp impulse then a quick decay (~0.25s) so it reads as an "ouch" without stalling anything ---
        if (_wince > 0f) _wince = Mathf.MoveToward(_wince, 0f, dt * 4.2f);
        if (_winceMod != null) { _winceMod.Wince = _wince; _winceMod.Variant = _winceVar; }

        // --- melee swing (only while on the ground locomoting — no arm-chop mid-fall/climb/get-up) ---
        _swing = Mathf.MoveToward(_swing, _swingTarget, dt * 3.5f);
        _strike = Mathf.MoveToward(_strike, 0f, dt * 3.2f);
        bool canSlash = _bState == BState.Loco;
        if (_gobSlash != null)
        {
            _gobSlash.SwingRad = canSlash ? Mathf.DegToRad(_swing * 40f - _strike * 115f) : 0f;   // + rear back (wind-up), − big chop down/forward
            _gobSlash.SpineLean = canSlash ? Mathf.DegToRad(_strike * 32f) : 0f;
            if (_gobSlash2 != null) _gobSlash2.SwingRad = _gobSlash.SwingRad;   // (ZOMBIE) other arm swings identically
        }
        UpdateSlashVfx(dt);
    }

    // Play a canonical clip (resolving its real AnimationPlayer key) only when it changes, at the given speed. One-shots restart
    // when re-selected after finishing (so a repeated stand-up/wall-slam plays again).
    private void PlayBiped(string canon, float speed)
    {
        if (_gobAp == null) return;
        if (!_bClip.TryGetValue(canon, out var key)) { canon = _locoWalk; if (!_bClip.TryGetValue(canon, out key)) return; }
        if (_bPlaying != canon) { _gobAp.Play(key); _bPlaying = canon; }   // one-shots play once and HOLD their last pose (loops keep looping via LoopMode)
        _gobAp.SpeedScale = speed;
    }

    private static Mesh Cone(float r, float h) => new CylinderMesh { TopRadius = 0.001f, BottomRadius = r, Height = h };
    private static Mesh Cyl(float r, float h) => new CylinderMesh { TopRadius = r, BottomRadius = r, Height = h };
    private static Mesh Box(float x, float y, float z) => new BoxMesh { Size = new Vector3(x, y, z) };
    private static Mesh Sph(float r) => new SphereMesh { Radius = r, Height = r * 2f };
    private static float R(float a, float b) => (float)GD.RandRange(a, b);

    private readonly List<MeshInstance3D> _detail = new();   // (PERF) the small trim parts — hidden as a group on far enemies (LOD)
    private readonly List<MeshInstance3D> _shadowParts = new();   // (PERF) the big shadow-casting parts (torso/head/limbs) — stop casting on far enemies
    private Material _limbMat;   // (FIX) limb material — limb parts are the silhouette (legs/arms) and must NEVER be culled as "trim"
    private bool _lodFar = false;
    private MeshInstance3D Part(Node3D parent, Mesh m, Material mat, Vector3 pos, Vector3 rotDeg, Vector3 scl)
    {
        var mi = new MeshInstance3D { Mesh = m, MaterialOverride = mat };
        // (PERF) small detail parts (teeth, warts, eyes, fingers…) don't cast shadows — the torso/head/limbs still do,
        // so the silhouette reads the same, but we stop redrawing ~15 tiny meshes per enemy into all 4 shadow cascades.
        var s = m.GetAabb().Size;
        bool small = Mathf.Max(s.X * Mathf.Abs(scl.X), Mathf.Max(s.Y * Mathf.Abs(scl.Y), s.Z * Mathf.Abs(scl.Z))) < 0.62f;
        if (small && mat != _limbMat)   // (FIX) never treat LIMBS as cullable trim — small creatures' legs/arms were vanishing at range
        {
            mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            _detail.Add(mi);   // …and drop these tiny trim parts once the foe is far enough that they're sub-pixel (LOD)
        }
        else _shadowParts.Add(mi);   // (PERF) limbs + big parts: always draw; far-LOD only stops their shadow-casting
        parent.AddChild(mi);
        mi.Position = pos; mi.RotationDegrees = rotDeg; mi.Scale = scl;
        return mi;
    }
    // (PERF) LOD: on distant enemies hide the dozens of tiny trim meshes (teeth/warts/eyes/fingers/spikes). The torso,
    // head and limbs keep drawing so the silhouette is unchanged; we just shed ~half the draw calls per far-off foe.
    public void SetLodFar(bool far)
    {
        if (far == _lodFar) return;
        _lodFar = far;
        for (int i = 0; i < _detail.Count; i++)
            if (_detail[i] != null && GodotObject.IsInstanceValid(_detail[i])) _detail[i].Visible = !far;
        var sh = far ? GeometryInstance3D.ShadowCastingSetting.Off : GeometryInstance3D.ShadowCastingSetting.On;   // (PERF) far foes stop feeding the shadow cascades
        for (int i = 0; i < _shadowParts.Count; i++)
            if (_shadowParts[i] != null && GodotObject.IsInstanceValid(_shadowParts[i])) _shadowParts[i].CastShadow = sh;
    }
    private Node3D Pivot(Node3D parent, Vector3 pos, Vector3 rotDeg = default)
    {
        var n = new Node3D();
        parent.AddChild(n);
        n.Position = pos; n.RotationDegrees = rotDeg;
        return n;
    }

    public void Build(CreatureKind kind, float radius, Material body, Material limb, Material accent)
    {
        _limbMat = limb;   // (FIX) so Part() can exempt limbs from the size-based trim cull
        _kind = kind; _scale = radius;
        _phase = R(0f, 6.28f);
        switch (kind)
        {
            case CreatureKind.Goblin: if (!AuthoredGoblin(radius, accent)) Goblin(radius, body, limb, accent, false); break;   // authored GLB when present, else procedural
            case CreatureKind.Bomber: Goblin(radius, body, limb, accent, true); break;
            case CreatureKind.Zombie: if (!AuthoredZombie(radius, accent)) Goblin(radius, body, limb, accent, false); break;   // authored two-arm zombie GLB, else procedural shamble
            case CreatureKind.Taker: if (!AuthoredTaker(radius, accent)) Goblin(radius, body, limb, accent, false); break;    // authored taker GLB (full action set), else procedural shamble
            case CreatureKind.Orc: if (!AuthoredOgre(radius, accent)) Orc(radius, body, limb, accent); break;   // authored ogre GLB when present, else procedural orc
            case CreatureKind.HollowBoss: if (!AuthoredHollow(radius, accent)) HollowBoss(radius, body, limb, accent); break;   // authored GLB w/ his own attack clips, else the old procedural half-orc
            case CreatureKind.Withered: if (!AuthoredWithered(radius, accent)) Spider(radius, body, limb, accent); break;   // authored spellcaster GLB (caster/stunner/healer/empowerer), else the old neon spider
            case CreatureKind.Spider: Spider(radius, body, limb, accent); break;
            case CreatureKind.Mosquito: Mosquito(radius, body, limb, accent); break;
            case CreatureKind.Zapper: Zapper(radius, body, limb, accent); break;
            case CreatureKind.Crocodile: Crocodile(radius, body, limb, accent); break;
            case CreatureKind.Troll: Troll(radius, body, limb, accent); break;
            case CreatureKind.Pigmy: Pigmy(radius, body, limb, accent); break;
            case CreatureKind.Pterodactyl: Pterodactyl(radius, body, limb, accent); break;
            case CreatureKind.Bat: Bat(radius, body, limb, accent); break;
            case CreatureKind.Snake: Snake(radius, body, limb, accent); break;
        }
    }

    // ---- jungle troll: hulking, hunched, HUGE dragging arms, underbite tusks, warty; a rushing bruiser ----
    private void Troll(float s, Material body, Material limb, Material accent)
    {
        float v = R(0.95f, 1.2f);
        float gh = s * 1.5f * v;
        _body = Pivot(this, new Vector3(0, gh, 0));
        _bodyBaseY = gh;
        float bw = s * 2.0f * v;
        Part(_body, Box(bw, gh, bw * 0.9f), body, Vector3.Zero, new Vector3(R(10, 18), 0, 0), Vector3.One);                       // massive hunched torso
        Part(_body, Sph(s * 0.9f), body, new Vector3(0, gh * 0.35f, -bw * 0.35f), Vector3.Zero, new Vector3(1.2f, 1f, 1f));       // hunchback
        Part(_body, Box(bw * 1.2f, gh * 0.4f, bw * 0.6f), limb, new Vector3(0, gh * 0.42f, 0), Vector3.Zero, Vector3.One);        // shoulders
        for (int i = 0; i < 5; i++) Part(_body, Sph(s * R(0.14f, 0.26f)), limb, new Vector3(R(-0.5f, 0.5f) * bw, R(-0.2f, 0.4f) * gh, R(-0.5f, -0.2f) * bw), Vector3.Zero, Vector3.One);   // warty lumps
        var head = Part(_body, Sph(s * 0.55f), body, new Vector3(0, gh * 0.5f, s * 0.25f), Vector3.Zero, new Vector3(1.1f, 0.9f, 1f));
        Part(head, Box(s * 0.8f, s * 0.2f, s * 0.3f), limb, new Vector3(0, s * 0.2f, s * 0.3f), new Vector3(-12, 0, 0), Vector3.One);   // heavy brow
        Part(head, Sph(s * 0.08f), accent, new Vector3(s * 0.18f, s * 0.05f, s * 0.42f), Vector3.Zero, Vector3.One);
        Part(head, Sph(s * 0.08f), accent, new Vector3(-s * 0.18f, s * 0.05f, s * 0.42f), Vector3.Zero, Vector3.One);
        Part(head, Box(s * 0.7f, s * 0.28f, s * 0.32f), body, new Vector3(0, -s * 0.28f, s * 0.34f), new Vector3(8, 0, 0), Vector3.One);   // jutting jaw
        Part(head, Cone(s * 0.11f, s * R(0.4f, 0.55f)), accent, new Vector3(s * 0.24f, -s * 0.2f, s * 0.42f), new Vector3(-20, 0, 0), Vector3.One);   // underbite tusks
        Part(head, Cone(s * 0.11f, s * R(0.4f, 0.55f)), accent, new Vector3(-s * 0.24f, -s * 0.2f, s * 0.42f), new Vector3(-20, 0, 0), Vector3.One);
        float legLen = gh * 0.4f, armLen = gh * 0.8f;   // stubby legs, huge dragging arms
        for (int i = 0; i < 2; i++)
        {
            float sx = i == 0 ? 1 : -1;
            var hip = Pivot(this, new Vector3(sx * bw * 0.32f, gh * 0.32f, 0));
            Part(hip, Cyl(s * 0.4f, legLen), limb, new Vector3(0, -legLen / 2f, 0), Vector3.Zero, Vector3.One);
            var knee = Pivot(hip, new Vector3(0, -legLen, 0));
            Part(knee, Cyl(s * 0.36f, legLen), limb, new Vector3(0, -legLen / 2f, 0), Vector3.Zero, Vector3.One);
            Part(knee, Box(s * 0.6f, s * 0.2f, s * 0.85f), limb, new Vector3(0, -legLen, s * 0.14f), Vector3.Zero, Vector3.One);
            _hips.Add(hip); _knees.Add(knee); _hipBase.Add(Vector3.Zero); _kneeBase.Add(Vector3.Zero);
            var arm = Pivot(_body, new Vector3(sx * bw * 0.6f, gh * 0.42f, 0));
            Part(arm, Cyl(s * 0.36f, armLen), limb, new Vector3(0, -armLen / 2f, 0), Vector3.Zero, Vector3.One);
            Part(arm, Sph(s * 0.5f), limb, new Vector3(0, -armLen, 0), Vector3.Zero, Vector3.One);   // huge fists
            _arms.Add(arm);
        }
    }

    // ---- pigmy: little tribal humanoid, big head with warpaint + a feather topknot, carries a spear/blowpipe ----
    private void Pigmy(float s, Material body, Material limb, Material accent)
    {
        float v = R(0.85f, 1.1f);
        float gh = s * 0.85f * v;
        _body = Pivot(this, new Vector3(0, gh, 0));
        _bodyBaseY = gh;
        float bw = s * 0.5f * v;
        Part(_body, Box(bw, gh * 0.8f, bw * 0.7f), body, Vector3.Zero, new Vector3(R(4, 10), 0, 0), Vector3.One);   // little torso
        var head = Part(_body, Sph(s * 0.4f), body, new Vector3(0, gh * 0.55f, s * 0.05f), Vector3.Zero, Vector3.One);
        Part(head, Box(s * 0.5f, s * 0.1f, s * 0.1f), accent, new Vector3(0, s * 0.02f, s * 0.3f), Vector3.Zero, Vector3.One);   // warpaint stripe
        Part(head, Sph(s * 0.06f), accent, new Vector3(s * 0.12f, s * 0.08f, s * 0.32f), Vector3.Zero, Vector3.One);
        Part(head, Sph(s * 0.06f), accent, new Vector3(-s * 0.12f, s * 0.08f, s * 0.32f), Vector3.Zero, Vector3.One);
        Part(head, Cone(s * 0.06f, s * 0.5f), accent, new Vector3(0, s * 0.4f, -s * 0.1f), new Vector3(-20, 0, 0), Vector3.One);   // feather topknot
        float legLen = gh * 0.4f, armLen = gh * 0.45f;
        for (int i = 0; i < 2; i++)
        {
            float sx = i == 0 ? 1 : -1;
            var hip = Pivot(this, new Vector3(sx * bw * 0.32f, gh * 0.3f, 0));
            Part(hip, Cyl(s * 0.12f, legLen), limb, new Vector3(0, -legLen / 2f, 0), Vector3.Zero, Vector3.One);
            var knee = Pivot(hip, new Vector3(0, -legLen, 0));
            Part(knee, Cyl(s * 0.1f, legLen), limb, new Vector3(0, -legLen / 2f, 0), Vector3.Zero, Vector3.One);
            Part(knee, Box(s * 0.24f, s * 0.1f, s * 0.34f), limb, new Vector3(0, -legLen, s * 0.1f), Vector3.Zero, Vector3.One);
            _hips.Add(hip); _knees.Add(knee); _hipBase.Add(Vector3.Zero); _kneeBase.Add(Vector3.Zero);
            var arm = Pivot(_body, new Vector3(sx * bw * 0.55f, gh * 0.32f, 0));
            Part(arm, Cyl(s * 0.09f, armLen), limb, new Vector3(0, -armLen / 2f, 0), Vector3.Zero, Vector3.One);
            Part(arm, Sph(s * 0.12f), limb, new Vector3(0, -armLen, 0), Vector3.Zero, Vector3.One);
            _arms.Add(arm);
        }
        // spear/blowpipe in the right hand — parented to the arm so it thrusts on the jab
        var weapon = Part(_arms[0], Cyl(s * 0.05f, s * 1.9f), accent, new Vector3(0, -armLen, s * 0.35f), new Vector3(82, 0, 0), Vector3.One);
        Part(weapon, Cone(s * 0.08f, s * 0.3f), limb, new Vector3(0, s * 0.95f, 0), Vector3.Zero, Vector3.One);   // tip
    }

    // ---- pterodactyl: leathery flyer, long beaked head + back crest, big membrane wings, tail ----
    private void Pterodactyl(float s, Material body, Material limb, Material accent)
    {
        float v = R(0.9f, 1.15f); s *= v;
        _body = Pivot(this, Vector3.Zero);
        _bodyBaseY = 0f;
        Part(_body, Sph(s * 0.5f), body, Vector3.Zero, Vector3.Zero, new Vector3(1, 0.9f, 1.4f));   // body
        Part(_body, Cyl(s * 0.14f, s * 0.8f), body, new Vector3(0, s * 0.25f, s * 0.4f), new Vector3(50, 0, 0), Vector3.One);   // neck
        var head = Part(_body, Sph(s * 0.24f), body, new Vector3(0, s * 0.5f, s * 0.75f), Vector3.Zero, Vector3.One);
        Part(head, Cone(s * 0.1f, s * 0.9f), limb, new Vector3(0, -s * 0.02f, s * 0.5f), new Vector3(90, 0, 0), Vector3.One);   // long beak
        Part(head, Cone(s * 0.12f, s * 0.5f), accent, new Vector3(0, s * 0.1f, -s * 0.2f), new Vector3(-60, 0, 0), Vector3.One);   // head crest
        Part(head, Sph(s * 0.06f), accent, new Vector3(s * 0.12f, s * 0.05f, s * 0.1f), Vector3.Zero, Vector3.One);
        Part(head, Sph(s * 0.06f), accent, new Vector3(-s * 0.12f, s * 0.05f, s * 0.1f), Vector3.Zero, Vector3.One);
        Part(_body, Cone(s * 0.1f, s * 1.0f), body, new Vector3(0, 0, -s * 0.85f), new Vector3(-90, 0, 0), Vector3.One);   // tail
        for (int i = 0; i < 2; i++)
        {
            float sx = i == 0 ? 1 : -1;
            var w = Pivot(_body, new Vector3(sx * s * 0.35f, s * 0.15f, 0));
            Part(w, Box(s * 2.2f, s * 0.05f, s * 1.0f), limb, new Vector3(sx * s * 1.1f, 0, 0), Vector3.Zero, Vector3.One);   // membrane
            Part(w, Cyl(s * 0.06f, s * 2.0f), body, new Vector3(sx * s * 1.0f, 0, s * 0.3f), new Vector3(0, 0, sx * 90f), Vector3.One);   // leading-edge bone
            _wings.Add(w);
        }
        for (int i = 0; i < 2; i++) { float sx = i == 0 ? 1 : -1; Part(_body, Cyl(s * 0.05f, s * 0.4f), limb, new Vector3(sx * s * 0.2f, -s * 0.4f, -s * 0.2f), Vector3.Zero, Vector3.One); }
    }

    // ---- bat: round furry body, big ears + fangs, membrane wings ----
    private void Bat(float s, Material body, Material limb, Material accent)
    {
        float v = R(0.85f, 1.15f); s *= v;
        _body = Pivot(this, Vector3.Zero);
        _bodyBaseY = 0f;
        Part(_body, Sph(s * 0.45f), body, Vector3.Zero, Vector3.Zero, new Vector3(1, 1.1f, 1));   // furry body
        var head = Part(_body, Sph(s * 0.3f), body, new Vector3(0, s * 0.35f, s * 0.1f), Vector3.Zero, Vector3.One);
        Part(head, Cone(s * 0.12f, s * 0.5f), body, new Vector3(s * 0.15f, s * 0.3f, 0), new Vector3(-10, 0, 10), Vector3.One);   // ears
        Part(head, Cone(s * 0.12f, s * 0.5f), body, new Vector3(-s * 0.15f, s * 0.3f, 0), new Vector3(-10, 0, -10), Vector3.One);
        Part(head, Sph(s * 0.07f), accent, new Vector3(s * 0.12f, s * 0.02f, s * 0.24f), Vector3.Zero, Vector3.One);
        Part(head, Sph(s * 0.07f), accent, new Vector3(-s * 0.12f, s * 0.02f, s * 0.24f), Vector3.Zero, Vector3.One);
        Part(head, Cone(s * 0.03f, s * 0.1f), accent, new Vector3(s * 0.06f, -s * 0.16f, s * 0.22f), new Vector3(180, 0, 0), Vector3.One);   // fangs
        Part(head, Cone(s * 0.03f, s * 0.1f), accent, new Vector3(-s * 0.06f, -s * 0.16f, s * 0.22f), new Vector3(180, 0, 0), Vector3.One);
        for (int i = 0; i < 2; i++)
        {
            float sx = i == 0 ? 1 : -1;
            var w = Pivot(_body, new Vector3(sx * s * 0.3f, s * 0.1f, 0));
            Part(w, Box(s * 1.6f, s * 0.04f, s * 0.9f), limb, new Vector3(sx * s * 0.8f, 0, -s * 0.1f), Vector3.Zero, Vector3.One);
            _wings.Add(w);
        }
        for (int i = 0; i < 2; i++) { float sx = i == 0 ? 1 : -1; Part(_body, Cyl(s * 0.04f, s * 0.25f), limb, new Vector3(sx * s * 0.12f, -s * 0.4f, 0), Vector3.Zero, Vector3.One); }
    }

    // ---- snake: a chain of tapering segments low to the ground; forked tongue; SLITHERS via a travelling yaw wave ----
    private void Snake(float s, Material body, Material limb, Material accent)
    {
        float v = R(0.9f, 1.2f); s *= v;
        _bodyBaseY = s * 0.3f;
        Node3D prev = this;
        int segs = 7; float segLen = s * 0.55f, r = s * 0.32f;
        for (int i = 0; i < segs; i++)
        {
            var seg = Pivot(prev, i == 0 ? new Vector3(0, s * 0.3f, s * 0.2f) : new Vector3(0, 0, -segLen));
            float rr = r * (1f - i * 0.09f);
            // a smooth capsule per segment (overlapping caps fill the joints) → a continuous body, not caterpillar beads
            Part(seg, new CapsuleMesh { Radius = rr, Height = segLen + rr * 2.1f, RadialSegments = 12, Rings = 5 }, body, new Vector3(0, 0, -segLen * 0.5f), new Vector3(90, 0, 0), Vector3.One);
            _hips.Add(seg); _knees.Add(null); _hipBase.Add(Vector3.Zero); _kneeBase.Add(Vector3.Zero);
            if (i == 0) _body = seg;
            prev = seg;
        }
        var head = _body;
        Part(head, Sph(s * 0.36f), body, new Vector3(0, 0, s * 0.15f), Vector3.Zero, new Vector3(1.2f, 0.85f, 1.2f));
        Part(head, Sph(s * 0.07f), accent, new Vector3(s * 0.14f, s * 0.12f, s * 0.28f), Vector3.Zero, Vector3.One);
        Part(head, Sph(s * 0.07f), accent, new Vector3(-s * 0.14f, s * 0.12f, s * 0.28f), Vector3.Zero, Vector3.One);
        Part(head, Box(s * 0.03f, s * 0.03f, s * 0.4f), accent, new Vector3(0, -s * 0.02f, s * 0.5f), Vector3.Zero, Vector3.One);   // forked tongue
        Part(head, Box(s * 0.03f, s * 0.03f, s * 0.15f), accent, new Vector3(s * 0.05f, -s * 0.02f, s * 0.68f), new Vector3(0, 20, 0), Vector3.One);
        Part(head, Box(s * 0.03f, s * 0.03f, s * 0.15f), accent, new Vector3(-s * 0.05f, -s * 0.02f, s * 0.68f), new Vector3(0, -20, 0), Vector3.One);
    }

    // ---- crocodile humanoid: bipedal, LONG SNOUT with teeth, back scutes, thick tapering tail ----
    private void Crocodile(float s, Material body, Material limb, Material accent)
    {
        float v = R(0.9f, 1.12f);
        float gh = s * 1.15f * v;
        _body = Pivot(this, new Vector3(0, gh, 0));
        _bodyBaseY = gh;
        float bw = s * 1.1f * v;
        Part(_body, Box(bw, gh, bw * 0.8f), body, Vector3.Zero, new Vector3(R(4, 10), 0, 0), Vector3.One);                     // torso
        Part(_body, Box(bw * 1.15f, gh * 0.35f, bw * 0.55f), limb, new Vector3(0, gh * 0.42f, -s * 0.05f), Vector3.Zero, Vector3.One);   // shoulders
        Part(_body, Box(bw * 0.7f, gh * 0.5f, bw * 0.45f), accent, new Vector3(0, gh * 0.05f, s * 0.42f), Vector3.Zero, Vector3.One);    // pale belly plate
        for (int sp = 0; sp < 5; sp++)   // ridged back scutes
            Part(_body, Cone(s * 0.14f, s * 0.28f), accent, new Vector3(0, gh * (0.0f + sp * 0.14f), -bw * 0.42f), new Vector3(-110, 0, 0), Vector3.One);

        // flat wide head + a LONG SNOUT jutting forward, upper + lower jaw with teeth, bulging eyes on top
        var head = Part(_body, Box(s * 0.5f, s * 0.32f, s * 0.5f), body, new Vector3(0, gh * 0.5f, s * 0.2f), Vector3.Zero, Vector3.One);
        Part(head, Box(s * 0.4f, s * 0.2f, s * 1.15f), body, new Vector3(0, s * 0.06f, s * 0.72f), Vector3.Zero, Vector3.One);          // upper snout
        Part(head, Box(s * 0.36f, s * 0.14f, s * 1.05f), limb, new Vector3(0, -s * 0.14f, s * 0.68f), Vector3.Zero, Vector3.One);       // lower jaw
        Part(head, Sph(s * 0.13f), accent, new Vector3(s * 0.18f, s * 0.24f, s * 0.1f), Vector3.Zero, Vector3.One);                     // eye
        Part(head, Sph(s * 0.13f), accent, new Vector3(-s * 0.18f, s * 0.24f, s * 0.1f), Vector3.Zero, Vector3.One);
        Part(head, Sph(s * 0.06f), limb, new Vector3(s * 0.18f, s * 0.28f, s * 0.12f), Vector3.Zero, Vector3.One);                      // pupils
        Part(head, Sph(s * 0.06f), limb, new Vector3(-s * 0.18f, s * 0.28f, s * 0.12f), Vector3.Zero, Vector3.One);
        for (int t = 0; t < 5; t++)   // teeth along the snout
        {
            float tz = s * (0.3f + t * 0.17f);
            Part(head, Cone(s * 0.05f, s * 0.15f), accent, new Vector3(s * 0.17f, -s * 0.02f, tz), new Vector3(180, 0, 0), Vector3.One);
            Part(head, Cone(s * 0.05f, s * 0.15f), accent, new Vector3(-s * 0.17f, -s * 0.02f, tz), new Vector3(180, 0, 0), Vector3.One);
        }
        Part(head, Sph(s * 0.06f), limb, new Vector3(s * 0.09f, s * 0.14f, s * 1.24f), Vector3.Zero, Vector3.One);                      // nostrils
        Part(head, Sph(s * 0.06f), limb, new Vector3(-s * 0.09f, s * 0.14f, s * 1.24f), Vector3.Zero, Vector3.One);

        // thick tapering tail sweeping back and down
        var tail = Pivot(_body, new Vector3(0, -gh * 0.15f, -bw * 0.5f));
        float seg = s * 0.42f;
        for (int t = 0; t < 4; t++)
            Part(tail, Box(s * (0.42f - t * 0.08f), s * (0.36f - t * 0.07f), seg), body, new Vector3(0, -t * s * 0.1f, -seg * 0.5f - t * seg * 0.55f), new Vector3(t * 7f, 0, 0), Vector3.One);

        // bipedal legs + short clawed arms
        float legLen = gh * 0.4f, armLen = gh * 0.45f;
        for (int i = 0; i < 2; i++)
        {
            float sx = i == 0 ? 1 : -1;
            var hip = Pivot(this, new Vector3(sx * bw * 0.3f, gh * 0.3f, 0));
            Part(hip, Cyl(s * 0.24f, legLen), limb, new Vector3(0, -legLen / 2f, 0), Vector3.Zero, Vector3.One);
            var knee = Pivot(hip, new Vector3(0, -legLen, 0));
            Part(knee, Cyl(s * 0.2f, legLen), limb, new Vector3(0, -legLen / 2f, 0), Vector3.Zero, Vector3.One);
            Part(knee, Box(s * 0.42f, s * 0.16f, s * 0.65f), limb, new Vector3(0, -legLen, s * 0.16f), Vector3.Zero, Vector3.One);
            _hips.Add(hip); _knees.Add(knee); _hipBase.Add(Vector3.Zero); _kneeBase.Add(Vector3.Zero);

            var arm = Pivot(_body, new Vector3(sx * bw * 0.55f, gh * 0.38f, 0));
            Part(arm, Cyl(s * 0.18f, armLen), limb, new Vector3(0, -armLen / 2f, 0), Vector3.Zero, Vector3.One);
            Part(arm, Sph(s * 0.22f), limb, new Vector3(0, -armLen, 0), Vector3.Zero, Vector3.One);
            _arms.Add(arm);
        }
    }

    // ---- goblin: spindly, hunched, big head, crooked nose, asymmetric ears ----
    private void Goblin(float s, Material body, Material limb, Material accent, bool bomber)
    {
        float v = bomber ? 0.8f : R(0.85f, 1.15f);    // per-instance size jitter
        float gh = s * 0.95f * v;
        _body = Pivot(this, new Vector3(0, gh, 0));
        _bodyBaseY = gh;
        float bw = s * 0.62f * v;
        Part(_body, Box(bw, gh * 0.9f, bw * 0.7f), body, Vector3.Zero, new Vector3(R(16, 26), 0, 0), Vector3.One);   // hunched torso
        Part(_body, Sph(s * 0.3f), body, new Vector3(R(-0.06f, 0.06f) * s, gh * 0.2f, -bw * 0.4f), Vector3.Zero, new Vector3(1.1f, 0.7f, 1f));   // hunchback lump

        // oversized head, jutting forward
        var head = Part(_body, Sph(s * 0.52f * v), body, new Vector3(0, gh * 0.55f, s * 0.22f), Vector3.Zero, new Vector3(1f, 0.92f, 1.05f));
        Part(head, Cone(s * 0.13f, s * 0.7f), body, new Vector3(R(-0.04f, 0.04f) * s, -s * 0.05f, s * 0.45f), new Vector3(R(78, 104), 0, 0), Vector3.One);   // long crooked nose
        float earL = R(0.45f, 0.8f), earR = R(0.45f, 0.8f);   // asymmetric ears
        Part(head, Cone(s * 0.16f, s * earL), limb, new Vector3(s * 0.45f, s * 0.12f, 0), new Vector3(0, 0, R(-95, -60)), Vector3.One);
        Part(head, Cone(s * 0.16f, s * earR), limb, new Vector3(-s * 0.45f, s * 0.12f, 0), new Vector3(0, 0, R(60, 95)), Vector3.One);
        Part(head, Sph(s * 0.09f), accent, new Vector3(s * 0.18f, s * 0.08f, s * 0.4f), Vector3.Zero, Vector3.One);     // beady eyes
        Part(head, Sph(s * 0.09f), accent, new Vector3(-s * 0.18f, s * 0.08f, s * 0.4f), Vector3.Zero, Vector3.One);
        Part(head, Box(s * 0.3f, s * 0.06f, s * 0.12f), limb, new Vector3(0, -s * 0.22f, s * 0.34f), new Vector3(R(-8, 8), 0, 0), Vector3.One);   // jagged mouth
        for (int w = 0; w < 3; w++)   // warts
            Part(head, Sph(s * R(0.04f, 0.07f)), limb, new Vector3(R(-0.4f, 0.4f) * s, R(-0.2f, 0.4f) * s, R(0.2f, 0.45f) * s), Vector3.Zero, Vector3.One);

        float legLen = gh * 0.5f, armLen = gh * 0.62f;   // long gangly arms
        for (int i = 0; i < 2; i++)
        {
            float sx = i == 0 ? 1 : -1;
            var hip = Pivot(this, new Vector3(sx * bw * 0.28f, gh * 0.4f, 0));
            Part(hip, Cyl(s * 0.11f, legLen), limb, new Vector3(0, -legLen / 2f, 0), Vector3.Zero, Vector3.One);
            var knee = Pivot(hip, new Vector3(0, -legLen, 0));
            Part(knee, Cyl(s * 0.09f, legLen), limb, new Vector3(0, -legLen / 2f, 0), Vector3.Zero, Vector3.One);
            Part(knee, Box(s * 0.26f, s * 0.1f, s * 0.5f), limb, new Vector3(0, -legLen, s * 0.12f), Vector3.Zero, Vector3.One);   // big splayed foot
            _hips.Add(hip); _knees.Add(knee); _hipBase.Add(Vector3.Zero); _kneeBase.Add(Vector3.Zero);

            var arm = Pivot(_body, new Vector3(sx * bw * 0.55f, gh * 0.32f, 0));
            Part(arm, Cyl(s * 0.09f, armLen), limb, new Vector3(0, -armLen / 2f, 0), Vector3.Zero, Vector3.One);
            Part(arm, Sph(s * 0.15f), limb, new Vector3(0, -armLen, 0), Vector3.Zero, Vector3.One);   // big knobbly hand
            _arms.Add(arm);
        }

        if (bomber)
        {
            // clutches an explosive keg to its chest, lit fuse sparking
            _keg = Pivot(_body, new Vector3(0, gh * 0.05f, s * 0.5f));
            Part(_keg, Cyl(s * 0.42f, s * 0.7f), limb, Vector3.Zero, new Vector3(90, 0, 0), Vector3.One);
            Part(_keg, Cyl(s * 0.44f, s * 0.1f), accent, new Vector3(0, 0, s * 0.18f), new Vector3(90, 0, 0), Vector3.One);   // iron band
            Part(_keg, Cyl(s * 0.03f, s * 0.4f), limb, new Vector3(0, s * 0.3f, 0), Vector3.Zero, Vector3.One);   // fuse
            Part(_keg, Sph(s * 0.12f), accent, new Vector3(0, s * 0.52f, 0), Vector3.Zero, Vector3.One);          // spark
        }
    }

    // ---- orc: massive, brutish, tiny sunken head, big tusks, back spikes ----
    private void Orc(float s, Material body, Material limb, Material accent)
    {
        float v = R(0.9f, 1.2f);
        float gh = s * 1.35f * v;
        _body = Pivot(this, new Vector3(0, gh, 0));
        _bodyBaseY = gh;
        float bw = s * 1.7f * v;
        Part(_body, Box(bw, gh, bw * 0.85f), body, Vector3.Zero, new Vector3(R(6, 14), 0, 0), Vector3.One);                  // huge torso
        Part(_body, Box(bw * 1.25f, gh * 0.4f, bw * 0.6f), limb, new Vector3(0, gh * 0.45f, -s * 0.1f), Vector3.Zero, Vector3.One);   // broad shoulders
        // back spikes
        for (int sp = 0; sp < 4; sp++)
            Part(_body, Cone(s * R(0.12f, 0.2f), s * R(0.4f, 0.7f)), limb, new Vector3(R(-0.3f, 0.3f) * s, gh * (0.1f + sp * 0.12f), -bw * 0.45f), new Vector3(-120, 0, 0), Vector3.One);

        // small head sunk between shoulders
        var head = Part(_body, Sph(s * 0.46f), body, new Vector3(0, gh * 0.5f, s * 0.18f), Vector3.Zero, new Vector3(1.15f, 0.85f, 1f));
        Part(head, Box(s * 0.7f, s * 0.18f, s * 0.3f), limb, new Vector3(0, s * 0.18f, s * 0.28f), new Vector3(-12, 0, 0), Vector3.One);   // heavy brow
        Part(head, Sph(s * 0.08f), accent, new Vector3(s * 0.16f, s * 0.04f, s * 0.36f), Vector3.Zero, Vector3.One);
        Part(head, Sph(s * 0.08f), accent, new Vector3(-s * 0.16f, s * 0.04f, s * 0.36f), Vector3.Zero, Vector3.One);
        Part(head, Box(s * 0.6f, s * 0.22f, s * 0.28f), limb, new Vector3(0, -s * 0.22f, s * 0.3f), new Vector3(8, 0, 0), Vector3.One);   // jutting jaw
        Part(head, Cone(s * 0.13f, s * R(0.45f, 0.65f)), accent, new Vector3(s * 0.22f, -s * 0.25f, s * 0.4f), new Vector3(R(-30, -10), 0, 0), Vector3.One);   // tusks up
        Part(head, Cone(s * 0.13f, s * R(0.45f, 0.65f)), accent, new Vector3(-s * 0.22f, -s * 0.25f, s * 0.4f), new Vector3(R(-30, -10), 0, 0), Vector3.One);

        float legLen = gh * 0.42f, armLen = gh * 0.6f;   // thick stubby legs, long dragging arms
        for (int i = 0; i < 2; i++)
        {
            float sx = i == 0 ? 1 : -1;
            var hip = Pivot(this, new Vector3(sx * bw * 0.34f, gh * 0.34f, 0));
            Part(hip, Cyl(s * 0.3f, legLen), limb, new Vector3(0, -legLen / 2f, 0), Vector3.Zero, Vector3.One);
            var knee = Pivot(hip, new Vector3(0, -legLen, 0));
            Part(knee, Cyl(s * 0.26f, legLen), limb, new Vector3(0, -legLen / 2f, 0), Vector3.Zero, Vector3.One);
            Part(knee, Box(s * 0.5f, s * 0.18f, s * 0.7f), limb, new Vector3(0, -legLen, s * 0.12f), Vector3.Zero, Vector3.One);   // big feet
            _hips.Add(hip); _knees.Add(knee); _hipBase.Add(Vector3.Zero); _kneeBase.Add(Vector3.Zero);

            var arm = Pivot(_body, new Vector3(sx * bw * 0.62f, gh * 0.4f, 0));
            Part(arm, Cyl(s * 0.24f, armLen), limb, new Vector3(0, -armLen / 2f, 0), Vector3.Zero, Vector3.One);
            Part(arm, Sph(s * 0.32f), limb, new Vector3(0, -armLen, 0), Vector3.Zero, Vector3.One);   // huge fists
            _arms.Add(arm);
        }
    }

    // ---- THE HOLLOW MOON: tall half-orc / half-zombie. LEFT body alive, RIGHT body rotting; a giant HOLE bored
    // clean through his midsection (he's hollow); a ZOMBIE goblin perched on the left shoulder + a NON-zombie
    // goblin on the right. Legs index 0 = left(alive), 1 = right(zombie, drags in the walk). ----
    private void HollowBoss(float s, Material body, Material limb, Material accent)
    {
        float gh = s * 1.55f;
        _body = Pivot(this, new Vector3(0, gh, 0));
        _bodyBaseY = gh;
        float bw = s * 1.7f;

        var aBody = Game.Toon(new Color(0.46f, 0.56f, 0.36f), 0.95f, 0.25f);   // living orc flesh (left)
        var aLimb = Game.Toon(new Color(0.34f, 0.42f, 0.26f), 0.95f, 0.2f);
        var zBody = Game.Toon(new Color(0.40f, 0.44f, 0.34f), 0.95f, 0.2f);    // rotting green-gray (right)
        var zLimb = Game.Toon(new Color(0.28f, 0.32f, 0.24f), 0.95f, 0.2f);
        var deadEye = Game.Emissive(new Color(1f, 0.18f, 0.12f), 2.2f);        // dead red (zombie side)

        // UPPER CHEST — split alive / zombie
        Part(_body, Box(bw * 0.52f, gh * 0.42f, bw * 0.62f), aBody, new Vector3(-bw * 0.26f, gh * 0.3f, 0), new Vector3(8, 0, 0), Vector3.One);
        Part(_body, Box(bw * 0.52f, gh * 0.42f, bw * 0.62f), zBody, new Vector3(bw * 0.26f, gh * 0.3f, 0), new Vector3(8, 0, 0), Vector3.One);
        // broad shoulders (split)
        Part(_body, Box(bw * 0.72f, gh * 0.28f, bw * 0.62f), aLimb, new Vector3(-bw * 0.36f, gh * 0.56f, -s * 0.05f), Vector3.Zero, Vector3.One);
        Part(_body, Box(bw * 0.72f, gh * 0.28f, bw * 0.62f), zLimb, new Vector3(bw * 0.36f, gh * 0.56f, -s * 0.05f), Vector3.Zero, Vector3.One);

        // THE HOLLOW HOLE — a big vertical ring you see straight through, with ragged flesh strands across it
        float holeR = bw * 0.44f;
        Part(_body, new TorusMesh { InnerRadius = holeR * 0.7f, OuterRadius = holeR, Rings = 20, RingSegments = 20 }, aLimb, new Vector3(0, -gh * 0.02f, 0), new Vector3(90, 0, 0), Vector3.One);
        Part(_body, Cyl(s * 0.06f, holeR * 1.4f), zLimb, new Vector3(bw * 0.06f, -gh * 0.02f, 0), new Vector3(0, 0, 74), Vector3.One);   // torn strand
        Part(_body, Cyl(s * 0.05f, holeR * 1.15f), aLimb, new Vector3(-bw * 0.05f, gh * 0.05f, 0), new Vector3(0, 0, -54), Vector3.One);

        // LOWER BODY / pelvis (split) + a back spine behind the hole so he's one piece
        Part(_body, Box(bw * 0.5f, gh * 0.32f, bw * 0.6f), aBody, new Vector3(-bw * 0.26f, -gh * 0.44f, 0), Vector3.Zero, Vector3.One);
        Part(_body, Box(bw * 0.5f, gh * 0.32f, bw * 0.6f), zBody, new Vector3(bw * 0.26f, -gh * 0.44f, 0), Vector3.Zero, Vector3.One);
        Part(_body, Box(bw * 0.34f, gh * 0.72f, bw * 0.2f), zLimb, new Vector3(0, -gh * 0.05f, -bw * 0.36f), Vector3.Zero, Vector3.One);   // spine

        // HEAD — split alive / zombie, sunk between the shoulders
        var head = Pivot(_body, new Vector3(0, gh * 0.66f, s * 0.12f));
        Part(head, Sph(s * 0.5f), aBody, new Vector3(-s * 0.24f, 0, 0), Vector3.Zero, new Vector3(1.1f, 0.92f, 1f));
        Part(head, Sph(s * 0.5f), zBody, new Vector3(s * 0.24f, 0, 0), Vector3.Zero, new Vector3(1.1f, 0.92f, 1f));
        Part(head, Box(s * 0.95f, s * 0.2f, s * 0.3f), aLimb, new Vector3(0, s * 0.24f, s * 0.28f), new Vector3(-12, 0, 0), Vector3.One);   // heavy brow
        Part(head, Sph(s * 0.1f), accent, new Vector3(-s * 0.2f, s * 0.02f, s * 0.42f), Vector3.Zero, Vector3.One);     // left eye: bright moonlight
        Part(head, Sph(s * 0.1f), deadEye, new Vector3(s * 0.2f, s * 0.02f, s * 0.42f), Vector3.Zero, Vector3.One);     // right eye: dead red
        Part(head, Box(s * 0.7f, s * 0.22f, s * 0.28f), aLimb, new Vector3(0, -s * 0.24f, s * 0.32f), new Vector3(8, 0, 0), Vector3.One);   // jutting jaw
        Part(head, Cone(s * 0.14f, s * 0.62f), accent, new Vector3(s * 0.24f, -s * 0.26f, s * 0.44f), new Vector3(-20, 0, 0), Vector3.One);   // tusks
        Part(head, Cone(s * 0.14f, s * 0.62f), accent, new Vector3(-s * 0.24f, -s * 0.26f, s * 0.44f), new Vector3(-20, 0, 0), Vector3.One);

        // SHOULDER RIDERS — left = a real ZOMBIE (pestilence caster), right = a real GOBLIN (mine-thrower)
        float gs = s * 0.42f;
        _gobZombie = new Creature();
        _gobZombie.Build(CreatureKind.Zombie, gs, Game.Toon(new Color(0.40f, 0.47f, 0.30f), 0.95f, 0.2f), Game.Toon(new Color(0.26f, 0.30f, 0.20f), 0.95f, 0.2f), Game.Emissive(new Color(1f, 0.2f, 0.15f), 2f));
        var lp = Pivot(_body, new Vector3(-bw * 0.44f, gh * 0.6f, -s * 0.02f));
        lp.AddChild(_gobZombie);
        _gobNormal = new Creature();
        _gobNormal.Build(CreatureKind.Goblin, gs, Game.Toon(new Color(0.42f, 0.6f, 0.25f), 0.95f, 0.2f), Game.Toon(new Color(0.28f, 0.42f, 0.18f), 0.95f, 0.2f), Game.Emissive(new Color(1f, 0.85f, 0.2f), 2f));
        var rp = Pivot(_body, new Vector3(bw * 0.44f, gh * 0.6f, -s * 0.02f));
        rp.AddChild(_gobNormal);

        // LEGS + ARMS — index 0 left(alive), 1 right(zombie, drags)
        float legLen = gh * 0.52f, armLen = gh * 0.68f;
        for (int i = 0; i < 2; i++)
        {
            float sx = i == 0 ? -1 : 1;
            var lm = i == 0 ? aLimb : zLimb;
            var hip = Pivot(this, new Vector3(sx * bw * 0.3f, gh * 0.26f, 0));
            Part(hip, Cyl(s * 0.33f, legLen), lm, new Vector3(0, -legLen / 2f, 0), Vector3.Zero, Vector3.One);
            var knee = Pivot(hip, new Vector3(0, -legLen, 0));
            Part(knee, Cyl(s * 0.29f, legLen), lm, new Vector3(0, -legLen / 2f, 0), Vector3.Zero, Vector3.One);
            Part(knee, Box(s * 0.55f, s * 0.2f, s * 0.78f), lm, new Vector3(0, -legLen, s * 0.12f), Vector3.Zero, Vector3.One);
            _hips.Add(hip); _knees.Add(knee); _hipBase.Add(Vector3.Zero); _kneeBase.Add(Vector3.Zero);

            var arm = Pivot(_body, new Vector3(sx * bw * 0.56f, gh * 0.42f, 0));
            Part(arm, Cyl(s * 0.27f, armLen), lm, new Vector3(0, -armLen / 2f, 0), Vector3.Zero, Vector3.One);
            Part(arm, Sph(s * 0.36f), lm, new Vector3(0, -armLen, 0), Vector3.Zero, Vector3.One);   // big fists
            _arms.Add(arm);
        }
    }

    private void ShoulderGoblin(float x, float y, Material gbody, Material glimb, Color eyeCol, float s)
    {
        var g = Pivot(_body, new Vector3(x, y, -s * 0.04f));
        g.Scale = Vector3.One * 0.52f;   // perched mini-goblin
        Part(g, Sph(s * 0.55f), gbody, Vector3.Zero, new Vector3(14, 0, 0), new Vector3(1f, 0.9f, 1f));   // hunched body
        var gheadN = Part(g, Sph(s * 0.5f), gbody, new Vector3(0, s * 0.62f, s * 0.05f), Vector3.Zero, Vector3.One);
        Part(gheadN, Cone(s * 0.16f, s * 0.62f), glimb, new Vector3(s * 0.42f, s * 0.16f, 0), new Vector3(0, 0, -68), Vector3.One);   // ears
        Part(gheadN, Cone(s * 0.16f, s * 0.62f), glimb, new Vector3(-s * 0.42f, s * 0.16f, 0), new Vector3(0, 0, 68), Vector3.One);
        var eye = Game.Emissive(eyeCol, 2.4f);
        Part(gheadN, Sph(s * 0.11f), eye, new Vector3(s * 0.17f, 0, s * 0.36f), Vector3.Zero, Vector3.One);
        Part(gheadN, Sph(s * 0.11f), eye, new Vector3(-s * 0.17f, 0, s * 0.36f), Vector3.Zero, Vector3.One);
        Part(gheadN, Box(s * 0.42f, s * 0.1f, s * 0.2f), glimb, new Vector3(0, -s * 0.28f, s * 0.3f), Vector3.Zero, Vector3.One);   // fanged grin
    }

    // ---- spider: bent legs (femur out/up, tibia down), animated tetrapod stepping ----
    private void Spider(float s, Material body, Material limb, Material accent)
    {
        float v = R(0.85f, 1.2f);
        s *= v;
        _body = Pivot(this, new Vector3(0, s * 0.85f, 0));
        _bodyBaseY = s * 0.85f;
        Part(_body, Sph(s * 0.75f), body, new Vector3(0, 0, -s * 0.55f), Vector3.Zero, new Vector3(1, 0.78f, 1.25f));   // abdomen
        var ceph = Part(_body, Sph(s * 0.5f), body, new Vector3(0, 0, s * 0.4f), Vector3.Zero, Vector3.One);            // cephalothorax
        for (int e = 0; e < 6; e++)
            Part(ceph, Sph(s * R(0.05f, 0.09f)), accent, new Vector3((e % 2 == 0 ? 1 : -1) * s * R(0.12f, 0.24f), s * 0.18f, s * (0.35f - (e / 2) * 0.1f)), Vector3.Zero, Vector3.One);   // eye cluster
        Part(ceph, Cone(s * 0.1f, s * 0.45f), limb, new Vector3(s * 0.16f, -s * 0.12f, s * 0.46f), new Vector3(125, 0, 0), Vector3.One);   // fangs
        Part(ceph, Cone(s * 0.1f, s * 0.45f), limb, new Vector3(-s * 0.16f, -s * 0.12f, s * 0.46f), new Vector3(125, 0, 0), Vector3.One);

        float femur = s * 0.7f, tibia = s * 0.95f;
        for (int i = 0; i < 8; i++)
        {
            int side = i < 4 ? 1 : -1;
            int idx = i % 4;
            float along = (idx - 1.5f) * s * 0.34f;
            float yaw = side * (50f + idx * 16f);
            // hip: femur juts outward and slightly up
            var hip = Pivot(this, new Vector3(side * s * 0.42f, s * 0.78f, along + s * 0.15f), new Vector3(-28f, yaw, side * -8f));
            Part(hip, Cyl(s * 0.06f, femur), limb, new Vector3(0, -femur / 2f, 0), Vector3.Zero, Vector3.One);
            // knee bends the tibia back down toward the ground
            var knee = Pivot(hip, new Vector3(0, -femur, 0), new Vector3(95f, 0, 0));
            Part(knee, Cyl(s * 0.05f, tibia), limb, new Vector3(0, -tibia / 2f, 0), Vector3.Zero, Vector3.One);
            _hips.Add(hip); _knees.Add(knee);
            _hipBase.Add(new Vector3(-28f, yaw, side * -8f));
            _kneeBase.Add(new Vector3(95f, 0, 0));
        }
    }

    // ---- mosquito ----
    // ---- insect wing membrane: a translucent, veined, iridescent plane (replaces the old solid box wings) ----
    private static Shader _wingShader;
    private const string WingCode = @"
shader_type spatial;
render_mode cull_disabled, depth_draw_opaque, diffuse_lambert, specular_disabled;   // LIT + matte → reads painterly like the world
uniform vec3 tint : source_color = vec3(0.34, 0.30, 0.26);   // muted membrane
uniform vec3 vein_col : source_color = vec3(0.14, 0.11, 0.09);
varying vec2 uvw;
varying vec3 wp;
float hash13(vec3 p){ p=fract(p*0.1031); p+=dot(p,p.yzx+33.33); return fract((p.x+p.y)*p.z); }
float vnoise(vec3 p){ vec3 i=floor(p),f=fract(p); f=f*f*(3.0-2.0*f);
    float n=mix(mix(mix(hash13(i),hash13(i+vec3(1,0,0)),f.x),mix(hash13(i+vec3(0,1,0)),hash13(i+vec3(1,1,0)),f.x),f.y),
                mix(mix(hash13(i+vec3(0,0,1)),hash13(i+vec3(1,0,1)),f.x),mix(hash13(i+vec3(0,1,1)),hash13(i+vec3(1,1,1)),f.x),f.y),f.z); return n; }
float fbm(vec3 p){ float a=0.0,m=0.5; a+=m*vnoise(p); p*=2.02; m*=0.5; a+=m*vnoise(p); p*=2.03; m*=0.5; a+=m*vnoise(p); return a; }
float wing_mask(vec2 uv){ float root=smoothstep(0.0,0.10,uv.x); float tip=1.0-smoothstep(0.86,1.0,uv.x); float hw=root*tip*0.5; return 1.0-smoothstep(hw-0.06,hw,abs(uv.y-0.5)); }
void vertex(){ uvw=UV; wp=(MODEL_MATRIX*vec4(VERTEX,1.0)).xyz; }
void fragment(){
    float mask=wing_mask(uvw);
    if(mask<0.02) discard;
    float m=fbm(wp*4.0 + vec3(uvw*4.0,0.0));                 // brushy painterly mottle
    // ORGANIC veins: one soft leading-edge curve + a couple of faint fbm-broken longitudinal hints (NOT hard parallel stripes)
    float lead=smoothstep(0.08,0.0,abs(uvw.y-(0.5+0.40*sin(uvw.x*1.2))));
    float longv=smoothstep(0.05,0.0,abs(fract((uvw.y-0.5)*2.0+(m-0.5)*0.8)-0.5))*smoothstep(0.12,0.5,uvw.x)*0.45;
    float v=max(lead, longv);
    vec3 col=tint*(1.0+(m-0.5)*2.0*0.32);                    // painterly value drift
    col=mix(col, vein_col, v*0.7);
    ALBEDO=clamp(col,vec3(0.0),vec3(1.0));
    ROUGHNESS=0.99; METALLIC=0.0;                            // dead matte
    ALPHA=mask*clamp(0.22+v*0.38+(m-0.5)*0.12, 0.08, 0.7);   // more see-through membrane
}";
    private static Material WingMat(Color accentCol)
    {
        _wingShader ??= new Shader { Code = WingCode };
        var m = new ShaderMaterial { Shader = _wingShader };
        // HEAVILY muted + desaturated membrane, only a whisper of the flyer's hue — painterly smoke, not neon glass
        float lum = accentCol.R * 0.3f + accentCol.G * 0.5f + accentCol.B * 0.2f;
        Color memb = accentCol.Lerp(new Color(lum, lum, lum), 0.72f).Darkened(0.5f);
        m.SetShaderParameter("tint", new Vector3(memb.R + 0.14f, memb.G + 0.13f, memb.B + 0.12f));
        m.SetShaderParameter("vein_col", new Vector3(memb.R * 0.45f, memb.G * 0.45f, memb.B * 0.45f));
        return m;
    }

    private void Mosquito(float s, Material body, Material limb, Material accent)
    {
        float v = R(0.85f, 1.15f); s *= v;
        _body = Pivot(this, Vector3.Zero);
        _bodyBaseY = 0f;
        // (REWORK) a DARK chitin carapace (the neon hue, heavily darkened) so the body reads as an insect instead of a bloom-blob;
        // the neon accent is kept for the EYES, antennae tips, segment-joint bands and an underbelly glow (bioluminescent synth-bug).
        Color accCol = (accent as StandardMaterial3D)?.AlbedoColor ?? new Color(0.5f, 0.7f, 1f);
        Color chitCol = new Color(accCol.R * 0.22f + 0.04f, accCol.G * 0.22f + 0.04f, accCol.B * 0.24f + 0.05f);
        // (PAINTERLY) matte chitin on the painterly master material (world-space macro value/hue drift + fine grain) so the body
        // reads hand-painted like the rest of the world, not glossy toon; accents use painterly MASKED emission (a soft glow, not a flat neon blob).
        var carapace = Vis.Painterly(chitCol, rough: 0.85f, roughVar: 0.16f, macroValue: 0.18f, macroHue: 0.06f, macroScale: 0.7f, detailScale: 5.0f, detailValue: 0.14f);
        var glow = Vis.Painterly(accCol, rough: 0.8f, roughVar: 0.1f, macroValue: 0.12f, macroHue: 0.04f, macroScale: 0.5f, emission: accCol, emissionEnergy: 2.2f, emissionThreshold: 0.3f);
        Part(_body, Sph(s * 0.45f), carapace, new Vector3(0, 0, s * 0.5f), Vector3.Zero, Vector3.One);          // head
        Part(_body, Sph(s * 0.55f), carapace, Vector3.Zero, Vector3.Zero, Vector3.One);                        // thorax
        Part(_body, Cyl(s * 0.26f, s * 1.4f), carapace, new Vector3(0, 0, -s * 0.9f), new Vector3(90, 0, 0), new Vector3(1, 1, 0.6f));   // abdomen
        Part(_body, Cone(s * 0.05f, s * 1.1f), carapace, new Vector3(0, -s * 0.1f, s * 1.25f), new Vector3(90, 0, 0), Vector3.One);       // proboscis
        Part(_body, Sph(s * 0.17f), glow, new Vector3(s * 0.24f, s * 0.12f, s * 0.6f), Vector3.Zero, new Vector3(1.1f, 1.1f, 1f));        // glowing compound eyes
        Part(_body, Sph(s * 0.17f), glow, new Vector3(-s * 0.24f, s * 0.12f, s * 0.6f), Vector3.Zero, new Vector3(1.1f, 1.1f, 1f));
        // segmented abdomen — matte carapace segments with a soft GLOWING joint band between each (bioluminescent rings)
        for (int i = 0; i < 3; i++)
        {
            float z = -s * (0.55f + i * 0.4f);
            Part(_body, Sph(s * (0.28f - i * 0.045f)), carapace, new Vector3(0, 0, z), Vector3.Zero, new Vector3(1.16f, 1.12f, 0.5f));
            Part(_body, Sph(s * (0.24f - i * 0.045f)), glow, new Vector3(0, -s * 0.02f, z + s * 0.2f), Vector3.Zero, new Vector3(1.05f, 0.5f, 0.16f));   // glow ring at the joint
        }
        // antennae — thin matte feelers from the head with a glowing tip
        for (int i = 0; i < 2; i++)
        {
            float ax = i == 0 ? 1 : -1;
            var ant = Pivot(_body, new Vector3(ax * s * 0.12f, s * 0.3f, s * 0.5f), new Vector3(-38f, ax * 12f, 0));
            Part(ant, Cyl(s * 0.022f, s * 0.9f), carapace, new Vector3(0, s * 0.45f, 0), Vector3.Zero, Vector3.One);
            Part(ant, Sph(s * 0.055f), glow, new Vector3(0, s * 0.9f, 0), Vector3.Zero, Vector3.One);
        }
        // (NEW) translucent VEINED wing membranes (a plane + insect-wing shader) instead of solid boxes — the big "not primitive" win
        var wingMat = WingMat(accCol);
        for (int i = 0; i < 2; i++)
        {
            float sx = i == 0 ? 1 : -1;
            var w = Pivot(_body, new Vector3(sx * s * 0.2f, s * 0.35f, 0));
            var wm = new MeshInstance3D
            {
                Mesh = new PlaneMesh { Size = new Vector2(s * 1.6f, s * 0.78f) },   // XZ plane, normal +Y — matches the old flat wing footprint
                MaterialOverride = wingMat,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,           // translucent → no solid shadow
                Position = new Vector3(sx * s * 0.8f, 0, -s * 0.1f),
                Scale = new Vector3(sx, 1f, 1f),                                     // mirror the left wing so both fan outward from the root
            };
            w.AddChild(wm);
            _wings.Add(w);
        }
        for (int i = 0; i < 6; i++)
        {
            int side = i < 3 ? 1 : -1;
            float along = ((i % 3) - 1) * s * 0.4f;
            var hip = Pivot(_body, new Vector3(side * s * 0.4f, -s * 0.2f, along), new Vector3(0, side * 30f, side * 50f));
            Part(hip, Cyl(s * 0.035f, s * 1.3f), carapace, new Vector3(0, -s * 0.65f, 0), Vector3.Zero, Vector3.One);   // matte painterly chitin legs (was toon-outlined blue)
            _hips.Add(hip); _knees.Add(null);
            _hipBase.Add(new Vector3(0, side * 30f, side * 50f)); _kneeBase.Add(Vector3.Zero);
        }
    }

    // ---- zapper: a floating hooded wizard that rears back and casts lightning ----
    private void Zapper(float s, Material body, Material limb, Material accent)
    {
        float v = R(0.9f, 1.1f); s *= v;
        _body = Pivot(this, new Vector3(0, s * 1.35f, 0));   // hovers
        _bodyBaseY = s * 1.35f;
        Part(_body, new CylinderMesh { TopRadius = s * 0.25f, BottomRadius = s * 0.95f, Height = s * 1.6f }, body, new Vector3(0, -s * 0.5f, 0), Vector3.Zero, Vector3.One);   // robe
        Part(_body, new CylinderMesh { TopRadius = s * 0.3f, BottomRadius = s * 0.55f, Height = s * 0.2f }, limb, new Vector3(0, -s * 1.25f, 0), Vector3.Zero, Vector3.One);   // tattered hem
        Part(_body, Sph(s * 0.38f), body, new Vector3(0, s * 0.4f, 0), Vector3.Zero, new Vector3(1, 1.1f, 1));   // shoulders
        var head = Part(_body, Sph(s * 0.32f), body, new Vector3(0, s * 0.78f, 0.02f), Vector3.Zero, Vector3.One);
        Part(head, Cone(s * 0.5f, s * 0.85f), limb, new Vector3(0, s * 0.2f, -s * 0.04f), new Vector3(10, 0, 0), Vector3.One);   // pointed hood
        Part(head, Sph(s * 0.07f), accent, new Vector3(s * 0.12f, -s * 0.02f, s * 0.27f), Vector3.Zero, Vector3.One);            // eyes
        Part(head, Sph(s * 0.07f), accent, new Vector3(-s * 0.12f, -s * 0.02f, s * 0.27f), Vector3.Zero, Vector3.One);
        for (int i = 0; i < 2; i++)
        {
            float sx = i == 0 ? 1 : -1;
            var arm = Pivot(_body, new Vector3(sx * s * 0.42f, s * 0.5f, 0));
            Part(arm, Cyl(s * 0.1f, s * 0.7f), limb, new Vector3(0, -s * 0.35f, 0), Vector3.Zero, Vector3.One);
            Part(arm, Sph(s * 0.13f), accent, new Vector3(0, -s * 0.72f, 0), Vector3.Zero, Vector3.One);   // glowing hands
            _arms.Add(arm);
        }
        _orb = Part(_body, Sph(s * 0.26f), accent, new Vector3(0, s * 0.55f, s * 0.55f), Vector3.Zero, Vector3.One);   // focus orb
        for (int i = 0; i < 4; i++)
            Part(_orb, Cone(s * 0.05f, s * 0.32f), accent, Vector3.Zero, new Vector3(R(0, 360), R(0, 360), R(0, 360)), Vector3.One);   // crackle spikes
    }

    public void Animate(float dt, float move)
    {
        move = Mathf.Clamp(move, 0f, 1f);
        if (_gobAuthored) { AnimateGoblin(dt, move); return; }   // authored goblin: GLB walk + procedural slash (handles its own cull)
        if (AnimSuspended) return;   // (PERF) invisible foe (far + outside the frustum) → freeze the pose, skip all the per-part transform writes
        switch (_kind)
        {
            case CreatureKind.Goblin:
            case CreatureKind.Bomber:
            case CreatureKind.Orc:
            case CreatureKind.Crocodile:
            case CreatureKind.Troll:
            case CreatureKind.Pigmy:
                {
                    float spd = (_kind == CreatureKind.Orc || _kind == CreatureKind.Troll) ? 3f : (_kind == CreatureKind.Bomber || _kind == CreatureKind.Pigmy ? 6f : 4.5f);
                    _phase += dt * (1.5f + move * spd);
                    float sw = (8f + move * ((_kind == CreatureKind.Orc || _kind == CreatureKind.Troll) ? 30f : 40f));
                    for (int i = 0; i < _hips.Count; i++)
                    {
                        float a = Mathf.Sin(_phase + i * Mathf.Pi);
                        _hips[i].RotationDegrees = new Vector3(a * sw, 0, 0);
                        if (_knees[i] != null) _knees[i].RotationDegrees = new Vector3(Mathf.Max(0f, a) * sw * 0.9f, 0, 0);
                    }
                    for (int i = 0; i < _arms.Count; i++)
                        _arms[i].RotationDegrees = new Vector3(-Mathf.Sin(_phase + i * Mathf.Pi) * sw * 0.6f, 0, 0);
                    // melee swing overlay: arms rear back through the wind-up, then slam forward on the strike
                    _swing = Mathf.MoveToward(_swing, _swingTarget, dt * 3.5f);
                    _strike = Mathf.MoveToward(_strike, 0f, dt * 7f);
                    if (_swing > 0.01f || _strike > 0.01f)
                    {
                        float raise = _swing * 80f - _strike * 150f;
                        for (int i = 0; i < _arms.Count; i++)
                            _arms[i].RotationDegrees = new Vector3(-raise, 0, _arms[i].RotationDegrees.Z);
                    }
                    if (_body != null) _body.Position = new Vector3(0, _bodyBaseY + Mathf.Abs(Mathf.Sin(_phase)) * _scale * 0.06f * (0.5f + move), 0);
                    if (_keg != null) _keg.RotationDegrees = new Vector3(Mathf.Sin(_phase * 2f) * 5f, 0, 0);
                }
                break;

            case CreatureKind.HollowBoss:
                {
                    _phase += dt * (1.3f + move * 3.0f);   // slow and heavy
                    float sw = 8f + move * 26f;
                    for (int i = 0; i < _hips.Count; i++)
                    {
                        float a = Mathf.Sin(_phase + i * Mathf.Pi);
                        float drag = i == 1 ? 0.42f : 1f;   // RIGHT (zombie) leg barely lifts → it drags/lurches
                        _hips[i].RotationDegrees = new Vector3(a * sw * drag - (i == 1 ? 8f : 0f), 0, 0);
                        if (_knees[i] != null) _knees[i].RotationDegrees = new Vector3(Mathf.Max(0f, a) * sw * (i == 1 ? 0.35f : 0.9f), 0, 0);
                    }
                    for (int i = 0; i < _arms.Count; i++)
                        _arms[i].RotationDegrees = new Vector3(-Mathf.Sin(_phase + i * Mathf.Pi) * sw * 0.5f + (i == 1 ? -16f : 0f), 0, (i == 1 ? 8f : 0f));   // right arm hangs limp
                    _swing = Mathf.MoveToward(_swing, _swingTarget, dt * 3.5f);
                    _strike = Mathf.MoveToward(_strike, 0f, dt * 7f);
                    if (_swing > 0.01f || _strike > 0.01f)
                    {
                        float raise = _swing * 80f - _strike * 150f;
                        for (int i = 0; i < _arms.Count; i++) _arms[i].RotationDegrees = new Vector3(-raise, 0, _arms[i].RotationDegrees.Z);
                    }
                    if (_body != null)
                    {
                        _body.RotationDegrees = new Vector3(4f + Mathf.Sin(_phase) * 2f, Mathf.Sin(_phase * 0.5f) * 3f, 5f + Mathf.Sin(_phase) * 3f);   // lurch/lean toward the dead side
                        _body.Position = new Vector3(0, _bodyBaseY + Mathf.Abs(Mathf.Sin(_phase)) * _scale * 0.05f * (0.5f + move), 0);
                    }
                    _gobFireZ = Mathf.MoveToward(_gobFireZ, 0f, dt * 1.5f);
                    _gobFireN = Mathf.MoveToward(_gobFireN, 0f, dt * 1.5f);
                    _gobZombie?.Animate(dt, _gobFireZ > 0.05f ? 0.6f : 0.04f);   // idle sway; lunge/cast when firing
                    _gobNormal?.Animate(dt, _gobFireN > 0.05f ? 0.6f : 0.04f);
                    if (StompWind > 0.01f && _hips.Count > 0) _hips[0].RotationDegrees = new Vector3(-StompWind * 70f, 0, 0);   // wind-up: raise the good leg; it slams down on release
                }
                break;

            case CreatureKind.Zombie:
                {
                    _phase += dt * (1.1f + move * 3.2f);   // slow, dragging shamble
                    bool idlePosing = move < 0.06f && IdlePose > 0 && _scream <= 0.01f;
                    float dropY = idlePosing ? (IdlePose == 1 ? -_scale * 0.55f : IdlePose == 2 ? -_scale * 0.28f : 0f) : 0f;
                    Position = new Vector3(Position.X, Mathf.MoveToward(Position.Y, dropY, dt * 5f), Position.Z);   // sit lie/slump/stun on the floor, not floating
                    if (idlePosing) ZombieIdlePose();
                    else
                    {
                        float swz = 6f + move * 20f;
                        for (int i = 0; i < _hips.Count; i++)
                        {
                            float a = Mathf.Sin(_phase + i * Mathf.Pi);
                            float drag = i == 0 ? 1f : 0.55f;   // one leg drags → limp
                            _hips[i].RotationDegrees = new Vector3(a * swz * drag, 0, 0);
                            if (_knees[i] != null) _knees[i].RotationDegrees = new Vector3(Mathf.Max(0f, a) * swz * 0.7f, 0, 0);
                        }
                        for (int i = 0; i < _arms.Count; i++)
                            _arms[i].RotationDegrees = new Vector3(-72f + Mathf.Sin(_phase * 0.8f + i) * 8f, 0, (i == 0 ? 10f : -10f) + Mathf.Sin(_phase) * 4f);   // arms dangle forward, reaching
                        _swing = Mathf.MoveToward(_swing, _swingTarget, dt * 3.5f);
                        _strike = Mathf.MoveToward(_strike, 0f, dt * 7f);
                        if (_strike > 0.01f)
                            for (int i = 0; i < _arms.Count; i++)
                                _arms[i].RotationDegrees = new Vector3(-72f - _strike * 70f, 0, _arms[i].RotationDegrees.Z);   // lunge on hit
                        if (_body != null)
                        {
                            _body.RotationDegrees = new Vector3(18f + Mathf.Sin(_phase * 0.6f) * 4f, Mathf.Sin(_phase * 0.5f) * 6f, Mathf.Sin(_phase) * 5f);   // hunched, swaying
                            _body.Position = new Vector3(Mathf.Sin(_phase * 0.5f) * _scale * 0.04f, _bodyBaseY + Mathf.Abs(Mathf.Sin(_phase)) * _scale * 0.03f, 0);
                        }
                    }
                    _scream = Mathf.MoveToward(_scream, 0f, dt * 0.8f);   // decays over ~1.2s
                    if (_scream > 0.01f)
                    {
                        for (int i = 0; i < _arms.Count; i++)
                            _arms[i].RotationDegrees = new Vector3(-150f, 0, (i == 0 ? 18f : -18f));   // arms up + out, palms to sky
                        if (_body != null) _body.RotationDegrees = new Vector3(-24f + Mathf.Sin(_phase * 9f) * 3f, 0, 0);   // head back, shrieking, shuddering
                    }
                }
                break;
            case CreatureKind.Spider:
                {
                    _phase += dt * (4f + move * 9f);
                    for (int i = 0; i < _hips.Count; i++)
                    {
                        int group = (i % 2) ^ ((i / 4) & 1);          // alternating tetrapod
                        float ph = _phase + group * Mathf.Pi;
                        float swing = Mathf.Sin(ph) * (4f + move * 16f);     // fore/aft sweep
                        float lift = Mathf.Max(0f, Mathf.Sin(ph)) * (move * 34f);   // raise foot on the forward half
                        var hb = _hipBase[i]; var kb = _kneeBase[i];
                        _hips[i].RotationDegrees = new Vector3(hb.X, hb.Y + swing, hb.Z);
                        if (_knees[i] != null) _knees[i].RotationDegrees = new Vector3(kb.X - lift, kb.Y, kb.Z);
                    }
                    if (_body != null) _body.Position = new Vector3(0, _bodyBaseY + Mathf.Abs(Mathf.Sin(_phase * 2f)) * _scale * 0.04f, 0);
                }
                break;

            case CreatureKind.Zapper:
                {
                    _cast = Mathf.MoveToward(_cast, _castTarget, dt * 4f);
                    _phase += dt * (1.6f + _cast * 4f);
                    if (_body != null) _body.Position = new Vector3(0, _bodyBaseY + Mathf.Sin(_phase) * _scale * 0.12f, 0);
                    for (int i = 0; i < _arms.Count; i++)
                        _arms[i].RotationDegrees = new Vector3(-25f - _cast * 120f, 0, (i == 0 ? 1 : -1) * (10f + _cast * 18f));   // arms rear back to cast
                    if (_orb != null)
                    {
                        float g = 1f + _cast * 1.2f + Mathf.Sin(_phase * 3f) * 0.06f;
                        _orb.Scale = Vector3.One * g;
                        _orb.Position = new Vector3(0, _scale * (0.55f + _cast * 0.3f), _scale * (0.55f + _cast * 0.1f));
                        _orb.RotateY(dt * 4f);
                    }
                }
                break;

            case CreatureKind.Mosquito:
                {
                    _wing += dt * 46f;
                    float flap = Mathf.Sin(_wing) * 55f;
                    for (int i = 0; i < _wings.Count; i++)
                        _wings[i].RotationDegrees = new Vector3(0, 0, (i == 0 ? 1 : -1) * flap);
                    _phase += dt * (2f + move * 3f);
                    for (int i = 0; i < _hips.Count; i++)
                    {
                        var b = _hipBase[i];
                        _hips[i].RotationDegrees = new Vector3(Mathf.Sin(_phase + i) * 6f, b.Y, b.Z + Mathf.Sin(_phase + i) * 4f);
                    }
                    if (_body != null) _body.Position = new Vector3(0, Mathf.Sin(_phase * 0.7f) * _scale * 0.12f, 0);
                }
                break;

            case CreatureKind.Pterodactyl:
            case CreatureKind.Bat:
                {
                    _cast = Mathf.MoveToward(_cast, _castTarget, dt * 3f);
                    bool charging = _kind == CreatureKind.Pterodactyl && _cast > 0.02f;   // ptero winding up its stun bolt
                    _wing += dt * (_kind == CreatureKind.Bat ? 22f : 13f) * (charging ? 2.4f : 1f);   // beat frantically while charging
                    float flap = Mathf.Sin(_wing) * (_kind == CreatureKind.Bat ? 46f : 40f);
                    for (int i = 0; i < _wings.Count; i++)
                        _wings[i].RotationDegrees = new Vector3(0, 0, (i == 0 ? 1 : -1) * (flap + (charging ? 35f * _cast : 0f)));   // wings sweep UP as it winds up
                    _phase += dt * 2f;
                    if (_body != null)
                    {
                        _body.RotationDegrees = new Vector3(charging ? -50f * _cast : 0f, 0f, 0f);                                   // rear back to cast — clear tell
                        _body.Position = new Vector3(0, Mathf.Sin(_phase) * _scale * 0.1f + (charging ? _cast * _scale * 0.35f : 0f), 0);   // rise as it charges
                        _body.Scale = Vector3.One * (charging ? 1f + Mathf.Sin(_phase * 11f) * 0.07f * _cast : 1f);                  // electric shudder
                    }
                }
                break;

            case CreatureKind.Snake:
                {
                    _phase += dt * (2f + move * 6f);
                    for (int i = 0; i < _hips.Count; i++)   // a travelling S-wave yaws each segment → slither
                        _hips[i].RotationDegrees = new Vector3(0, Mathf.Sin(_phase - i * 0.8f) * (7f + move * 15f), 0);
                }
                break;
        }
    }
}

