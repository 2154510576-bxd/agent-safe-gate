//多步代理验收，配合 mock-ai.mjs
//用法：node e2e-agent.mjs [baseUrl]
import crypto from 'node:crypto';

const BASE = process.argv[2] || 'http://127.0.0.1:8904';
let pass = 0, fail = 0;
const failures = [];

//断言并累计通过/失败
function check(name, condition, extra = '') {
  if (condition) { pass++; console.log(`  [PASS] ${name}`); }
  else { fail++; failures.push(name + (extra ? ' -> ' + extra : '')); console.log(`  [FAIL] ${name} ${extra}`); }
}

//生成请求签名头
function sign(method, path, bodyText, signKey) {
  const ts = Date.now().toString();
  const nonce = crypto.randomBytes(18).toString('hex');
  const bodyHash = crypto.createHash('sha256').update(bodyText ?? '', 'utf8').digest('hex');
  const canonical = [method, path, ts, nonce, bodyHash].join('\n');
  return { ts, nonce, sig: crypto.createHmac('sha256', signKey).update(canonical, 'utf8').digest('base64') };
}

//发请求并解析 JSON
async function call(method, path, { token, signKey, body } = {}) {
  const bodyText = body === undefined ? null : JSON.stringify(body);
  const headers = { Accept: 'application/json' };
  if (token) headers.Authorization = 'Bearer ' + token;
  if (bodyText !== null) headers['Content-Type'] = 'application/json';
  if (signKey && ['POST', 'PUT', 'PATCH', 'DELETE'].includes(method)) {
    const s = sign(method, path, bodyText ?? '', signKey);
    headers['X-Timestamp'] = s.ts; headers['X-Nonce'] = s.nonce; headers['X-Signature'] = s.sig;
  }
  const res = await fetch(BASE + path, { method, headers, body: bodyText ?? undefined, redirect: 'manual' });
  const text = await res.text();
  let json = null; try { json = text ? JSON.parse(text) : null; } catch { json = { raw: text.slice(0, 160) }; }
  return { status: res.status, body: json };
}

//登录取 token 与签名密钥
async function login(userName, password) {
  const res = await call('POST', '/api/auth/login', { body: { userName, password } });
  if (!res.body?.ok) throw new Error(`登录失败 ${userName}: ${JSON.stringify(res.body).slice(0, 160)}`);
  return { token: res.body.data.token, signKey: res.body.data.signKey, user: res.body.data.user };
}

//发一条对话消息
const chat = (s, message) => call('POST', '/api/agent/chat', { ...s, body: { message } });

console.log('== 多步代理能力验收（mock 模型） ==');

const health = await call('GET', '/api/public/health');
check('服务健康检查', health.status === 200, `status=${health.status}`);
console.log('      AI 模式：' + health.body?.data?.mode);

const admin = await login('admin', 'SysRoot#2026ak');
const finance = await login('finance', 'Money#2026abc');
const employee = await login('employee', 'Staff#2026abc');
check('四角色中的三个登录成功', !!admin.token && !!finance.token && !!employee.token);

//多步检索后汇总
const listFiles = await chat(employee, '列出我的文件');
check('准备：列出文件可用（只读步骤 + AI 汇总）',
  ['CHAT', 'EXECUTED'].includes(listFiles.body?.data?.status) && (listFiles.body?.data?.steps || []).some(s => s.tool === 'list_files'),
  `status=${listFiles.body?.data?.status} steps=${JSON.stringify((listFiles.body?.data?.steps || []).map(s => s.tool))}`);

//造一份日报文件供检索命中
const createReport = await chat(employee, '写入 日报-2026-09-29.txt 内容：三号产线改造完毕');
if (createReport.body?.data?.status === 'PENDING_APPROVAL') {
  await call('POST', `/api/admin/tasks/${createReport.body.data.taskId}/approve`, { token: admin.token, signKey: admin.signKey, body: { reason: '同意（测试造数）' } });
}
const reportExists = await call('GET', '/api/agent/files/content?scope=private&path=' + encodeURIComponent('日报-2026-09-29.txt'), { token: employee.token });
check('准备：日报文件已存在', reportExists.status === 200, `status=${reportExists.status}`);

