using Godot;
using System.Collections.Generic;

// UltCastOverlay.cs — when an ALLY casts their ultimate, a stylized "cutout" flashes in on the right edge of the
// screen for ~2s: that witch's pose image (from res://assets/witch_poses/<witch>.png), framed in an arcane-shard
// mask edged in her element colour, with element particle effects drifting over it (snow for Frost, embers for
// Ember, blood for Crimson, …) and the ult's name stylized across the top. Several at once stack + fan-tilt.
//
// Triggered from Net.ReceiveUltCast (an ally ulted; CallLocal=false so you never see your own), from
// Player.ActivateUlt when the solo-test toggle is on (dev), and from the `ultwindow` dev command (Preview).
public partial class UltCastOverlay : CanvasLayer
{
    private const int MaxWindows = 4;
    private const float WindowLife = 2.0f;   // it's an image now → a quick flash, not a lingering cinematic

    private static readonly string[] WitchNames = { "lunar", "divine", "crimson", "verdant", "gale", "frost", "forsaken", "ember", "arcane" };

    private class Win
    {
        public Control Root;
        public object Owner;   // RemoteAvatar or Player — for dedup / refresh
        public bool IsSelf;
        public Color Col;
        public float T, Anim;
    }

    private readonly List<Win> _wins = new();
    public bool SoloTest = false;   // dev: also flash a window for YOUR own ults in single player
    private Texture2D _dot;         // shared soft-dot particle texture

    // canvas_item shader for the cutout: a chamfered "arcane shard" mask + vignette, a glowing element-colour rim,
    // faint scanlines + a slow sheen sweep. edge_col = the caster's element colour. Composites the pose image over
    // a moody backdrop so a transparent-background cutout still reads.
    private const string FrameShader = @"
shader_type canvas_item;
uniform vec4 edge_col : source_color = vec4(1.0, 0.9, 0.6, 1.0);
float tri(float t) { return abs(fract(t) - 0.5) * 2.0; }   // 0..1 triangle wave → jagged teeth
void fragment() {
    vec2 uv = UV;
    vec4 tex = texture(TEXTURE, uv);
    vec2 q = uv - 0.5;
    q.x += q.y * 0.20;                        // slant/lean → a dynamic, non-square panel
    vec2 pa = abs(q) * 2.0;                   // 0..1 to the (sheared) edges
    float jY = 0.12 * tri(uv.x * 5.0 + 0.3);  // top/bottom jagged teeth (vary along x)
    float jX = 0.12 * tri(uv.y * 3.0);        // left/right jagged teeth (vary along y)
    float ex = pa.x + jX;
    float ey = pa.y + jY;
    float m = max(ex, ey);
    float diag = ex + ey;                      // aggressively SLASHED corners
    float shape = smoothstep(1.0, 0.95, m) * smoothstep(1.78, 1.66, diag);
    float vig = smoothstep(1.35, 0.30, length(pa));
    float rim = clamp(smoothstep(0.84, 0.98, m) + smoothstep(1.50, 1.66, diag), 0.0, 1.0) * shape;
    vec3 bg = mix(vec3(0.03, 0.02, 0.06), edge_col.rgb * 0.16, vig);
    vec3 col = mix(bg, tex.rgb, tex.a);
    col = mix(col, edge_col.rgb, 0.05);
    col = mix(col, edge_col.rgb * 1.6, rim * 0.95);            // glowing jagged element-colour rim
    float sheen = smoothstep(0.0, 0.5, 1.0 - abs(fract(uv.x - TIME * 0.12) - 0.5) * 2.0) * 0.05;   // a diagonal light sweep across the wide panel
    col += edge_col.rgb * sheen;
    COLOR = vec4(col, shape);
}";

    public override void _Ready()
    {
        Layer = 3;
        // a soft round dot for the particles (radial gradient → transparent edge)
        var g = new Gradient();
        g.SetColor(0, new Color(1, 1, 1, 1));
        g.SetColor(1, new Color(1, 1, 1, 0));
        _dot = new GradientTexture2D { Gradient = g, Width = 16, Height = 16, Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(1f, 0.5f) };
    }

