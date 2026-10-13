// 本地指令行为探测脚本
import crypto from 'node:crypto';
const BASE = process.env.AISERVER_BASE || 'http://127.0.0.1:8899';

// 生成请求签名
function sign(method, pathWithQuery, bodyText, signKey) {
  const ts = Date.now().toString();
  const nonce = crypto.randomBytes(18).toString('hex');
  const bodyHash = crypto.createHash('sha256').update(bodyText ?? '', 'utf8').digest('hex');
  const canonical = [method, pathWithQuery, ts, nonce, bodyHash].join('\n');
  return { ts, nonce, sig: crypto.createHmac('sha256', signKey).update(canonical, 'utf8').digest('base64') };
}

// 发一次请求并解析响应
async function call(method, path, { token, signKey, body } = {}) {
  const bodyText = body === undefined ? null : JSON.stringify(body);
  const headers = { Accept: 'application/json' };
  if (token) headers.Authorization = 'Bearer ' + token;
  if (bodyText !== null) headers['Content-Type'] = 'application/json';
  if (signKey && ['POST', 'PUT', 'PATCH', 'DELETE'].includes(method)) { // 写操作才签名
    const s = sign(method, path, bodyText ?? '', signKey);
    headers['X-Timestamp'] = s.ts; headers['X-Nonce'] = s.nonce; headers['X-Signature'] = s.sig;
  }
  const res = await fetch(BASE + path, { method, headers, body: bodyText ?? undefined, redirect: 'manual' });
  const text = await res.text();
  let json = null; try { json = text ? JSON.parse(text) : null; } catch { json = { raw: text.slice(0, 200) }; }
  return { status: res.status, body: json };
}

// 登录取 token
const login = await call('POST', '/api/auth/login', { body: { userName: 'employee', password: 'Staff#2026abc' } });
if (!login.body?.ok) { console.log('登录失败', JSON.stringify(login.body)); process.exit(1); } // 登录不上就停
const s = { token: login.body.data.token, signKey: login.body.data.signKey };
console.log('登录成功：', login.body.data.user.displayName, login.body.data.user.roleLabel, login.body.data.user.employeeId);

// 待探测的指令
const cases = [
  '列出我的文件',
  '读取 draft/周报.txt',
  '列出共享区文件',
  '写入 report/季度总结.md 内容：Q3 完成情况',
  '删除 待删除.txt 原因：临时文件',
  '目前帮我查一下有多少用户',
  '我的可用区域与权限',
  '我的通知'
];

// 逐条发送并打印结果
for (const message of cases) {
  const r = await call('POST', '/api/agent/chat', { ...s, body: { message } });
  const d = r.body?.data ?? {};
  console.log('--------------------------------------------------');
  console.log('输入：', message);
  console.log('  source=' + d.source + '  tool=' + d.tool + '  status=' + d.status + '  task=' + (d.taskId || '-'));
  console.log('  意图：', d.intent);
  console.log('  reply：', (d.reply || '').replace(/\n/g, ' ').slice(0, 200));
  if (d.notes?.length) console.log('  notes：', d.notes.join(' / ')); // 有备注才打印
  await new Promise(r => setTimeout(r, 400)); // 限速
}
