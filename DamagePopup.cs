using Godot;

// A floating damage number, billboarded in world space, colored by damage type.
// DamagePopup.cs — floating damage number. Init(amount,col,at,crit,heal,amp) styles it (gold 'CRIT!' for crits, larger for hex-amped hits).
public partial class DamagePopup : Label3D
{
    private float _t = 0f;
    private const float Life = 0.85f;
    private Vector3 _vel;
    private Color _col = Colors.White;
    private bool _crit = false;

    public void Init(float amount, Color col, Vector3 at, bool crit = false, bool heal = false, bool amp = false)
    {
        int n = Mathf.RoundToInt(Mathf.Max(1f, amount));
        _crit = crit;
        Text = crit ? $"{n}  CRIT!" : (heal ? "+" : "") + n.ToString();
        if (crit) col = new Color(1f, 0.84f, 0.32f);   // gold
        _col = col;
        Modulate = col;
        OutlineModulate = new Color(0f, 0f, 0f, 0.85f);
        OutlineSize = crit ? 11 : 8;
        FontSize = crit ? 66 : (amp ? 58 : 44);   // crit loudest; marked/amped a bit bigger
        PixelSize = 0.012f;
        Billboard = BaseMaterial3D.BillboardModeEnum.Enabled;
        NoDepthTest = true;
        Shaded = false;
        if (crit) Scale = Vector3.One * 1.8f;   // punch in, settles in _Process
        var rng = new RandomNumberGenerator(); rng.Randomize();
        GlobalPosition = at + new Vector3(rng.RandfRange(-0.5f, 0.5f), 1.7f, rng.RandfRange(-0.5f, 0.5f));
        _vel = new Vector3(rng.RandfRange(-0.6f, 0.6f), crit ? 3.2f : 2.6f, rng.RandfRange(-0.6f, 0.6f));
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _t += dt;
        _vel.Y -= 4.5f * dt;
        GlobalPosition += _vel * dt;
        if (_crit) Scale = Scale.Lerp(Vector3.One, Mathf.Clamp(dt * 13f, 0f, 1f));   // scale-punch settle
        float a = Mathf.Clamp(1f - _t / Life, 0f, 1f);
        Modulate = new Color(_col.R, _col.G, _col.B, a);
        if (_t >= Life) QueueFree();
    }
}
