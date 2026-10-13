//飞书用户表账号同步验收，配合 mock-feishu.mjs
//用法：node e2e-feishu-sync.mjs [服务地址] [假飞书地址]
import crypto from 'node:crypto';

const BASE = process.argv[2] || 'http://127.0.0.1:8990';
const MOCK = process.argv[3] || 'http://127.0.0.1:8991';
const USER_TABLE = 'tbl_mock';

let pass = 0, fail = 0; const bad = [];
//断言并累计通过/失败
function check(n, c, e = '') { if (c) { pass++; console.log(`  [PASS] ${n}`); } else { fail++; bad.push(n + (e ? ' -> ' + e : '')); console.log(`  [FAIL] ${n} ${e}`); } }

//生成请求签名头
function sign(m, p, b, k) {
  const ts = Date.now().toString();
  const nonce = crypto.randomBytes(18).toString('hex');
  const h = crypto.createHash('sha256').update(b ?? '', 'utf8').digest('hex');
  const sig = crypto.createHmac('sha256', k).update([m, p, ts, nonce, h].join('\n'), 'utf8').digest('base64');
  return { ts, nonce, sig };
}

//发请求并解析 JSON
async function call(m, p, { token, signKey, body } = {}) {
  const bt = body === undefined ? null : JSON.stringify(body);
  const h = { Accept: 'application/json' };
  if (token) h.Authorization = 'Bearer ' + token;
  if (bt !== null) h['Content-Type'] = 'application/json';
  if (signKey && ['POST', 'PUT', 'PATCH', 'DELETE'].includes(m)) {
    const s = sign(m, p, bt ?? '', signKey);
    h['X-Timestamp'] = s.ts; h['X-Nonce'] = s.nonce; h['X-Signature'] = s.sig;
  }
  const r = await fetch(BASE + p, { method: m, headers: h, body: bt === null ? undefined : bt, redirect: 'manual' });
  const t = await r.text();
  let j = null; try { j = t ? JSON.parse(t) : null; } catch { j = { raw: t.slice(0, 200) }; }
  return { status: r.status, body: j };
}

//登录取 token 与签名密钥
async function login(u, p) {
  const r = await call('POST', '/api/auth/login', { body: { userName: u, password: p } });
  return r.status === 200
    ? { token: r.body.data.token, signKey: r.body.data.signKey, user: r.body.data.user }
    : { status: r.status, error: r.body?.message || '' };
}

//调用账号同步接口
async function sync(session, dryRun, reason) {
  return call('POST', '/api/admin/users/sync-from-feishu', {
    token: session.token, signKey: session.signKey, body: { dryRun, reason },
  });
}

//对齐对面前端口令哈希口径
function feishuHash(password) {
  const salt = crypto.randomBytes(16);
  const digest = crypto.pbkdf2Sync(password, salt, 100000, 32, 'sha256');
  return { saltHex: salt.toString('hex'), hashHex: digest.toString('hex') };
}

//往假飞书写一行
async function seedRow(recordId, fields) {
  const r = await fetch(MOCK + '/__mock/seed', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ tableId: USER_TABLE, recordId, fields }),
  });
  return r.status === 200;
}

//切换假飞书模式
async function mockMode(mode) {
  await fetch(MOCK + '/__mock/mode', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(mode),
  });
}