    // ---- triggers ----
    public void Trigger(RemoteAvatar av, Player.UltKind ult)
    {
        if (av == null || !GodotObject.IsInstanceValid(av)) return;
        Spawn(av, false, av.WitchIdx, av.WitchCol, $"WARDEN {av.Slot + 2}", ult);
    }
    public void TriggerLocal(Player p, Player.UltKind ult)
    {
        if (p == null || !GodotObject.IsInstanceValid(p)) return;
        Spawn(p, true, p.WitchIndex, WitchModel.WitchColor(Mathf.Max(0, p.WitchIndex)), "WARDEN 1", ult);
    }
    public void Preview(int witchIdx, Player.UltKind ult)
    {
        Spawn(null, false, witchIdx, WitchModel.WitchColor(Mathf.Max(0, witchIdx)), "PREVIEW", ult);
    }

    public void EnableSolo(bool on)
    {
        SoloTest = on;
        if (!on) for (int i = _wins.Count - 1; i >= 0; i--) if (_wins[i].IsSelf) _wins[i].T = 0f;
    }

    private void Spawn(object owner, bool isSelf, int witchIdx, Color col, string name, Player.UltKind ult)
    {
        if (Game.I == null) return;
        witchIdx = Mathf.Clamp(witchIdx, 0, WitchNames.Length - 1);
        if (owner != null)   // already flashing this caster? refresh + retitle
            foreach (var w in _wins) if (w.Owner == owner) { w.T = WindowLife; w.Anim = Mathf.Min(w.Anim, 0.6f); RetitleWindow(w, ult); return; }
        if (_wins.Count >= MaxWindows) CloseWindow(_wins[0]);

        var win = new Win { Owner = owner, IsSelf = isSelf, Col = col, T = WindowLife };
        BuildPanel(win, witchIdx, col, name, ult);
        _wins.Add(win);
    }

    private void BuildPanel(Win w, int witchIdx, Color col, string name, Player.UltKind ult)
    {
        Vector2 vp = GetViewport().GetVisibleRect().Size;
        float u = Mathf.Max(0.5f, vp.Y / 720f);
        float W = 256f * u, H = 144f * u;   // WIDE (landscape) action-panel — ~half the old area

        w.Root = new Control { Size = new Vector2(W, H), PivotOffset = new Vector2(W * 0.5f, H * 0.5f), ClipContents = true };
        AddChild(w.Root);

        // the pose image (or a dark element-tinted placeholder), shaped + rimmed by the shard shader
        var tex = LoadPose(WitchNames[witchIdx]) ?? PlaceholderTex(col);
        var img = new TextureRect
        {
            Texture = tex,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            Size = new Vector2(W, H),
        };
        var sm = new ShaderMaterial { Shader = new Shader { Code = FrameShader } };
        sm.SetShaderParameter("edge_col", new Color(col.R, col.G, col.B, 1f));
        img.Material = sm;
        w.Root.AddChild(img);

        // element particles drifting over the cutout
        var fx = new CpuParticles2D { Texture = _dot, Position = new Vector2(W * 0.5f, H * 0.5f), Emitting = true };
        ConfigureFx(fx, DamageForWitch(witchIdx), W, H, u);
        w.Root.AddChild(fx);

        // a bright element-colour flash on appear
        var flash = new ColorRect { Color = new Color(col.R, col.G, col.B, 0.55f), Size = new Vector2(W, H) };
        w.Root.AddChild(flash);
        flash.CreateTween().TweenProperty(flash, "modulate:a", 0f, 0.35f).SetEase(Tween.EaseType.Out);

        // ult name — stylized, across the TOP; warden name tucked at the BOTTOM
        var ultl = new Label { Text = Hud.UltName(ult).ToUpper(), Position = new Vector2(0, 7f * u), Size = new Vector2(W, 24f * u), HorizontalAlignment = HorizontalAlignment.Center };
        StyleLabel(ultl, Mathf.RoundToInt(17f * u), col.Lerp(Colors.White, 0.55f), Mathf.RoundToInt(5f * u));
        w.Root.AddChild(ultl);
        var div = new ColorRect { Color = new Color(col.R, col.G, col.B, 0.9f), Position = new Vector2(W * 0.5f - 30f * u, 28f * u), Size = new Vector2(60f * u, 2f * u) };
        w.Root.AddChild(div);
        var nm = new Label { Text = name, Position = new Vector2(0, H - 20f * u), Size = new Vector2(W, 16f * u), HorizontalAlignment = HorizontalAlignment.Center };
        StyleLabel(nm, Mathf.RoundToInt(10f * u), new Color(0.9f, 0.93f, 1f, 0.85f), Mathf.RoundToInt(4f * u));
        w.Root.AddChild(nm);

        w.Root.SetMeta("ult_lbl", ultl);
    }

