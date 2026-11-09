// Turns clawd-pet's CSS-animated SVGs (github.com/abderrahimghazali/clawd-pet, MIT) into sprite sheets
// the hub app can play: one PNG grid of frames plus a JSON manifest per pet.
//
//   node render.mjs fetch working-typing notification sleeping   -> assets/clawd-pet/svg/<name>.svg
//   node render.mjs render [names...] [--max 150] [--scale n] [--fps 15] [--max-loop 8000] [--crisp] [--keep-bg]
//                                                                  -> assets/pets/<name>.png + .json
//
// --max      largest frame side in px; the scale is the biggest whole number that fits (--scale forces one)
// --crisp    no anti-aliasing: rotated arms turn into stair steps, like real pixel art
// --keep-bg  keep shapes that cover the whole picture (e.g. the red flash in "error")
//
// Frames are not screen-recorded: every animation is paused and seeked to the exact frame time, so the
// output is identical on any machine and the last frame flows into the first.
// Uses the installed Edge (or Chrome) through playwright-core, so no browser download is needed.
// The character belongs to Anthropic; see assets/README.md for the credits that go with the files.

import { mkdir, readFile, readdir, writeFile } from "node:fs/promises";
import { dirname, join, basename } from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright-core";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const svgDir = join(root, "assets", "clawd-pet", "svg");
const outDir = join(root, "assets", "pets");

// pinned so a re-fetch gives the same art
const commit = "b208f0c04a4084a17f4e5f5adf5198a752be0b36";
const source = (name) =>
  `https://raw.githubusercontent.com/abderrahimghazali/clawd-pet/${commit}/public/pets/clawd-${name}.svg`;

const [command, ...rest] = process.argv.slice(2);
const flags = {};
const names = [];
for (let i = 0; i < rest.length; i++) {
  if (rest[i] === "--crisp") flags.crisp = true;
  else if (rest[i] === "--keep-bg") flags.keepBg = true;
  else if (rest[i].startsWith("--")) flags[rest[i].slice(2)] = Number(rest[++i]);
  else names.push(rest[i]);
}

if (command === "fetch") await fetchPets(names);
else if (command === "render") await renderPets(names, flags);
else {
  console.error("usage: node render.mjs fetch <name...> | render [name...] [--max px] [--scale n] [--fps n] [--max-loop ms] [--crisp] [--keep-bg]");
  process.exit(1);
}

async function fetchPets(list) {
  if (!list.length) throw new Error("name at least one pet, e.g. working-typing");
  await mkdir(svgDir, { recursive: true });
  for (const name of list) {
    if (!/^[a-z0-9-]+$/.test(name)) throw new Error(`bad pet name: ${name}`);
    const res = await fetch(source(name));
    if (!res.ok) throw new Error(`${name}: HTTP ${res.status}`);
    const svg = await res.text();
    checkSafe(name, svg);
    await writeFile(join(svgDir, `${name}.svg`), svg);
    console.log(`fetched ${name} (${svg.length} bytes)`);
  }
}

