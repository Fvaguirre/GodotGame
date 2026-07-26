# Model Direction

We need a production-ready [enemy/prop] for a stylized fantasy action game.

Do not build the final model from Godot primitives.

First inspect the existing asset pipeline and tell me:
1. Whether this should be authored in Blender, procedurally generated, or assembled
   from existing modular pieces.
2. Required mesh parts.
3. Target triangle counts for LOD0, LOD1, and LOD2.
4. Material slots and texture channels.
5. Skeleton and socket requirements.
6. Required animations.
7. Godot import settings.
8. Runtime performance risks.

Then create any Blender automation, Godot import configuration, inherited scenes,
materials, sockets, and validation tools needed.

Do not fake completion if an authored mesh is still required. Leave a clearly
named asset placeholder and generate an exact asset brief.
