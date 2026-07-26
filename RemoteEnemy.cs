using Godot;

// Shared host/client mapping of enemy type -> wire index and a representative look.
// Keep this list and order identical to how SpawnEnemy assigns TypeIdx.
// RemoteEnemy.cs — the CLIENT-SIDE ENEMY PROXY TABLE. EnemyKinds maps each enemy type string <-> an
// index <-> a render color, so the host can send a compact type index in EnemySnapshot and clients
// can build the matching Enemy proxy. CRITICAL: every spawnable enemy type string MUST have an entry
// in the Types table (and a Col(idx) case) or it will work on the host but be invisible/crash on
// clients. Add your new type here when adding an enemy (DEV_GUIDE.md §6.1, step 4).
public static class EnemyKinds
{
    public static readonly string[] Types =
        { "shade", "wisp", "brute", "caster", "flyer", "sieger", "healer", "zapper", "bomber", "goblin", "miniboss", "boss",
          "sentinel", "diver", "hexer", "splitter", "totem", "spawnling", "wardbane", "swarmer", "taker",
          "jtroll", "pigmy", "pigmydart", "ptero", "bat", "croc", "snake",   // (NEW) Rainforest jungle enemies, indices 21..27
          "phalanx", "archer" };   // (NEW) the Warded Phalanx compound miniboss, indices 28..29 — APPEND ONLY; these indices go over the wire

    public static int Index(string t)
    {
        int i = System.Array.IndexOf(Types, t);
        return i < 0 ? 0 : i;
    }

    public static Color Col(int idx) => idx switch
    {
        1 => new Color(0.50f, 0.82f, 1.0f),
        2 => new Color(0.78f, 0.12f, 0.18f),
        3 => DamageTypes.Col(DamageType.Arcane),
        4 => new Color(0.8f, 0.85f, 1f),
        5 => DamageTypes.Col(DamageType.Ember),
        6 => DamageTypes.Col(DamageType.Holy),
        7 => new Color(0.55f, 0.8f, 1f),
        8 => new Color(1f, 0.45f, 0.18f),
        9 => new Color(1f, 0.84f, 0.3f),
        10 => new Color(0.62f, 0.30f, 0.85f),
        11 => new Color(0.85f, 0.25f, 0.45f),
        12 => new Color(0.6f, 0.62f, 0.7f),     // sentinel
        13 => new Color(0.9f, 0.6f, 1f),         // diver
        14 => DamageTypes.Col(DamageType.Curse), // hexer
        15 => new Color(0.5f, 0.85f, 0.4f),      // splitter
        16 => new Color(1f, 0.8f, 0.35f),        // totem
        17 => new Color(0.5f, 0.85f, 0.4f),      // spawnling
        18 => new Color(0.6f, 0.3f, 0.85f),      // wardbane
        21 => new Color(0.28f, 0.42f, 0.24f),    // jungle troll
        22 => new Color(0.75f, 0.6f, 0.35f),     // pigmy
        23 => new Color(0.7f, 0.55f, 0.3f),      // pigmy dartblower
        24 => new Color(0.55f, 0.75f, 0.85f),    // pteradactyl
        25 => new Color(0.3f, 0.24f, 0.3f),      // bat
        26 => new Color(0.4f, 0.55f, 0.3f),      // crocodile bomber
        27 => new Color(0.5f, 0.8f, 0.35f),      // snake
        28 => new Color(0.55f, 0.42f, 0.95f),    // phalanx ward-bearer
        29 => new Color(0.70f, 0.58f, 1.0f),     // phalanx archer
        _ => new Color(0.54f, 0.47f, 0.84f),
    };

    public static float Radius(int idx) => idx switch
    {
        1 => 0.9f, 2 => 2.2f, 3 => 1.0f, 4 => 0.75f, 5 => 2.0f, 6 => 1.1f,
        7 => 1.0f, 8 => 0.85f, 9 => 1.0f, 10 => 3.0f, 11 => 4.0f,
        21 => 2.2f, 22 => 0.8f, 23 => 0.85f, 24 => 1.0f, 25 => 0.7f, 26 => 1.6f, 27 => 0.7f,
        28 => 2.8f, 29 => 1.05f,
        _ => 1.3f,
    };

    public static string Label(int idx) => idx switch
    {
        9 => "LOOT GOBLIN", 10 => "MINI-BOSS", 11 => "THE HOLLOW MOON",
        28 => "WARD BEARER", 29 => "PHALANX ARCHER", _ => "",
    };
}

// A client-side visual stand-in for a host-owned enemy. Position is fed by the host snapshot;
// it interpolates between updates. Clients cannot damage these directly yet (that's the next sub-step).
public partial class RemoteEnemy : Node3D
{
    private Vector3 _target;
    private bool _have = false;
    public int TypeIdx;

    public void Setup(int typeIdx)
    {
        TypeIdx = typeIdx;
        float rad = EnemyKinds.Radius(typeIdx);
        var col = EnemyKinds.Col(typeIdx);
        var body = new MeshInstance3D { Mesh = new SphereMesh { Radius = rad, Height = rad * 2f } };
        var mat = new StandardMaterial3D {
            AlbedoColor = col,
            EmissionEnabled = true, Emission = col, EmissionEnergyMultiplier = 0.5f
        };
        body.MaterialOverride = mat;
        AddChild(body);

        string lbl = EnemyKinds.Label(typeIdx);
        if (lbl != "")
        {
            var tag = new Label3D {
                Text = lbl, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                Modulate = col, OutlineModulate = new Color(0, 0, 0, 0.8f), OutlineSize = 6,
                FontSize = 30, PixelSize = 0.012f, NoDepthTest = true
            };
            tag.Position = new Vector3(0, rad + 1.4f, 0);
            AddChild(tag);
        }
    }

    public void SetTarget(Vector3 pos)
    {
        _target = pos;
        if (!_have) { GlobalPosition = pos; _have = true; }
    }

    public void Tick(float dt)
    {
        if (_have) GlobalPosition = GlobalPosition.Lerp(_target, Mathf.Clamp(dt * 16f, 0f, 1f));
    }
}