const multi = await chat(employee, '读一下日报，告诉我写了什么');
const md = multi.body?.data || {};
check('多步任务最终给出结论（CHAT/已完成）', md.status === 'CHAT', `status=${md.status}`);
check('轨迹里至少有 2 步', (md.steps || []).length >= 2, `steps=${(md.steps || []).length}`);
check('第 1 步是检索（search_files）', (md.steps || [])[0]?.tool === 'search_files', JSON.stringify((md.steps || [])[0] || {}));
check('第 2 步是读取检索到的文件（read_file）', (md.steps || [])[1]?.tool === 'read_file', JSON.stringify((md.steps || [])[1] || {}));
check('第 2 步用的是真实命中的路径', ((md.steps || [])[1]?.summary || '').includes('日报-2026-09-29'), (md.steps || [])[1]?.summary);
check('最终回答引用了真实执行结果（假模型汇总观察）', (md.reply || '').includes('假模型结论') && (md.reply || '').includes('文件'), md.reply);
console.log('      步骤链：' + (md.steps || []).map(s => s.title + '(' + s.tool + ')').join(' → '));

//周报的检索读取链路
const createWeekly = await chat(employee, '写入 周报-2026-10-05.txt 内容：本周完成报销域对接');
if (createWeekly.body?.data?.status === 'PENDING_APPROVAL') {
  await call('POST', `/api/admin/tasks/${createWeekly.body.data.taskId}/approve`, { token: admin.token, signKey: admin.signKey, body: { reason: '同意（测试造数）' } });
}
const weekly = await chat(employee, '看一下周报');
const wk = weekly.body?.data || {};
check('周报检索链路给出了结论', wk.status === 'CHAT', `status=${wk.status}`);
check('周报链路先检索再读取', (wk.steps || [])[0]?.tool === 'search_files' && (wk.steps || [])[1]?.tool === 'read_file',
  JSON.stringify((wk.steps || []).map(s => s.tool)));

//统计问答走统计工具
const stats = await chat(finance, '统计业务数据');
const st = stats.body?.data || {};
check('统计请求命中统计工具', (st.steps || []).some(s => s.tool === 'count_records'),
  JSON.stringify((st.steps || []).map(s => s.tool)));
check('统计请求返回结论而不是报错', st.status === 'CHAT' && !/失败|错误/.test(st.reply || ''), (st.reply || '').slice(0, 140));

//账号名册与角色隔离
const usersByAdmin = await chat(admin, '目前帮我查一下有多少用户');
const ua = usersByAdmin.body?.data || {};
check('管理员可查询账号数量', ua.status === 'CHAT' && /账号总数 \d+ 个/.test(ua.reply || ''), ua.reply);
check('账号统计包含角色分布', /员工 \d+ 个|财务 \d+ 个|管理员 \d+ 个|超级管理员 \d+ 个/.test(JSON.stringify(ua.steps || [])),
  ua.reply + ' steps=' + JSON.stringify(ua.steps || []).slice(0, 160));
check('名册结果不含任何口令字段', !JSON.stringify(ua.result || {}).toLowerCase().includes('password'),
  JSON.stringify(ua.result || {}).slice(0, 160));

const usersByFinance = await chat(finance, '目前帮我查一下有多少用户');
check('财务部查询账号名册被拦（角色隔离）', usersByFinance.body?.data?.status === 'REJECTED_RULE',
  `status=${usersByFinance.body?.data?.status} reply=${usersByFinance.body?.data?.reply}`);
check('拦截原因明确（缺少权限）', (usersByFinance.body?.data?.reply || '').includes('缺少权限'),
  usersByFinance.body?.data?.reply);

const usersByEmployee = await chat(employee, '有多少用户');
check('员工查询账号名册同样被拦', usersByEmployee.body?.data?.status === 'REJECTED_RULE',
  `status=${usersByEmployee.body?.data?.status}`);

//超管同属管理端，走管理端工具
const root = await login('root', 'Overlord#2026ak');
check('超级管理员登录成功', !!root.token, JSON.stringify(root.error || ''));
const usersByRoot = await chat(root, '目前帮我查一下有多少用户');
const ur = usersByRoot.body?.data || {};
check('超级管理员不被"仅管理员可用"挡住', ur.status === 'CHAT' && !/仅管理员可用/.test(ur.reply || ''),
  `status=${ur.status} reply=${(ur.reply || '').slice(0, 160)}`);
check('超级管理员同样拿到名册统计', /账号总数 \d+ 个/.test(ur.reply || ''), (ur.reply || '').slice(0, 160));
check('超级管理员的轨迹里含管理端只读工具', (ur.steps || []).some(s => s.tool === 'list_users'),
  JSON.stringify((ur.steps || []).map(s => s.tool)));