// The SVGs only need shapes and CSS; refuse anything that could run code or load from elsewhere.
function checkSafe(name, svg) {
  const bad = [/<script/i, /<foreignObject/i, /\son[a-z]+\s*=/i, /href\s*=\s*["'](?!#)/i, /url\(\s*["']?(?!#)/i, /@import/i];
  for (const re of bad)
    if (re.test(svg)) throw new Error(`${name}: refusing SVG that matches ${re}`);
  if (!/^\s*<svg[\s>]/.test(svg)) throw new Error(`${name}: not a bare <svg> document`);
}

async function renderPets(list, { scale = 0, max = 150, fps = 15, "max-loop": maxLoop = 8000, crisp = false, keepBg = false }) {
  if (!list.length)
    list = (await readdir(svgDir)).filter((f) => f.endsWith(".svg")).map((f) => basename(f, ".svg"));
  await mkdir(outDir, { recursive: true });

  const browser = await launch();
  try {
    for (const name of list) {
      const svg = await readFile(join(svgDir, `${name}.svg`), "utf8");
      checkSafe(name, svg);
      const m = await renderOne(browser, name, svg, { scale, max, fps, maxLoop, crisp, keepBg });
      console.log(`${name}: ${m.frames} frames ${m.frameWidth}x${m.frameHeight} at ${m.scale}x, loop ${m.loopMs} ms${m.seamless ? "" : " (not seamless)"}`);
    }
  } finally {
    await browser.close();
  }
  await writePreview();
}

// assets/pets/preview.html: every rendered pet playing inside a 320x170 hub-sized frame, shown at 2x
async function writePreview() {
  const pets = [];
  for (const f of (await readdir(outDir)).filter((f) => f.endsWith(".json")).sort())
    pets.push(JSON.parse(await readFile(join(outDir, f), "utf8")));
  const html = `<!doctype html><meta charset="utf-8"><title>Clawd pets on the hub</title>
<style>body{margin:16px;background:#1a1a1a;color:#ccc;font:13px Segoe UI,sans-serif}
.grid{display:flex;flex-wrap:wrap;gap:16px}figure{margin:0}canvas{display:block;width:640px;height:340px;image-rendering:pixelated;border-radius:8px}
figcaption{margin-top:6px}</style>
<div class="grid">${pets.map((p, i) => `<figure><canvas id="c${i}" width="320" height="170"></canvas><figcaption>${p.name}: ${p.frames} frames, ${p.frameWidth}x${p.frameHeight} at ${p.scale}x, ${p.loopMs} ms</figcaption></figure>`).join("")}</div>
<script>
const pets = ${JSON.stringify(pets)};
pets.forEach((p, i) => {
  const ctx = document.getElementById("c" + i).getContext("2d");
  ctx.imageSmoothingEnabled = false;
  const img = new Image();
  img.src = p.name + ".png";
  img.onload = () => {
    const start = performance.now();
    (function draw(now) {
      const f = Math.floor((now - start) / p.frameMs) % p.frames;
      ctx.fillStyle = "#000"; ctx.fillRect(0, 0, 320, 170);
      const x = Math.round(8 + (150 - p.frameWidth) / 2), y = Math.round((170 - p.frameHeight) / 2);
      ctx.drawImage(img, (f % p.columns) * p.frameWidth, Math.floor(f / p.columns) * p.frameHeight,
        p.frameWidth, p.frameHeight, x, y, p.frameWidth, p.frameHeight);
      ctx.fillStyle = "#707070"; ctx.font = "12px Bahnschrift, Segoe UI"; ctx.fillText("CLAUDE CODE", 172, 22);
      ctx.fillStyle = "#fff"; ctx.font = "bold 24px Bahnschrift, Segoe UI"; ctx.fillText(p.name.replace("working-", "").toUpperCase(), 172, 82);
      ctx.fillStyle = "#707070"; ctx.font = "12px Bahnschrift, Segoe UI"; ctx.fillText("5H 23%   7D 41%", 172, 150);
      requestAnimationFrame(draw);
    })(start);
  };
});
</script>
`;
  await writeFile(join(outDir, "preview.html"), html);
  console.log(`preview: ${join(outDir, "preview.html")}`);
}

async function launch() {
  for (const channel of ["msedge", "chrome"]) {
    try {
      return await chromium.launch({ channel });
    } catch {}
  }
  throw new Error("no Edge or Chrome found");
}

// The files leave a lot of room around the character (for jumps and particles), so the frame is cropped
// to what is actually drawn at any point of the loop and scaled up by a whole number, which keeps the
// one-unit pixels square and sharp.
async function renderOne(browser, name, svg, { scale: fixedScale, max, fps, maxLoop, crisp, keepBg }) {
  const vb = /viewBox\s*=\s*"([-\d.\s,]+)"/.exec(svg);
  if (!vb) throw new Error(`${name}: no viewBox`);
  const [, , vw, vh] = vb[1].trim().split(/[\s,]+/).map(Number);
  const measure = 10; // px per unit while measuring
  const page = await browser.newPage({ viewport: { width: Math.ceil(vw * measure), height: Math.ceil(vh * measure) } });
  try {
    const style = `<style>html,body{margin:0;background:#000}.cell{overflow:hidden}
      .cell>svg{display:block}${crisp ? "svg *{shape-rendering:crispEdges}" : ""}</style>`;

    // 1) one copy to learn the timing of every animation and drop full-frame backgrounds (a flash that
    //    covers the whole viewBox would show up as a box on the black screen)
    await page.setContent(`${style}<div class="cell">${resize(svg, vw * measure, vh * measure)}</div>`);
    const { timings, removed } = await page.evaluate(({ keepBg, vw, vh }) => {
      let removed = 0;
      if (!keepBg)
        for (const el of document.querySelectorAll("svg :is(rect,circle,ellipse,path,polygon)")) {
          if (el.closest("defs")) continue;
          const b = el.getBBox();
          if (b.width >= vw * 0.9 && b.height >= vh * 0.9) { el.remove(); removed++; }
        }
      const timings = document.getAnimations().map((a) => {
        const t = a.effect.getTiming();
        return { duration: Number(t.duration) || 0, delay: t.delay || 0, alternate: String(t.direction).includes("alternate") };
      });
      return { timings, removed };
    }, { keepBg, vw, vh });
    const { loopMs, seamless } = loopLength(timings, maxLoop);
    const frames = Math.max(1, Math.round((loopMs / 1000) * fps));
    const frameMs = loopMs / frames; // spread evenly so the loop closes exactly
    // seek past every delay so no copy sits in a "not started yet" phase
    const maxDelay = Math.max(0, ...timings.map((t) => t.delay));
    const base = Math.ceil(maxDelay / loopMs) * loopMs;

    // 2) union of everything visible over the loop, in viewBox units, snapped outward to whole units.
    //    Black shapes (ground shadow, eyes) are invisible on the hub and do not count.
    const box = await page.evaluate(({ base, loopMs, samples }) => {
      const svg = document.querySelector("svg");
      const r = svg.getBoundingClientRect();
      const [vx, vy, vw] = svg.getAttribute("viewBox").trim().split(/[\s,]+/).map(Number);
      const k = r.width / vw;
      const shapes = [...svg.querySelectorAll("rect,circle,ellipse,path,polygon,polyline,line,use,text")]
        .filter((el) => !el.closest("defs"));
      const anims = document.getAnimations();
      anims.forEach((a) => a.pause());
      let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
      for (let s = 0; s < samples; s++) {
        anims.forEach((a) => (a.currentTime = base + (s * loopMs) / samples));
        for (const el of shapes) {
          const cs = getComputedStyle(el);
          if (cs.display === "none" || cs.visibility === "hidden") continue;
          if (cs.fill === "rgb(0, 0, 0)" && cs.stroke === "none") continue;
          let opacity = 1;
          for (let e = el; e && e !== svg.parentNode; e = e.parentElement) opacity *= Number(getComputedStyle(e).opacity);
          if (opacity < 0.05) continue;
          const b = el.getBoundingClientRect();
          if (!b.width && !b.height) continue;
          x0 = Math.min(x0, b.left); y0 = Math.min(y0, b.top); x1 = Math.max(x1, b.right); y1 = Math.max(y1, b.bottom);
        }
      }
      if (x0 === Infinity) return null;
      const ux = (px) => vx + (px - r.left) / k, uy = (px) => vy + (px - r.top) / k;
      const left = Math.floor(ux(x0)) - 1, top = Math.floor(uy(y0)) - 1;
      return { x: left, y: top, w: Math.ceil(ux(x1)) + 1 - left, h: Math.ceil(uy(y1)) + 1 - top };
    }, { base, loopMs, samples: Math.min(frames, 48) });
    if (!box) throw new Error(`${name}: nothing visible`);

    const scale = fixedScale || Math.max(1, Math.min(8, Math.floor(max / Math.max(box.w, box.h))));
    const w = box.w * scale, h = box.h * scale;
    const cropped = await page.evaluate(({ box, w, h }) => {
      const svg = document.querySelector("svg");
      svg.setAttribute("viewBox", `${box.x} ${box.y} ${box.w} ${box.h}`);
      svg.setAttribute("width", w);
      svg.setAttribute("height", h);
      return svg.outerHTML;
    }, { box, w, h });

    // 3) all frames side by side, each copy frozen at its own time, captured in one screenshot
    const columns = Math.min(frames, 10), rows = Math.ceil(frames / columns);
    await page.setViewportSize({ width: columns * w, height: rows * h });
    const cells = Array.from({ length: frames }, () => `<div class="cell" style="width:${w}px;height:${h}px">${cropped}</div>`).join("");
    await page.setContent(`${style}<div style="display:grid;grid-template-columns:repeat(${columns},${w}px)">${cells}</div>`);
    await page.evaluate(({ base, frameMs }) => {
      document.querySelectorAll(".cell").forEach((cell, i) => {
        for (const a of cell.firstElementChild.getAnimations({ subtree: true })) {
          a.pause();
          a.currentTime = base + i * frameMs;
        }
      });
    }, { base, frameMs });
    await page.evaluate(() => new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r))));
    const png = await page.screenshot({ clip: { x: 0, y: 0, width: columns * w, height: rows * h } });

    const manifest = {
      name, source: `clawd-pet@${commit.slice(0, 7)}/clawd-${name}.svg`,
      frameWidth: w, frameHeight: h, columns, frames,
      frameMs: Math.round(frameMs * 100) / 100, loopMs, seamless, scale, removedBackgrounds: removed,
    };
    await writeFile(join(outDir, `${name}.png`), png);
    await writeFile(join(outDir, `${name}.json`), JSON.stringify(manifest, null, 2) + "\n");
    return manifest;
  } finally {
    await page.close();
  }
}

