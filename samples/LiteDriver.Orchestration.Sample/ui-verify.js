const fs = require('node:fs');
const { spawn } = require('node:child_process');

const EDGE = '/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge';
const BASE = 'http://127.0.0.1:8110';
const PORT = 9351;
const PROFILE = '/tmp/orch-edge-profile';
const OUT = '/Users/cnefan31/development/lite-driver/designs/orchestration-ui.png';

const edge = spawn(EDGE, [
  '--headless=new', '--disable-gpu', '--hide-scrollbars', '--no-first-run',
  '--no-default-browser-check', '--disable-background-networking',
  '--disable-component-update', '--disable-features=EdgeUpdater,MediaRouter',
  `--remote-debugging-port=${PORT}`, `--user-data-dir=${PROFILE}`,
  '--window-size=1600,950', BASE + '/',
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
      const pg = pages.find(p => p.type === 'page' && p.url.includes('8110'));
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
  await sleep(1800);

  const evalJs = async expr => {
    const r = await send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true });
    if (r.exceptionDetails) throw new Error(JSON.stringify(r.exceptionDetails.exception || r.exceptionDetails));
    return r.result.value;
  };

  const report = {};

  // 1) 初始渲染：示例图应有 3 个节点、2 条连线
  report.initial = await evalJs(`(() => ({
    title: document.title,
    nodes: document.querySelectorAll('.node').length,
    nodeKinds: [...document.querySelectorAll('.node')].map(n => n.dataset.kind),
    wires: document.querySelectorAll('svg.wires path[stroke^="url"]').length,
    protocols: document.querySelectorAll('#p-kind option').length
  }))()`);

  // 2) 点击某个节点，属性面板应出现
  await evalJs(`(() => {
    const n = [...document.querySelectorAll('.node')].find(x => x.dataset.kind === 'Client');
    const r = n.getBoundingClientRect();
    n.dispatchEvent(new PointerEvent('pointerdown', { bubbles:true, clientX:r.left+40, clientY:r.top+18, pointerId:1 }));
    n.dispatchEvent(new PointerEvent('pointerup', { bubbles:true, clientX:r.left+40, clientY:r.top+18, pointerId:1 }));
  })()`);
  await sleep(400);
  report.props = await evalJs(`(() => ({
    hasLabel: !!document.getElementById('p-label'),
    hasEndpoints: !!document.getElementById('p-endpoints'),
    hasDelete: !!document.getElementById('p-delete'),
    emptyHidden: !document.querySelector('.props .empty')
  }))()`);

  // 3) 修改端点文本 -> 模型应更新
  await evalJs(`(() => {
    const t = document.getElementById('p-endpoints');
    t.value = '127.0.0.1:65001';
    t.dispatchEvent(new Event('input', { bubbles:true }));
  })()`);
  await sleep(300);
  report.editEndpoints = await evalJs(`(() => {
    const n = [...document.querySelectorAll('.node')].find(x => x.dataset.kind === 'Client');
    return n.textContent.includes('65001');
  })()`);

  // 4) 通过 HTML5 拖拽新增一个「服务端」节点
  report.beforeDrag = await evalJs(`document.querySelectorAll('.node').length`);
  await evalJs(`(() => {
    const src = document.querySelector('.source[data-kind="Server"]');
    const wrap = document.getElementById('canvasWrap');
    const wr = wrap.getBoundingClientRect();
    const dt = new DataTransfer();
    src.dispatchEvent(new DragEvent('dragstart', { bubbles:true, dataTransfer:dt }));
    wrap.dispatchEvent(new DragEvent('dragover', { bubbles:true, cancelable:true, dataTransfer:dt, clientX: wr.left+700, clientY: wr.top+420 }));
    wrap.dispatchEvent(new DragEvent('drop', { bubbles:true, cancelable:true, dataTransfer:dt, clientX: wr.left+700, clientY: wr.top+420 }));
  })()`);
  await sleep(500);
  report.afterDrag = await evalJs(`(() => ({
    count: document.querySelectorAll('.node').length,
    servers: [...document.querySelectorAll('.node')].filter(n=>n.dataset.kind==='Server').length
  }))()`);

  // 5) 校验按钮：应给出结果（新增服务端未连协议，预期校验失败）
  await evalJs(`document.getElementById('btnValidate').click()`);
  await sleep(1200);
  report.validate = await evalJs(`(() => ({
    msg: document.getElementById('msg').textContent,
    errNodes: document.querySelectorAll('.node.err').length
  }))()`);

  // 6) 删除刚加的未连线服务端，再校验应通过
  await evalJs(`(() => {
    const bad = [...document.querySelectorAll('.node.err')].pop();
    if (bad) {
      const r = bad.getBoundingClientRect();
      bad.dispatchEvent(new PointerEvent('pointerdown', { bubbles:true, clientX:r.left+40, clientY:r.top+18, pointerId:2 }));
      bad.dispatchEvent(new PointerEvent('pointerup', { bubbles:true, clientX:r.left+40, clientY:r.top+18, pointerId:2 }));
    }
  })()`);
  await sleep(300);
  await evalJs(`(() => { const d = document.getElementById('p-delete'); if(d) d.click(); })()`);
  await sleep(400);
  await evalJs(`document.getElementById('btnValidate').click()`);
  await sleep(1200);
  report.validateAfterFix = await evalJs(`document.getElementById('msg').textContent`);

  // 7) 应用
  await evalJs(`document.getElementById('btnApply').click()`);
  await sleep(2500);
  report.apply = await evalJs(`(() => ({
    msg: document.getElementById('msg').textContent,
    rev: document.getElementById('sRev').textContent,
    ready: document.getElementById('sReady').textContent
  }))()`);

  // 8) 布局检查
  report.layout = await evalJs(`(() => ({
    innerWidth: innerWidth,
    scrollWidth: document.documentElement.scrollWidth,
    overflow: [...document.querySelectorAll('.node')].filter(n => {
      const r = n.getBoundingClientRect();
      return r.right > innerWidth + 1;
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