    // per-element particle look (colour + motion): snow falls, embers rise, blood rains, wind streaks, etc.
    private void ConfigureFx(CpuParticles2D p, DamageType dt, float W, float H, float u)
    {
        p.EmissionShape = CpuParticles2D.EmissionShapeEnum.Rectangle;
        p.EmissionRectExtents = new Vector2(W * 0.5f, H * 0.5f);
        p.Preprocess = 1.0;
        Color c; Vector2 grav, dir; float vMin, vMax, sMin, sMax, life, spread; int amt;
        switch (dt)
        {
            case DamageType.Frost:  c = new Color(0.85f, 0.94f, 1f); grav = new Vector2(0, 30); dir = Vector2.Down; vMin = 4; vMax = 14; sMin = 1.4f; sMax = 3.2f; life = 3.4f; spread = 25; amt = 30; break;
            case DamageType.Ember:  c = new Color(1f, 0.62f, 0.24f); grav = new Vector2(0, -48); dir = Vector2.Up; vMin = 8; vMax = 22; sMin = 1.2f; sMax = 2.6f; life = 1.8f; spread = 30; amt = 34; break;
            case DamageType.Blood:  c = new Color(0.78f, 0.09f, 0.13f); grav = new Vector2(0, 78); dir = Vector2.Down; vMin = 10; vMax = 26; sMin = 1.1f; sMax = 2.4f; life = 1.5f; spread = 12; amt = 20; break;
            case DamageType.Nature: c = new Color(0.4f, 0.9f, 0.55f); grav = new Vector2(0, 18); dir = Vector2.Down; vMin = 4; vMax = 12; sMin = 1.5f; sMax = 3.0f; life = 3.6f; spread = 45; amt = 22; break;
            case DamageType.Wind:   c = new Color(0.72f, 0.97f, 0.9f); grav = Vector2.Zero; dir = Vector2.Right; vMin = 30; vMax = 70; sMin = 1.0f; sMax = 2.4f; life = 1.5f; spread = 25; amt = 26; break;
            case DamageType.Curse:  c = new Color(0.82f, 0.4f, 0.9f); grav = new Vector2(0, -26); dir = Vector2.Up; vMin = 4; vMax = 12; sMin = 1.4f; sMax = 3.0f; life = 2.8f; spread = 40; amt = 20; break;
            case DamageType.Holy:   c = new Color(1f, 0.94f, 0.72f); grav = new Vector2(0, -12); dir = Vector2.Up; vMin = 3; vMax = 9; sMin = 1.5f; sMax = 3.2f; life = 3.4f; spread = 35; amt = 22; break;
            case DamageType.Arcane: c = new Color(0.6f, 0.32f, 1f); grav = Vector2.Zero; dir = Vector2.Up; vMin = 14; vMax = 42; sMin = 1.0f; sMax = 2.2f; life = 1.6f; spread = 180; amt = 26; break;
            default:                c = new Color(0.9f, 0.9f, 1f); grav = new Vector2(0, 9); dir = Vector2.Down; vMin = 3; vMax = 8; sMin = 1.4f; sMax = 3.0f; life = 4.0f; spread = 30; amt = 16; break;   // Lunar
        }
        p.Amount = amt;
        p.Lifetime = life;
        p.Gravity = grav * u;
        p.Direction = dir;
        p.Spread = spread;
        p.InitialVelocityMin = vMin * u;
        p.InitialVelocityMax = vMax * u;
        p.ScaleAmountMin = sMin * u;
        p.ScaleAmountMax = sMax * u;
        p.Color = new Color(c.R, c.G, c.B, 0.9f);
        // fade the particles out over their life so they twinkle rather than pop
        var ramp = new Gradient();
        ramp.SetColor(0, new Color(c.R, c.G, c.B, 0f));
        ramp.SetColor(1, new Color(c.R, c.G, c.B, 0f));
        ramp.AddPoint(0.2f, new Color(c.R, c.G, c.B, 1f));
        ramp.AddPoint(0.7f, new Color(c.R, c.G, c.B, 0.9f));
        p.ColorRamp = ramp;
    }