// Shortest time after which every animation is back where it started: the LCM of their periods.
// Jitters faster than half a second are left out (a jump in them at the seam is not visible, and at
// 15 fps they alias anyway). When the LCM is longer than maxLoop the longest period is used instead.
function loopLength(timings, maxLoop) {
  const periods = timings
    .map((t) => Math.round((t.alternate ? 2 : 1) * t.duration / 10)) // 10 ms units keeps the LCM sane
    .filter((p) => p > 0);
  if (!periods.length) return { loopMs: 1000, seamless: true }; // a still image
  const slow = periods.filter((p) => p >= 50);
  const gcd = (a, b) => (b ? gcd(b, a % b) : a);
  let lcm = 1;
  for (const p of slow.length ? slow : periods) {
    lcm = (lcm / gcd(lcm, p)) * p;
    if (lcm * 10 > maxLoop) { lcm = Math.max(...periods); break; }
  }
  return { loopMs: lcm * 10, seamless: periods.every((p) => lcm % p === 0) };
}

// sets the root <svg>'s size, leaving the rest of its tag alone
function resize(svg, w, h) {
  return svg.replace(/<svg[^>]*>/, (tag) =>
    tag.replace(/\s(width|height)\s*=\s*"[^"]*"/g, "").replace(/^<svg/, `<svg width="${w}" height="${h}"`));
}
