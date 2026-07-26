using Godot;

// Gamepad menu reticle. Lives on its own high CanvasLayer (Layer 100) so it draws OVER both the HUD (layer 0) and the
// Lobby / CharSelect / Perk Control screens (layer 50). This is our own cursor — it shows even if Parsec hides or pins
// the real OS cursor. Position + visibility come from Game (Game.PadCursor / Game.PadCursorShown).
public partial class PadCursor : Control
{
    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;          // never eats clicks
        SetAnchorsPreset(LayoutPreset.FullRect);
    }

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        var g = Game.I;
        if (g == null || !g.PadCursorShown) return;
        var pc = g.PadCursor;
        float u = Mathf.Max(1f, GetViewportRect().Size.Y / 720f);
        float r = 9f * u;
        DrawCircle(pc, r + 2f * u, new Color(0, 0, 0, 0.7f));                 // dark halo for contrast on any background
        DrawCircle(pc, r, new Color(1f, 0.95f, 0.8f, 0.95f));                 // warm cream dot
        DrawCircle(pc, r * 0.42f, new Color(0.15f, 0.12f, 0.1f, 0.95f));      // center pip
    }
}
