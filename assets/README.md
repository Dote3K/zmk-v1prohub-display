# Assets

The pets on the Claude Code screen.

- `clawd-pet/svg/` — animated SVGs from [clawd-pet](https://github.com/abderrahimghazali/clawd-pet)
  (commit `b208f0c`), MIT licensed; see `clawd-pet/LICENSE`.
- `pets/` — sprite sheets (`<name>.png` + `<name>.json`) rendered from those SVGs by
  `tools/clawd-frames/render.mjs`; `preview.html` plays them all.

Clawd, the character the pets draw, is Claude Code's mascot and belongs to Anthropic, PBC. This is
an unofficial fan project, not affiliated with or endorsed by Anthropic.

To add or redo a pet:

```
cd tools/clawd-frames
npm install
node render.mjs fetch <name>
node render.mjs render <name>
```
