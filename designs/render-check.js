const fs = require('node:fs');
const { spawn } = require('node:child_process');

const EDGE = '/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge';
const URL = 'file:///Users/cnefan31/development/lite-driver/docs/lite-driver-architecture-review.html';
const PORT = 9333;
const PROFILE = '/tmp/lite-driver-edge-cdp-profile';
const target = process.argv[2] || 'desktop';
const width = target === 'mobile' ? 390 : 1440;
const height = target === 'mobile' ? 844 : 1200;
const out = `/Users/cnefan31/development/lite-driver/designs/lite-driver-${target}.png`;

const edge = spawn(EDGE, [
  '--headless=new', '--disable-gpu', '--hide-scrollbars', '--no-first-run',
  '--no-default-browser-check', '--disable-background-networking',
  '--disable-component-update', '--disable-features=EdgeUpdater,MediaRouter',
  `--remote-debugging-port=${PORT}`, `--user-data-dir=${PROFILE}-${target}`,
  `--window-size=${width},${height}`, URL,
], { stdio: ['ignore', 'pipe', 'pipe'] });
let stderr = '';
edge.stderr.on('data', d => { stderr += d.toString(); });

const sleep = ms => new Promise(r => setTimeout(r, ms));

async function getJson(path) {
  const res = await fetch(`http://127.0.0.1:${PORT}${path}`);
  if (!res.ok) throw new Error(`GET ${path} -> ${res.status}`);
  return res.json();
}

async function waitForPage() {
  for (let i = 0; i < 60; i++) {
    try {
      const pages = await getJson('/json');
      const page = pages.find(p => p.type === 'page' && p.url.startsWith('file:'));
      if (page) return page;
    } catch (_) {}
    await sleep(250);
  }
  throw new Error(`No page target. stderr=${stderr.slice(-800)}`);
}

(async () => {
  const page = await waitForPage();
  const ws = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => {
    ws.onopen = resolve;
    ws.onerror = reject;
  });
  let seq = 0;
  const pending = new Map();
  ws.onmessage = event => {
    const msg = JSON.parse(event.data);
    if (msg.id && pending.has(msg.id)) {
      const { resolve, reject } = pending.get(msg.id);
      pending.delete(msg.id);
      msg.error ? reject(new Error(JSON.stringify(msg.error))) : resolve(msg.result);
    }
  };
  const send = (method, params = {}) => new Promise((resolve, reject) => {
    const id = ++seq;
    pending.set(id, { resolve, reject });
    ws.send(JSON.stringify({ id, method, params }));
  });
  await send('Page.enable');
  await send('Runtime.enable');
  await send('Emulation.setDeviceMetricsOverride', {
    width, height, deviceScaleFactor: 1, mobile: target === 'mobile',
  });
  await sleep(1200);
  const evaluate = async expression => {
    const r = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    if (r.exceptionDetails) throw new Error(JSON.stringify(r.exceptionDetails));
    return r.result.value;
  };
  const metrics = await evaluate(`(() => {
    const doc = document.documentElement;
    const main = document.querySelector('main.document');
    const svgs = [...document.querySelectorAll('main svg')];
    const overflow = [...document.querySelectorAll('main *')].filter(el => {
      const r = el.getBoundingClientRect();
      return r.width > 0 && (r.right > innerWidth + 1 || r.left < -1);
    }).slice(0, 8).map(el => el.tagName + '.' + el.className + '@' + Math.round(el.getBoundingClientRect().left) + '..' + Math.round(el.getBoundingClientRect().right));
    return {
      title: document.title,
      width: innerWidth,
      height: innerHeight,
      bodyScrollWidth: document.body.scrollWidth,
      docScrollWidth: doc.scrollWidth,
      docScrollHeight: doc.scrollHeight,
      mainWidth: main.getBoundingClientRect().width,
      sections: document.querySelectorAll('main section').length,
      svgSizes: svgs.map(s => { const r = s.getBoundingClientRect(); return [Math.round(r.width), Math.round(r.height)]; }),
      svgTitleCount: document.querySelectorAll('main svg title').length,
      tableCount: document.querySelectorAll('main table').length,
      q9: document.body.innerText.includes('Q9'),
      overflow,
    };
  })()`);
  const shot = await send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
  fs.writeFileSync(out, Buffer.from(shot.data, 'base64'));
  console.log(JSON.stringify({ target, out, metrics }, null, 2));
  ws.close();
  edge.kill('SIGTERM');
  await sleep(300);
  edge.kill('SIGKILL');
})().catch(err => {
  console.error('RENDER_FAILED', err.message, stderr.slice(-1000));
  edge.kill('SIGKILL');
  process.exitCode = 1;
});
