const fs = require('node:fs');
const { spawn } = require('node:child_process');

const EDGE = '/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge';
const BASE = 'http://127.0.0.1:8111';
const PORT = 9361;
const PROFILE = '/tmp/orch2-edge-profile';
const OUT = '/Users/cnefan31/development/lite-driver/designs/orchestration-typed.png';

const edge = spawn(EDGE, [
  '--headless=new', '--disable-gpu', '--hide-scrollbars', '--no-first-run',
  '--no-default-browser-check', '--disable-background-networking',
  '--disable-component-update', '--disable-features=EdgeUpdater,MediaRouter',
  `--remote-debugging-port=${PORT}`, `--user-data-dir=${PROFILE}`,
  '--window-size=1680,980', BASE + '/',
], { stdio: ['ignore', 'pipe', 'pipe'] });
let stderr = '';
edge.stderr.on('data', d => { stderr += d.toString(); });
const sleep = ms => new Promise(r => setTimeout(r, ms));

async function getJson(p) {
  const res = await fetch(`http://127.0.0.1:${PORT}${p}`);
  if (!res.ok) throw new Error(`${p} -> ${res.status}`);
  return res.json();
}
async function waitForPage() {
  for (let i = 0; i < 80; i++) {
    try {
      const pages = await getJson('/json');
      const pg = pages.find(p => p.type === 'page' && p.url.includes('8111'));
      if (pg) return pg;
    } catch (_) {}
    await sleep(250);
  }
  throw new Error('no page target; stderr=' + stderr.slice(-600));
}

(async () => {
  const page = await waitForPage();
  const ws = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
  let seq = 0;
  const pending = new Map();
  ws.onmessage = ev => {
    const m = JSON.parse(ev.data);
    if (m.id && pending.has(m.id)) {
      const { resolve, reject } = pending.get(m.id);
      pending.delete(m.id);
      m.error ? reject(new Error(JSON.stringify(m.error))) : resolve(m.result);
    }
  };
  const send = (method, params = {}) => new Promise((resolve, reject) => {
    const id = ++seq;
    pending.set(id, { resolve, reject });
    ws.send(JSON.stringify({ id, method, params }));
  });
  await send('Page.enable');
  await send('Runtime.enable');
  await sleep(2000);

  const evalJs = async expr => {
    const r = await send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true });
    if (r.exceptionDetails) throw new Error(JSON.stringify(r.exceptionDetails.exception || r.exceptionDetails));
    return r.result.value;
  };

  const report = {};

  // 1) 托盘应渲染 7 种节点类型（从后端动态获取）
  report.palette = await evalJs(`(() => ({
    sources: [...document.querySelectorAll('#palette .source')].map(s => s.dataset.type),
    nodes: document.querySelectorAll('.node').length,
    ports: document.querySelectorAll('.port').length
  }))()`);

  // 2) 端口应带类型标注
  report.portTypes = await evalJs(`(() => {
    const out = {};
    document.querySelectorAll('.node').forEach(n => {
      out[n.dataset.type] = [...n.querySelectorAll('.port')]
        .map(p => p.dataset.port + ':' + p.dataset.type + ':' + p.dataset.dir);
    });
    return out;
  })()`);

  // 3) 拖入一个变换节点
  report.beforeDrag = await evalJs(`document.querySelectorAll('.node').length`);
  await evalJs(`(() => {
    const src = document.querySelector('.source[data-type="transform"]');
    const wrap = document.getElementById('canvasWrap');
    const wr = wrap.getBoundingClientRect();
    const dt = new DataTransfer();
    src.dispatchEvent(new DragEvent('dragstart', { bubbles:true, dataTransfer:dt }));
    wrap.dispatchEvent(new DragEvent('dragover', { bubbles:true, cancelable:true, dataTransfer:dt, clientX: wr.left+640, clientY: wr.top+330 }));
    wrap.dispatchEvent(new DragEvent('drop', { bubbles:true, cancelable:true, dataTransfer:dt, clientX: wr.left+640, clientY: wr.top+330 }));
  })()`);
  await sleep(500);
  report.afterDrag = await evalJs(`(() => ({
    count: document.querySelectorAll('.node').length,
    transforms: [...document.querySelectorAll('.node')].filter(n=>n.dataset.type==='transform').length
  }))()`);

  // 4) 选中客户端，检查属性面板显示端口类型与必连标记
  await evalJs(`(() => {
    const n = [...document.querySelectorAll('.node')].find(x => x.dataset.type === 'client');
    const r = n.getBoundingClientRect();
    n.dispatchEvent(new PointerEvent('pointerdown', { bubbles:true, clientX:r.left+40, clientY:r.top+16, pointerId:1 }));
    n.dispatchEvent(new PointerEvent('pointerup', { bubbles:true, clientX:r.left+40, clientY:r.top+16, pointerId:1 }));
  })()`);
  await sleep(400);
  report.props = await evalJs(`(() => ({
    hasPortsView: !!document.querySelector('.ports-view'),
    portsText: (document.querySelector('.ports-view')||{}).innerText || '',
    fields: [...document.querySelectorAll('.props .field label')].map(l => l.textContent)
  }))()`);

  // 5) 校验按钮
  await evalJs(`document.getElementById('btnValidate').click()`);
  await sleep(1500);
  report.validate = await evalJs(`document.getElementById('msg').textContent`);

  // 6) 布局检查
  report.layout = await evalJs(`(() => ({
    innerWidth: innerWidth,
    scrollWidth: document.documentElement.scrollWidth,
    overflowNodes: [...document.querySelectorAll('.node')].filter(n => {
      const r = n.getBoundingClientRect(); return r.right > innerWidth + 1;
    }).length
  }))()`);

  const shot = await send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
  fs.writeFileSync(OUT, Buffer.from(shot.data, 'base64'));

  console.log(JSON.stringify(report, null, 2));
  ws.close();
  edge.kill('SIGTERM');
  await sleep(300);
  edge.kill('SIGKILL');
})().catch(e => {
  console.error('FAILED', e.message, stderr.slice(-800));
  edge.kill('SIGKILL');
  process.exitCode = 1;
});
