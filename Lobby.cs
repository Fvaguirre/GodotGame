using Godot;

// Simple pre-game lobby (LAN, no password). Solo plays as before; Host starts a server and enters;
// Join connects to a host by IP and enters. The host gets a popup when someone connects.
// Lobby.cs — the pre-game lobby UI and host/join flow (enter IP, start). Hands off to Game once the run begins.
public partial class Lobby : Control
{
    private LineEdit _ip;
    private Label _status;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;

        var bg = new ColorRect { Color = new Color(0.03f, 0.03f, 0.06f, 0.96f) };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var box = new VBoxContainer();
        box.SetAnchorsPreset(LayoutPreset.Center);
        box.Position = new Vector2(-170, -150);
        box.CustomMinimumSize = new Vector2(340, 0);
        box.AddThemeConstantOverride("separation", 12);
        AddChild(box);

        var title = new Label { Text = "WARDENS OF THE MOONLIT GROVE", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 22);
        box.AddChild(title);

        var sub = new Label { Text = "co-op is LAN only \u2014 one player hosts, the other joins", HorizontalAlignment = HorizontalAlignment.Center };
        sub.AddThemeFontSizeOverride("font_size", 12);
        sub.Modulate = new Color(0.7f, 0.7f, 0.8f);
        box.AddChild(sub);

        box.AddChild(new HSeparator());

        var solo = new Button { Text = "Play Solo", CustomMinimumSize = new Vector2(0, 40) };
        solo.Pressed += () => { Hide(); Game.I.LobbySolo(); };
        box.AddChild(solo);

        var host = new Button { Text = "Host & Play", CustomMinimumSize = new Vector2(0, 40) };
        host.Pressed += () => { Hide(); Game.I.LobbyHost(); };
        box.AddChild(host);

        box.AddChild(new HSeparator());

        var joinLbl = new Label { Text = "Join a host on your network:" };
        joinLbl.AddThemeFontSizeOverride("font_size", 12);
        box.AddChild(joinLbl);

        _ip = new LineEdit { PlaceholderText = "host IP (e.g. 192.168.1.42)", Text = "" };
        box.AddChild(_ip);

        var join = new Button { Text = "Join & Play", CustomMinimumSize = new Vector2(0, 40) };
        join.Pressed += () => { Hide(); Game.I.LobbyJoin(_ip.Text); };
        box.AddChild(join);

        _status = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Center };
        _status.AddThemeFontSizeOverride("font_size", 12);
        _status.Modulate = new Color(0.8f, 0.9f, 1f);
        box.AddChild(_status);
    }
}
