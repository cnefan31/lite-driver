const fs = require('node:fs');
const { spawn } = require('node:child_process');

const EDGE = '/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge';
const URL = 'http://127.0.0.1:8099/';
const PORT = 9341;
const PROFILE = '/tmp/bench-edge-profile';
const OUT = '/Users/cnefan31/development/lite-driver/designs/bench-ui.png';

const edge = spawn(EDGE, [
  '--headless=new', '--disable-gpu', '--hide-scrollbars', '--no-first-run',
  '--no-default-browser-check', '--disable-background-networking',
  '--disable-component-update', '--disable-features=EdgeUpdater,MediaRouter',
  `--remote-debugging-port=${PORT}`, `--user-data-dir=${PROFILE}`,
  '--window-size=1440,1400', URL,
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
      const page = pages.find(p => p.type === 'page' && p.url.includes('127.0.0.1:8099'));
      if (page) return page;
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
  await sleep(1500);

  const evaluate = async expr => {
    const r = await send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true });
    if (r.exceptionDetails) throw new Error(JSON.stringify(r.exceptionDetails.exception || r.exceptionDetails));
    return r.result.value;
  };

  // 1) 页面初始状态：协议下拉是否被填充
  const initial = await evaluate(`(() => ({
    title: document.title,
    cliOptions: [...document.querySelectorAll('#cli-proto option')].map(o => o.value),
    srvOptions: [...document.querySelectorAll('#srv-proto option')].map(o => o.value),
    bannerVisible: getComputedStyle(document.getElementById('banner')).display !== 'none'
  }))()`);
  console.log('初始状态:', JSON.stringify(initial));

  // 2) 点击启动双驱动
  await evaluate(`document.getElementById('btn-start').click()`);
  await sleep(4000);
  const afterStart = await evaluate(`(() => ({
    srv: document.getElementById('srv-state').textContent,
    cli: document.getElementById('cli-state').textContent,
    port: document.getElementById('srv-port').textContent,
    sessions: document.getElementById('srv-sessions').textContent,
    epoch: document.getElementById('cli-epoch').textContent,
    banner: document.getElementById('banner').textContent
  }))()`);
  console.log('启动后:', JSON.stringify(afterStart));

  // 3) 点击连通测试
  await evaluate(`document.getElementById('btn-conn').click()`);
  await sleep(4000);
  const afterConn = await evaluate(`(() => {
    const items = [...document.querySelectorAll('#checks li')].map(li => ({
      mark: li.querySelector('.mark').textContent.trim(),
      text: li.querySelector('span:nth-child(2)') ? li.querySelector('span:nth-child(2)').childNodes[0].textContent.trim() : ''
    }));
    return { items, banner: document.getElementById('banner').textContent,
             logLines: document.getElementById('log').textContent.split('\\n').length };
  })()`);
  console.log('连通测试:', JSON.stringify(afterConn));

  // 4) 切换协议到 modbus-like 并重测
  await evaluate(`(() => {
    const c = document.getElementById('cli-proto'); c.value = 'modbus-like';
    c.dispatchEvent(new Event('change'));
    const s = document.getElementById('srv-proto'); s.value = 'modbus-like';
    s.dispatchEvent(new Event('change'));
  })()`);
  await sleep(300);
  await evaluate(`document.getElementById('btn-start').click()`);
  await sleep(3500);
  await evaluate(`document.getElementById('btn-conn').click()`);
  await sleep(3500);
  const modbus = await evaluate(`(() => ({
    checks: [...document.querySelectorAll('#checks .mark')].map(m => m.textContent.trim()),
    banner: document.getElementById('banner').textContent
  }))()`);
  console.log('modbus-like:', JSON.stringify(modbus));

  // 5) 布局检查
  const layout = await evaluate(`(() => ({
    scrollWidth: document.documentElement.scrollWidth,
    innerWidth: innerWidth,
    overflow: [...document.querySelectorAll('.card, .pill, button')]
      .filter(el => { const r = el.getBoundingClientRect(); return r.right > innerWidth + 1; })
      .map(el => el.className).slice(0, 5)
  }))()`);
  console.log('布局:', JSON.stringify(layout));

  const shot = await send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true });
  fs.writeFileSync(OUT, Buffer.from(shot.data, 'base64'));
  console.log('截图:', OUT, fs.statSync(OUT).size, 'bytes');

  ws.close();
  edge.kill('SIGTERM');
  await sleep(300);
  edge.kill('SIGKILL');
})().catch(e => {
  console.error('FAILED', e.message, stderr.slice(-800));
  edge.kill('SIGKILL');
  process.exitCode = 1;
});
