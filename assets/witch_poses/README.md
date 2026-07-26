# Witch ult-cast pose images

Drop one image per witch here. When an ally casts their ultimate, the ult-cast window
(`UltCastOverlay.cs`) shows that witch's pose image, framed in her element colour with
element particle effects, for ~2 seconds.

## Naming (exact, lowercase)

| File | Witch | Element colour |
|------|-------|----------------|
| `lunar.png`    | Lunar    | pale moon-white |
| `divine.png`   | Divine   | warm gold |
| `crimson.png`  | Crimson  | crimson red |
| `verdant.png`  | Verdant  | verdant green |
| `gale.png`     | Gale     | mint-cyan |
| `frost.png`    | Frost    | icy blue |
| `forsaken.png` | Forsaken | violet-magenta |
| `ember.png`    | Ember    | amber-gold |
| `arcane.png`   | Arcane   | deep violet |

## Notes
- `.png`, `.jpg`, `.jpeg`, and `.webp` are all accepted (png preferred).
- The window is a WIDE, jagged action-anime panel (landscape, ~16:9). Author art
  landscape (~1.8:1). Images use "keep-aspect-covered" so they fill the frame
  (edges may crop — keep the witch centred and give some margin).
- A dramatic, transparent-background cutout of the witch mid-cast reads best; the
  shader slashes the edges into jagged teeth and slanted corners, so don't put
  important detail right at the border.
- If an image is missing, the window falls back to a dark element-tinted placeholder
  so the frame + particles + name still show.
- Images are loaded at runtime; if Godot hasn't imported a newly-added file yet, the
  code also loads it straight off disk, so it should appear without a re-import.