//清空假飞书再预置数据
async function mockReset() {
  const r = await fetch(MOCK + '/__mock/reset', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' });
  return r.status === 200;
}

//预置对面数据
const PWD_EMP = 'Monkey#2026abc';
const PWD_FIN = 'Pig#2026abcdef';
const PWD_ADM = 'Sand#2026abcdef';
const PWD_OFF = 'Steady#2026abc';
const PWD_EMP2 = 'Horse#2026abcd';
const PWD_ADM2 = 'Ox#2026abcdefgh';

const hEmp = feishuHash(PWD_EMP);
const hFin = feishuHash(PWD_FIN);
const hAdm = feishuHash(PWD_ADM);
const hOff = feishuHash(PWD_OFF);
const hEmp2 = feishuHash(PWD_EMP2);
const hAdm2 = feishuHash(PWD_ADM2);
const hRootFake = feishuHash('WhoAmI#2026abcd');

const MS_NOW = Date.now();

check('清空假飞书用户表', await mockReset());
//限制假飞书每页三行
await mockMode({ pageLimit: 3 });

const seeded = [];
seeded.push(await seedRow('recFsEmpA', {
  工号: 'E0101', 姓名: '孙悟空', 用户名: 'feishu_emp', 角色: '员工',
  密码哈希: hEmp.hashHex, 盐: hEmp.saltHex, 账号状态: '启用', 创建时间: MS_NOW,
}));
seeded.push(await seedRow('recFsFinB', {
  工号: 'E0102', 姓名: '猪悟能', 用户名: 'feishu_fin', 角色: '财务',
  密码哈希: hFin.hashHex, 盐: hFin.saltHex, 账号状态: '启用', 创建时间: MS_NOW,
}));
seeded.push(await seedRow('recFsAdmC', {
  工号: '', 姓名: '沙悟净', 用户名: 'feishu_adm', 角色: '管理员',
  密码哈希: hAdm.hashHex, 盐: hAdm.saltHex, 账号状态: '启用', 创建时间: MS_NOW,
}));
seeded.push(await seedRow('recFsNoPwdD', {
  工号: '', 姓名: '无凭据甲', 用户名: 'feishu_nopwd', 角色: '员工', 账号状态: '启用',
}));
seeded.push(await seedRow('recFsOffE', {
  工号: '', 姓名: '停用乙', 用户名: 'feishu_off', 角色: '员工',
  密码哈希: hOff.hashHex, 盐: hOff.saltHex, 账号状态: '停用',
}));
seeded.push(await seedRow('recFsRootF', {
  工号: 'E0004', 姓名: '冒名顶替', 用户名: 'root', 角色: '管理员',
  密码哈希: hRootFake.hashHex, 盐: hRootFake.saltHex, 账号状态: '启用',
}));
seeded.push(await seedRow('recFsNoNameG', {
  工号: 'E0999', 姓名: '没有用户名', 用户名: '', 角色: '员工', 账号状态: '启用',
}));
seeded.push(await seedRow('recFsDupH', {
  工号: 'E0101', 姓名: '重名丙', 用户名: 'feishu_emp', 角色: '员工',
  密码哈希: hEmp.hashHex, 盐: hEmp.saltHex, 账号状态: '启用',
}));
check('假飞书用户表预置八行', seeded.every(Boolean));

//登录与只读预览
const root = await login('root', 'Overlord#2026ak');
const adm = await login('admin', 'SysRoot#2026ak');
const emp = await login('employee', 'Staff#2026abc');
check('超管/管理员/员工均可登录', !!root.token && !!adm.token && !!emp.token);

const rowsView = await call('GET', '/api/admin/users/feishu-rows', { token: root.token });
check('只读预览可读', rowsView.status === 200 && rowsView.body?.data?.configured === true, `status=${rowsView.status}`);
check('预览列出对面八行（每页三行，需翻页读全）', (rowsView.body?.data?.rows || []).length === 8, String(rowsView.body?.data?.count));
const mockState = await (await fetch(MOCK + '/__mock/state')).json();
check('服务端确实发起了翻页请求', (mockState.data?.counters?.list || 0) >= 2, JSON.stringify(mockState.data?.counters));
const previewEmp = (rowsView.body?.data?.rows || []).find(r => r.userName === 'feishu_emp');
check('预览标明对面有口令材料', previewEmp?.hasPassword === true && previewEmp?.existsLocally === false, JSON.stringify(previewEmp));
check('预览标明 root 在本机已存在', (rowsView.body?.data?.rows || []).find(r => r.userName === 'root')?.existsLocally === true);
check('预览不泄露口令哈希', !JSON.stringify(rowsView.body).includes(hEmp.hashHex) && !JSON.stringify(rowsView.body).includes('密码哈希'));

const empPreview = await call('GET', '/api/admin/users/feishu-rows', { token: emp.token });
check('员工无权预览账号表（403）', empPreview.status === 403, `status=${empPreview.status}`);

const empSync = await sync(emp, false, '员工越权同步测试');
check('员工无权执行同步（403）', empSync.status === 403, `status=${empSync.status} msg=${empSync.body?.message}`);

//预览同步不落盘
const dry = await sync(root, true, '先预览再决定');
check('预览同步返回成功', dry.status === 200 && dry.body?.data?.dryRun === true, `status=${dry.status} msg=${dry.body?.message}`);
check('预览统计：读到 8、新建 5、已存在 1、跳过 2', dry.body?.data?.read === 8 && dry.body?.data?.created === 5
  && dry.body?.data?.exists === 1 && dry.body?.data?.skipped === 2,
  JSON.stringify({ read: dry.body?.data?.read, created: dry.body?.data?.created, exists: dry.body?.data?.exists, skipped: dry.body?.data?.skipped }));
check('预览标出无口令账号', dry.body?.data?.needsPasswordReset === 1, String(dry.body?.data?.needsPasswordReset));

const afterDry = await login('feishu_emp', PWD_EMP);
check('预览不写入：预览后账号仍无法登录', !afterDry.token, JSON.stringify(afterDry.user || afterDry.error));

//执行同步
const real = await sync(root, false, '首次开户导入对面账号');
check('执行同步成功', real.status === 200 && real.body?.data?.dryRun === false, `status=${real.status} msg=${real.body?.message}`);
check('同步统计与预览一致', real.body?.data?.created === 5 && real.body?.data?.exists === 1
  && real.body?.data?.skipped === 2 && real.body?.data?.adopted === 0,
  JSON.stringify({ created: real.body?.data?.created, exists: real.body?.data?.exists, skipped: real.body?.data?.skipped }));

const items = real.body?.data?.items || [];
const itemOf = n => items.find(i => i.userName === n);
check('无用户名的那行被跳过', itemOf('')?.action === 'skipped', JSON.stringify(itemOf('')));
check('对面重名的第二行被跳过', items.filter(i => i.userName === 'feishu_emp').some(i => i.action === 'skipped'),
  JSON.stringify(items.filter(i => i.userName === 'feishu_emp').map(i => i.action)));
check('root 记为本机已存在（不覆盖）', itemOf('root')?.action === 'exists', JSON.stringify(itemOf('root')));
check('无口令账号标记需重置口令', itemOf('feishu_nopwd')?.needsPasswordReset === true, JSON.stringify(itemOf('feishu_nopwd')));

//导入后可用原口令登录
const empAfter = await login('feishu_emp', PWD_EMP);
check('导入账号可用对面原口令登录', !!empAfter.token, JSON.stringify(empAfter.error));
check('导入账号角色映射为员工', empAfter.user?.role === 'employee', empAfter.user?.role);
check('导入账号沿用对面工号', empAfter.user?.employeeId === 'E0101', empAfter.user?.employeeId);

const finAfter = await login('feishu_fin', PWD_FIN);
check('财务账号可登录', !!finAfter.token && finAfter.user?.role === 'finance', JSON.stringify(finAfter.user));
const finMeta = await call('GET', '/api/invoice/meta', { token: finAfter.token });
check('导入的财务账号能看全部单据', finMeta.body?.data?.canSeeAll === true);

const admAfter = await login('feishu_adm', PWD_ADM);
check('对面的管理员映射成 admin（不是超管）', admAfter.user?.role === 'admin', admAfter.user?.role);
check('导入的管理员可进账号列表', (await call('GET', '/api/admin/users', { token: admAfter.token })).status === 200);
check('导入的管理员不能创建超管', (await call('POST', '/api/admin/users', {
  token: admAfter.token, signKey: admAfter.signKey,
  body: { userName: 'evilboss', password: 'Evil#2026abcde', displayName: '越权', role: 'superadmin' },
})).status === 403);
check('导入账号工号按模式补齐', /^E\d{4}$/.test(admAfter.user?.employeeId || ''), admAfter.user?.employeeId);

const noPwd = await login('feishu_nopwd', 'Whatever#2026ab');
check('对面没有口令哈希的账号登不进来', !noPwd.token, JSON.stringify(noPwd.user || noPwd.error));

const off = await login('feishu_off', PWD_OFF);
check('对面标停用的账号在本机同样是停用', !off.token, JSON.stringify(off.user || off.error));

const rootStill = await login('root', 'Overlord#2026ak');
check('本机 root 口令未被对面覆盖', !!rootStill.token);
const rootFake = await login('root', 'WhoAmI#2026abcd');
check('对面给 root 的假口令无法登录本机', !rootFake.token);

const usersAfter = await call('GET', '/api/admin/users', { token: root.token });
const list = usersAfter.body?.data || [];
const originOf = n => (list.find(u => u.userName === n) || {}).origin;
check('导入账号标记来源 feishu-sync', originOf('feishu_emp') === 'feishu-sync' && originOf('feishu_adm') === 'feishu-sync');
check('本机原有账号来源仍为 local', originOf('root') === 'local', String(originOf('root')));

//登录后升级为本机口令方案
const schemeOf = n => (list.find(u => u.userName === n) || {}).passwordScheme;
const itersOf = n => (list.find(u => u.userName === n) || {}).passwordIterations;
check('导入账号登录后已升级为本机口令方案',
  schemeOf('feishu_emp') === 'pbkdf2-sha256+pepper' && schemeOf('feishu_fin') === 'pbkdf2-sha256+pepper',
  `emp=${schemeOf('feishu_emp')} fin=${schemeOf('feishu_fin')}`);
check('升级后的迭代次数取服务端配置', itersOf('feishu_emp') === 120000, String(itersOf('feishu_emp')));
check('升级之后原口令仍然可用', !!(await login('feishu_emp', PWD_EMP)).token);
check('对面没给口令的账号标记为无口令', schemeOf('feishu_nopwd') === 'none', String(schemeOf('feishu_nopwd')));

//重复同步保持幂等
const again = await sync(root, false, '重复同步应无新增');
check('重复同步不新建账号', again.body?.data?.created === 0 && again.body?.data?.adopted === 0,
  JSON.stringify({ created: again.body?.data?.created, adopted: again.body?.data?.adopted }));
check('重复同步统计为已存在 6 行', again.body?.data?.exists === 6, String(again.body?.data?.exists));

//再次同步认领本机账号
const localRows = await call('GET', '/api/admin/users/feishu-rows', { token: root.token });
check('同步后预览显示账号已存在本机', (localRows.body?.data?.rows || []).filter(r => r.existsLocally).length === 7,
  String((localRows.body?.data?.rows || []).filter(r => r.existsLocally).length));

//管理员同步不能造管理员
const hNew = feishuHash(PWD_EMP2);
await seedRow('recFsEmp2', {
  工号: '', 姓名: '白龙马', 用户名: 'feishu_emp2', 角色: '员工',
  密码哈希: hNew.hashHex, 盐: hNew.saltHex, 账号状态: '启用',
});
await seedRow('recFsAdm2', {
  工号: '', 姓名: '越权甲', 用户名: 'feishu_adm2', 角色: '管理员',
  密码哈希: hAdm2.hashHex, 盐: hAdm2.saltHex, 账号状态: '启用',
});

const admSync = await sync(adm, false, '普通管理员同步：只应补员工');
check('普通管理员同步成功但被逐行限制', admSync.status === 200 && admSync.body?.data?.created === 1,
  JSON.stringify({ created: admSync.body?.data?.created, skipped: admSync.body?.data?.skipped }));
const adm2Item = (admSync.body?.data?.items || []).find(i => i.userName === 'feishu_adm2');
check('管理员账号被同步跳过并写明原因', adm2Item?.action === 'skipped' && (adm2Item?.detail || '').includes('超级管理员'),
  JSON.stringify(adm2Item));
check('普通管理员同步出的员工可登录', !!(await login('feishu_emp2', PWD_EMP2)).token);
check('对面的管理员没被普通管理员导入', !(await login('feishu_adm2', PWD_ADM2)).token);

//对面读取失败时的行为
await mockMode({ fail: true });
const broken = await sync(root, false, '对面故障时的同步');
check('对面读取失败时返回 409 而不是写坏数据', broken.status === 409 && broken.body?.code === 'SYNC_UNAVAILABLE',
  `status=${broken.status} code=${broken.body?.code} msg=${broken.body?.message}`);
await mockMode({ fail: false });

const recovered = await sync(root, true, '对面恢复后的预览');
check('对面恢复后同步恢复正常', recovered.status === 200, `status=${recovered.status}`);

//审计与前端链接
const audit = await call('GET', '/api/admin/audit?action=user.sync-feishu&limit=50', { token: root.token });
const auditRows = audit.body?.data || [];
check('同步写入审计', auditRows.length >= 4, String(auditRows.length));
check('审计区分预览与执行', auditRows.some(r => r.outcome === 'PREVIEW') && auditRows.some(r => r.outcome === 'SUCCESS'),
  JSON.stringify(auditRows.map(r => r.outcome)));
check('审计记录了调用者', auditRows.every(r => r.actorName === 'root' || r.actorName === 'admin'),
  JSON.stringify(auditRows.map(r => r.actorName)));

const meta = await call('GET', '/api/invoice/meta', { token: root.token });
const tableUrl = meta.body?.data?.tableUrl || '';
const userTableUrl = meta.body?.data?.userTableUrl || '';
check('元数据下发报销单原表地址', tableUrl.startsWith('https://') && tableUrl.includes('tbl_invoice'), tableUrl);
check('元数据下发用户表原表地址', userTableUrl.startsWith('https://') && userTableUrl.includes('tbl_mock'), userTableUrl);

const verify = await call('GET', '/api/admin/audit/verify', { token: root.token });
check('审计哈希链完好', verify.body?.data?.ok === true, JSON.stringify(verify.body?.data));

console.log('');
if (fail === 0) {
  console.log(`飞书账号同步验收：${pass} 项全部通过`);
  process.exit(0);
}
console.log(`飞书账号同步验收：通过 ${pass} 项，失败 ${fail} 项`);
for (const b of bad) console.log('  - ' + b);
process.exit(1);