    private static DamageType DamageForWitch(int idx) => idx switch
    {
        1 => DamageType.Holy, 2 => DamageType.Blood, 3 => DamageType.Nature, 4 => DamageType.Wind,
        5 => DamageType.Frost, 6 => DamageType.Curse, 7 => DamageType.Ember, 8 => DamageType.Arcane,
        _ => DamageType.Lunar,
    };

    private static Texture2D LoadPose(string name)
    {
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".webp" })
        {
            string res = $"res://assets/witch_poses/{name}{ext}";
            if (ResourceLoader.Exists(res)) { var t = ResourceLoader.Load<Texture2D>(res); if (t != null) return t; }
        }
        // dev fallback: load straight off disk even if Godot hasn't imported the file yet
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg" })
        {
            string abs = ProjectSettings.GlobalizePath($"res://assets/witch_poses/{name}{ext}");
            if (FileAccess.FileExists(abs)) { var img = Image.LoadFromFile(abs); if (img != null) return ImageTexture.CreateFromImage(img); }
        }
        return null;
    }

    private static Texture2D PlaceholderTex(Color col)
    {
        var g = new Gradient();
        g.SetColor(0, new Color(0.06f, 0.05f, 0.09f));
        g.SetColor(1, new Color(col.R * 0.28f, col.G * 0.28f, col.B * 0.28f));
        return new GradientTexture2D { Gradient = g, Width = 64, Height = 80, FillFrom = new Vector2(0.5f, 0f), FillTo = new Vector2(0.5f, 1f) };
    }

    private static void StyleLabel(Label l, int size, Color col, int outline)
    {
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", col);
        l.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
        l.AddThemeConstantOverride("outline_size", outline);
    }

    private void RetitleWindow(Win w, Player.UltKind ult)
    {
        if (w.Root != null && w.Root.HasMeta("ult_lbl") && w.Root.GetMeta("ult_lbl").As<Label>() is Label l && GodotObject.IsInstanceValid(l))
            l.Text = Hud.UltName(ult).ToUpper();
    }

    public override void _Process(double delta)
    {
        if (_wins.Count == 0) return;
        float dt = (float)delta;
        Vector2 vp = GetViewport().GetVisibleRect().Size;
        float u = Mathf.Max(0.5f, vp.Y / 720f);
        float W = 256f * u, H = 144f * u, gap = 12f * u, margin = 18f * u;

        float totalH = _wins.Count * H + (_wins.Count - 1) * gap;
        float startY = Mathf.Max(24f * u, vp.Y * 0.5f - totalH * 0.5f);

        for (int i = _wins.Count - 1; i >= 0; i--)
        {
            var w = _wins[i];
            if (w.Owner != null && w.Owner is GodotObject go && !GodotObject.IsInstanceValid(go)) w.T = 0f;

            w.T -= dt;
            float target = w.T > 0f ? 1f : 0f;
            w.Anim = Mathf.MoveToward(w.Anim, target, dt / (target > w.Anim ? 0.28f : 0.35f));
            if (w.T <= 0f && w.Anim <= 0.001f) { CloseWindow(w); continue; }

            float ease = Mathf.SmoothStep(0f, 1f, w.Anim);
            float targetX = vp.X - W - margin;
            float slide = (1f - ease) * (W + margin + 60f * u);
            float ty = startY + i * (H + gap);
            if (w.Root != null && GodotObject.IsInstanceValid(w.Root))
            {
                w.Root.Position = new Vector2(targetX + slide, ty);
                float tilt = (_wins.Count > 1 ? (i - (_wins.Count - 1) * 0.5f) * 3.5f : 0f);
                w.Root.RotationDegrees = tilt * ease;
                w.Root.Modulate = new Color(1f, 1f, 1f, ease);
                w.Root.Scale = new Vector2(0.9f + 0.1f * ease, 0.9f + 0.1f * ease);
            }
        }
    }

    private void CloseWindow(Win w)
    {
        _wins.Remove(w);
        if (w.Root != null && GodotObject.IsInstanceValid(w.Root)) w.Root.QueueFree();
    }
}