//报销单可见范围
//未配置飞书则跳过该段断言
const empList = await call('GET', '/api/invoice/list', { token: employee.token });
const finList = await call('GET', '/api/invoice/list', { token: finance.token });
if (empList.status === 502 || finList.status === 502) {
  console.log('  [SKIP] 报销单可见范围断言需要配置飞书（本次部署未配置，/api/invoice/list 返回 502 FEISHU_UNAVAILABLE）');
} else {
  check('员工查询报销单只得到本人的单', empList.status === 200 && empList.body?.data?.scope === 'mine',
    `status=${empList.status} scope=${empList.body?.data?.scope}`);
  check('财务部查询报销单可看全部', finList.status === 200 && finList.body?.data?.scope === 'all',
    `status=${finList.status} scope=${finList.body?.data?.scope}`);
}
const empMeta = await call('GET', '/api/invoice/meta', { token: employee.token });
const finMeta = await call('GET', '/api/invoice/meta', { token: finance.token });
check('审核能力按角色下发', empMeta.body?.data?.canReview === false && finMeta.body?.data?.canReview === true,
  `employee=${empMeta.body?.data?.canReview} finance=${finMeta.body?.data?.canReview}`);

//文件域澄清
//重复创建已存在文件走澄清
const duplicateCreate = await chat(employee, '写入 日报-2026-09-29.txt 内容：重复创建应当被询问');
const dc = duplicateCreate.body?.data || {};
check('对已存在文件再走 create 不会静默覆盖（未执行写操作）', dc.status !== 'EXECUTED',
  `status=${dc.status} tool=${dc.tool} reply=${(dc.reply || '').slice(0, 140)}`);
check('该请求没有直接排队成覆盖任务（说明走的是澄清而不是默认覆盖）', !dc.taskId,
  `taskId=${dc.taskId} status=${dc.status}`);
const afterDuplicate = await call('GET', '/api/agent/files/content?scope=private&path=' + encodeURIComponent('日报-2026-09-29.txt'), { token: employee.token });
check('原文件内容保持不变', (afterDuplicate.body?.data?.data?.content || '').includes('三号产线改造完毕'),
  (afterDuplicate.body?.data?.data?.content || '').slice(0, 120));

//已下线业务域回归
//旧域工具不产生任务
const retiredTool = await chat(finance, '把示例物流标记为重要，原因：客户升级');
const rt = retiredTool.body?.data || {};
check('已下线的客户档案工具不再产生任务', rt.status !== 'PENDING_APPROVAL' && !rt.taskId,
  `status=${rt.status} taskId=${rt.taskId}`);
check('请求被降级为对话而不是执行', rt.status === 'CHAT' || rt.status === 'REJECTED_RULE',
  `status=${rt.status} reply=${(rt.reply || '').slice(0, 160)}`);

//写操作仍走审批链路
const writeAfterRead = await chat(employee, '读一下日报，然后把要点写进 周报.txt');
const wr = writeAfterRead.body?.data || {};
//写操作经规则引擎与 AI 审核
check('先读后写到达写操作环节（已执行或转人工）', ['EXECUTED', 'PENDING_APPROVAL'].includes(wr.status), `status=${wr.status}`);
check('写操作进入了任务与审计链路', !!wr.taskId, `taskId=${wr.taskId}`);
check('读过的步骤在轨迹里保留', (wr.steps || []).length >= 2, `steps=${JSON.stringify((wr.steps || []).map(s => s.tool))}`);
check('回复体现真实执行结果（不是推测性话术）',
  /已创建文件|已提交人工审批|执行未成功/.test(wr.reply || ''), (wr.reply || '').slice(0, 140));

//覆盖同一文件直接执行
const overwrite = await chat(employee, '把 周报.txt 覆盖成：第二版要点');
const ow = overwrite.body?.data || {};
check('私人区覆盖普通文本直接执行（有快照可回退）', ow.status === 'EXECUTED',
  `status=${ow.status} reply=${(ow.reply || '').slice(0, 120)}`);

//审计与可追溯
const verify = await call('GET', '/api/admin/audit/verify', { token: admin.token });
check('审计链依旧完整', verify.body?.data?.ok === true, JSON.stringify(verify.body?.data));

const aiPlans = await call('GET', '/api/admin/audit?limit=50&action=ai.plan', { token: admin.token });
const manySteps = (aiPlans.body?.data || []).some(e => (e.detail || '').includes('第2步'));
check('审计里能看到多步轨迹（可追溯）', manySteps,
  (aiPlans.body?.data || []).slice(0, 2).map(e => e.detail).join(' | ').slice(0, 160));

console.log('--------------------------------------------------------------');
console.log(`多步代理验收：通过 ${pass} 项，失败 ${fail} 项`);
if (failures.length) { console.log('失败项：'); failures.forEach(f => console.log('  - ' + f)); }
process.exit(fail === 0 ? 0 : 1);
