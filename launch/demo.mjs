// Renders demo.html frame by frame through headless Chrome and builds the two looping clips:
// assets/demo-without.gif and assets/demo-with.gif (plus mp4s) with ffmpeg.
// node demo.mjs                        -> both clips
// node demo.mjs without:2.9 with:5.1   -> just stills at those moments, for checking
import { spawn, execFileSync } from 'node:child_process';
import { mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const CHROME = 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const FPS = 20, W = 880, H = 440, SCALE = 2, GIF_W = 720;
const stills = process.argv.slice(2);

const port = 9333;
const chrome = spawn(CHROME, ['--headless=new', '--disable-gpu', '--hide-scrollbars', `--remote-debugging-port=${port}`,
  `--user-data-dir=${join(tmpdir(), 'dw-demo-chrome')}`, 'about:blank'], { stdio: 'ignore' });

async function target() {
  for (let i = 0; i < 50; i++) {
    try {
      const list = await (await fetch(`http://127.0.0.1:${port}/json`)).json();
      const page = list.find(t => t.type === 'page');
      if (page) return page.webSocketDebuggerUrl;
    } catch {}
    await new Promise(r => setTimeout(r, 200));
  }
  throw new Error('chrome did not start');
}

const ws = new WebSocket(await target());
await new Promise(r => ws.addEventListener('open', r));
let id = 0;
const pending = new Map();
ws.addEventListener('message', m => {
  const msg = JSON.parse(m.data);
  if (msg.id && pending.has(msg.id)) { pending.get(msg.id)(msg); pending.delete(msg.id); }
});
const send = (method, params = {}) => new Promise(r => { const i = ++id; pending.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
const evaluate = async expr => (await send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true })).result?.result?.value;

await send('Emulation.setDeviceMetricsOverride', { width: W, height: H, deviceScaleFactor: SCALE, mobile: false });
await send('Page.enable');

async function open(side) {
  await send('Page.navigate', { url: 'file:///' + join(HERE, 'demo.html').replace(/\\/g, '/') + '?side=' + side });
  await new Promise(r => setTimeout(r, 1500));
  await evaluate('document.fonts.ready.then(() => new Promise(r => setTimeout(r, 300))).then(() => 1)');
}
async function shot(t, file) {
  await evaluate(`render(${t}); new Promise(r => requestAnimationFrame(() => requestAnimationFrame(() => r(1))))`);
  const res = await send('Page.captureScreenshot', { format: 'png' });
  writeFileSync(file, Buffer.from(res.result.data, 'base64'));
}

if (stills.length) {
  for (const s of stills) {
    const [side, t] = s.split(':');
    await open(side);
    await shot(Number(t), join(HERE, `_still-${side}-${t}.png`));
  }
} else {
  for (const side of ['without', 'with']) {
    const frames = join(tmpdir(), 'dw-demo-frames-' + side);
    rmSync(frames, { recursive: true, force: true });
    mkdirSync(frames, { recursive: true });
    await open(side);
    const n = Math.round((await evaluate('END')) * FPS);
    for (let f = 0; f < n; f++) await shot(f / FPS, join(frames, `f${String(f).padStart(4, '0')}.png`));
    const input = ['-y', '-loglevel', 'error', '-framerate', String(FPS), '-i', join(frames, 'f%04d.png')];
    execFileSync('ffmpeg', [...input,
      '-vf', `scale=${GIF_W}:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=192:stats_mode=diff[p];[b][p]paletteuse=dither=none:diff_mode=rectangle`,
      '-loop', '0', join(HERE, 'assets', `demo-${side}.gif`)], { stdio: 'inherit' });
    execFileSync('ffmpeg', [...input, '-vf', `scale=${W * SCALE}:-2`, '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-crf', '18',
      join(HERE, 'assets', `demo-${side}.mp4`)], { stdio: 'inherit' });
    console.log(`${side}: ${n} frames`);
  }
}
ws.close();
chrome.kill();
process.exit(0);
