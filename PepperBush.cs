using Godot;

// PepperBush.cs — the Rainforest's smashable prop (the jungle's answer to the Grove's pumpkin). Inherits Pumpkin so it reuses
// the ENTIRE smash/damage/network/loot contract (Hp, TakeDamage, Smash, BroadcastSmashPumpkin, RollDrop) for free — only the
// visual differs: a leafy green bush hung with glowing red peppers. All the world-damage (DamageWorld/SmashNear) callers hit
// it automatically since it lives in Game.Smashables just like a pumpkin.
public partial class PepperBush : Pumpkin
{
    protected override void BuildVisual(bool lit, RandomNumberGenerator rng)
    {
        _col = new Color(0.22f, 0.5f, 0.16f);   // leafy green — the "pulp" color the smash shards use

        _body = new MeshInstance3D { Mesh = new SphereMesh { Radius = _size, Height = _size } };
        _body.MaterialOverride = Game.Toon(new Color(0.16f, 0.42f, 0.14f), 0.9f, 0.28f, 0.03f);
        _body.Position = new Vector3(0, _size * 0.6f, 0);
        _body.Scale = new Vector3(1.15f, 0.85f, 1.15f);
        AddChild(_body);

        // extra leaf clumps + hanging peppers — all parented to _body so the smash (which hides _body) clears them too
        for (int i = 0; i < 3; i++)
        {
            var leaf = new MeshInstance3D { Mesh = new SphereMesh { Radius = _size * 0.62f, Height = _size * 0.62f }, MaterialOverride = _body.MaterialOverride };
            leaf.Position = new Vector3((rng.Randf() - 0.5f) * _size, _size * (0.2f + rng.Randf() * 0.5f), (rng.Randf() - 0.5f) * _size);
            _body.AddChild(leaf);
        }
        int peppers = 3 + rng.RandiRange(0, 3);
        for (int i = 0; i < peppers; i++)
        {
            var pep = new MeshInstance3D { Mesh = new SphereMesh { Radius = _size * 0.15f, Height = _size * 0.44f } };
            pep.MaterialOverride = Game.ToonEmissive(new Color(0.9f, 0.16f, 0.08f), 0.6f);
            pep.Scale = new Vector3(1f, 1.7f, 1f);
            pep.Position = new Vector3((rng.Randf() - 0.5f) * _size * 1.5f, _size * (0.1f + rng.Randf() * 0.4f), (rng.Randf() - 0.5f) * _size * 1.5f);
            _body.AddChild(pep);
        }
        // no stem (_stem stays null; Smash guards for it)
    }
}
