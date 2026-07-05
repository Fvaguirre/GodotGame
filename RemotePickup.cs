using Godot;

// Client-side container for a host-owned pickup. It renders the REAL Orb/Chest model (in remote mode)
// so allies see the exact same look (orb tint, chest model + light beam), and lerps to the synced position.
// The host handles actual collection/opening for whichever player is nearest, so these are visual only.
// RemotePickup.cs — the client-side proxy for host-owned pickups (so loot appears for everyone). Synced via Net.
public partial class RemotePickup : Node3D
{
    private Vector3 _target;
    private bool _have = false;
    public int Kind;

    public void Setup(int kind, Color color)
    {
        Kind = kind;
        if (kind == 0)
        {
            var orb = new Orb { Tint = color, Remote = true };
            AddChild(orb);   // Orb._Ready builds the real model using Tint
        }
        else
        {
            var chest = new Chest { Remote = true };
            AddChild(chest); // Chest._Ready builds the real model + light beam; Remote stops it opening
        }
    }

    public void SetTarget(Vector3 pos) { _target = pos; if (!_have) { GlobalPosition = pos; _have = true; } }

    public void Tick(float dt)
    {
        if (!_have) return;
        GlobalPosition = GlobalPosition.Lerp(_target, Mathf.Clamp(dt * 16f, 0f, 1f));
    }
}
