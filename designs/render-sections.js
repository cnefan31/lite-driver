const fs = require('node:fs');
const { spawn } = require('node:child_process');

const EDGE = '/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge';
const URL = 'file:///Users/cnefan31/development/lite-driver/docs/lite-driver-architecture-review.html';
const PORT = 9334;
const PROFILE = '/tmp/lite-driver-edge-cdp-layout';
const outDir = '/Users/cnefan31/development/lite-driver/designs';

const edge = spawn(EDGE, [
  '--headless=new', '--disable-gpu', '--hide-scrollbars', '--no-first-run',
  '--no-default-browser-check', '--disable-background-networking',
  '--disable-component-update', '--disable-features=EdgeUpdater,MediaRouter',
  `--remote-debugging-port=${PORT}`, `--user-data-dir=${PROFILE}`,
  '--window-size=1440,1200', URL,
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
  await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = reject; });
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
  await sleep(1000);
  const evaluate = async expression => {
    const r = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    if (r.exceptionDetails) throw new Error(JSON.stringify(r.exceptionDetails));
    return r.result.value;
  };
  const setViewport = (width, height, mobile) =>
    send('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile });

  const viewportChecks = {};
  for (const [name, w, h, mobile] of [['desktop', 1440, 1200, false], ['mobile', 390, 844, true]]) {
    await setViewport(w, h, mobile);
    await sleep(500);
    viewportChecks[name] = await evaluate(`(() => {
      const wraps = [...document.querySelectorAll('main .table-wrap, main .diagram')];
      return {
        innerWidth,
        docScrollWidth: document.documentElement.scrollWidth,
        bodyScrollWidth: document.body.scrollWidth,
        wrapCount: wraps.length,
        scrollableWraps: wraps.filter(el => el.scrollWidth > el.clientWidth).length,
        allWrapsOverflowXAuto: wraps.every(el => getComputedStyle(el).overflowX === 'auto'),
        maxWrapLeft: Math.max(...wraps.map(el => Math.round(el.getBoundingClientRect().left))),
        maxWrapClientWidth: Math.max(...wraps.map(el => el.clientWidth)),
      };
    })()`);
  }

  await setViewport(1440, 1200, false);
  await sleep(500);
  const items = [
    { name: 'architecture-diagram', section: 'architecture', kind: '.diagram', index: 0 },
    { name: 'topology-diagram', section: 'architecture', kind: '.diagram', index: 1 },
    { name: 'state-diagram', section: 'state', kind: '.diagram', index: 0 },
    { name: 'decisions', section: 'decisions', kind: 'section', index: 0 },
  ];
  const captures = [];
  for (const item of items) {
    const rect = await evaluate(`(() => {
      const root = document.getElementById(${JSON.stringify(item.section)});
      const el = ${JSON.stringify(item.kind)} === 'section'
        ? root
        : root.querySelectorAll(${JSON.stringify(item.kind)})[${item.index}];
      if (!el) return null;
      const r = el.getBoundingClientRect();
      return { x: r.left + scrollX, y: r.top + scrollY, width: r.width, height: r.height };
    })()`);
    if (!rect) throw new Error(`Missing target for ${item.name}`);
    await sleep(200);
    const clip = {
      x: Math.max(0, Math.round(rect.x)),
      y: Math.max(0, Math.round(rect.y)),
      width: Math.round(Math.min(1440 - rect.x, rect.width)),
      height: Math.round(Math.min(2000, rect.height)),
      scale: 1,
    };
    const shot = await send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true, clip });
    const path = `${outDir}/check-${item.name}.png`;
    fs.writeFileSync(path, Buffer.from(shot.data, 'base64'));
    captures.push({ ...item, path, clip, rect });
  }

  console.log(JSON.stringify({ viewportChecks, captures }, null, 2));
  ws.close();
  edge.kill('SIGTERM');
  await sleep(300);
  edge.kill('SIGKILL');
})().catch(err => {
  console.error('CAPTURE_FAILED', err.message, stderr.slice(-1000));
  edge.kill('SIGKILL');
  process.exitCode = 1;
});
