//账号权限与飞书镜像端到端验收套件
import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const BASE = process.argv[2] || 'http://127.0.0.1:8990';
const MOCK = process.argv[3] || 'http://127.0.0.1:8991';

//镜像哈希的迭代次数
const MIRROR_PBKDF2_ITERATIONS = 100000;

//演示账号，四角色各一
const SEED = {
  employee: { userName: 'employee', password: 'Staff#2026abc', role: 'employee', employeeId: 'E0001' },
  finance: { userName: 'finance', password: 'Money#2026abc', role: 'finance', employeeId: 'E0002' },
  admin: { userName: 'admin', password: 'SysRoot#2026ak', role: 'admin', employeeId: 'E0003' },
  root: { userName: 'root', password: 'Overlord#2026ak', role: 'superadmin', employeeId: 'E0004' },
};

const testsDir = path.dirname(fileURLToPath(import.meta.url));
const repoDir = path.dirname(testsDir);
const serverExeCandidates = [
  path.join(repoDir, 'AiApprovalServer', 'bin', 'Debug', 'net8.0', 'AiApprovalServer.exe'),
  path.join(repoDir, 'AiApprovalServer', 'bin', 'Debug', 'net8.0', 'AiApprovalServer'),
];

let pass = 0;
let fail = 0;
const failures = [];
const blocked = [];

//转成截断后的短文本
function brief(value, max) {
  const limit = typeof max === 'number' ? max : 160;
  let text;
  try { text = JSON.stringify(value); } catch { text = String(value); }
  if (typeof text !== 'string') text = String(value);
  return text.length > limit ? text.slice(0, limit) : text;
}

//记一条断言结果
function check(name, condition, extra = '') {
  if (condition) {
    pass++;
    console.log(`  [PASS] ${name}`);
  } else {
    fail++;
    failures.push(name + (extra ? ' -> ' + extra : ''));
    console.log(`  [FAIL] ${name} ${extra}`);
  }
}

//前置不可用导致的失败，单独归类
function checkBlocked(name, reason) {
  fail++;
  blocked.push(name + ' -> ' + reason);
  failures.push(name + ' -> ' + reason);
  console.log(`  [BLOCKED] ${name} ${reason}`);
}

//算一次签名
function sign(method, pathWithQuery, bodyText, signKey) {
  const ts = Date.now().toString();
  const nonce = crypto.randomBytes(18).toString('hex');
  const bodyHash = crypto.createHash('sha256').update(bodyText ?? '', 'utf8').digest('hex');
  const canonical = [method, pathWithQuery, ts, nonce, bodyHash].join('\n');
  const sig = crypto.createHmac('sha256', signKey).update(canonical, 'utf8').digest('base64');
  return { ts, nonce, sig };
}

//发一次请求，按需带令牌与签名头
async function call(method, requestPath, { token, signKey, body, headers = {}, base } = {}) {
  const bodyText = body === undefined ? null : JSON.stringify(body);
  const finalHeaders = { Accept: 'application/json', ...headers };
  if (token) finalHeaders.Authorization = 'Bearer ' + token;
  if (bodyText !== null && !finalHeaders['Content-Type']) finalHeaders['Content-Type'] = 'application/json';
  if (signKey && ['POST', 'PUT', 'PATCH', 'DELETE'].includes(method)) {
    const s = sign(method, requestPath, bodyText ?? '', signKey);
    finalHeaders['X-Timestamp'] = s.ts;
    finalHeaders['X-Nonce'] = s.nonce;
    finalHeaders['X-Signature'] = s.sig;
  }

  const res = await fetch((base || BASE) + requestPath, {
    method,
    headers: finalHeaders,
    body: bodyText === null ? undefined : bodyText,
    redirect: 'manual',
  });
  const text = await res.text();
  let json = null;
  try { json = text ? JSON.parse(text) : null; } catch { json = { raw: text.slice(0, 200) }; }
  return { status: res.status, body: json, text };
}

//登录并取令牌与签名密钥
async function login(userName, password) {
  const res = await call('POST', '/api/auth/login', { body: { userName, password } });
  if (res.status !== 200) return { error: res.body, status: res.status };
  return {
    token: res.body.data.token,
    signKey: res.body.data.signKey,
    user: res.body.data.user,
    permissions: (res.body.data.user && res.body.data.user.permissions) || [],
  };
}

//调假飞书的观测接口
async function mockCall(method, requestPath, body) {
  const bodyText = body === undefined ? null : JSON.stringify(body);
  const res = await fetch(MOCK + requestPath, {
    method,
    headers: bodyText === null ? {} : { 'Content-Type': 'application/json' },
    body: bodyText === null ? undefined : bodyText,
  });
  const text = await res.text();
  try { return text ? JSON.parse(text) : null; } catch { return { raw: text.slice(0, 300) }; }
}

//按 PBKDF2 算密码哈希
function pbkdf2Hex(password, saltHex, iterations) {
  return crypto.pbkdf2Sync(password, Buffer.from(saltHex, 'hex'), iterations, 32, 'sha256').toString('hex');
}

//查指定动作的审计记录
async function auditEntries(session, action) {
  const res = await call('GET', `/api/admin/audit?action=${encodeURIComponent(action)}&limit=1000`, { token: session.token });
  //查询失败要显式报出来
  if (res.status !== 200) {
    console.log(`  [WARN] 审计查询 ${action} 返回 ${res.status}：${JSON.stringify(res.body).slice(0, 160)}`);
  }
  return (res.body && res.body.data) || [];
}

//在假飞书表里按用户名找行
function findMockRow(state, userName) {
  const tables = (state && state.data && state.data.tables) || {};
  for (const rows of Object.values(tables)) {
    for (const row of rows) {
      if (row.fields && row.fields['用户名'] === userName) return row;
    }
  }
  return null;
}

//轮询等健康检查返回 200
async function waitForHealth(port, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const res = await fetch(`http://127.0.0.1:${port}/api/public/health`);
      if (res.status === 200) return true;
    } catch { /* 服务还没起来，继续等 */ }
    await new Promise(r => setTimeout(r, 500));
  }
  return false;
}

const sessions = {};

//主流程，跑完整套用例
async function main() {
  console.log('== 环境与播种账号 ==');
  const health = await call('GET', '/api/public/health');
  check('被测服务可访问（健康检查 200）', health.status === 200 && health.body?.data?.status === 'ok',
    `status=${health.status}`);

  const mockState = await mockCall('GET', '/__mock/state');
  check('假飞书可访问（镜像观测端点可用）', !!mockState?.data,
    `mock=${MOCK} resp=${brief(mockState)}`);
  await mockCall('POST', '/__mock/reset');
  await mockCall('POST', '/__mock/mode', { fail: false, failFirst: 0 });

  for (const key of Object.keys(SEED)) {
    sessions[key] = await login(SEED[key].userName, SEED[key].password);
  }

  const missing = Object.keys(SEED).filter(k => !sessions[k].token);
  if (missing.length) {
    console.log('      播种账号当前不可用：' + missing.map(k => `${SEED[k].userName}(${sessions[k].status})`).join('、'));
    missing.forEach(k => console.log(`        ${SEED[k].userName} 登录返回：${brief(sessions[k].error)}`));
  }

  for (const key of Object.keys(SEED)) {
    const spec = SEED[key];
    check(`演示账号 ${spec.userName}（${spec.role}）可登录并返回正确角色`,
      !!sessions[key].token && sessions[key].user?.role === spec.role,
      `status=${sessions[key].status ?? 200} role=${brief(sessions[key].user?.role)} err=${brief(sessions[key].error)}`);
  }

  for (const key of Object.keys(SEED)) {
    const s = sessions[key];
    if (!s.token) continue;
    check(`登录返回带权限清单（${key}）`, Array.isArray(s.permissions) && s.permissions.length > 0,
      `permissions=${brief(s.permissions, 120)}`);
    check(`登录返回带工号 employeeId 且形如 E0001（${key}）`, /^E\d{4}$/.test(s.user?.employeeId || ''),
      `employeeId=${brief(s.user?.employeeId)}（当前登录响应字段：${Object.keys(s.user || {}).join(',')}）`);
    check(`演示账号工号与播种顺序一致（${key} = ${SEED[key].employeeId}）`,
      s.user?.employeeId === SEED[key].employeeId,
      `expected=${SEED[key].employeeId} actual=${brief(s.user?.employeeId)}`);
  }

  const adminToken = sessions.admin.token;
  const adminSign = sessions.admin.signKey;

  const userList = adminToken ? await call('GET', '/api/admin/users', { token: adminToken }) : { status: 0, body: null };
  check('管理员可读取账号列表', userList.status === 200 && Array.isArray(userList.body?.data),
    `status=${userList.status}`);
  const usersBefore = userList.body?.data || [];
  check('账号列表带工号且为 E0001 格式', usersBefore.some(u => /^E\d{4}$/.test(u.employeeId || '')),
    `employeeIds=${brief(usersBefore.map(u => u.employeeId))}`);

  console.log('\n== 登录态自述信息 ==');
  if (sessions.admin.token) {
    const me = await call('GET', '/api/auth/me', { token: sessions.admin.token });
    check('/api/auth/me 返回当前身份', me.status === 200 && me.body?.data?.userName === 'admin',
      `status=${me.status} body=${brief(me.body)}`);
    check('/api/auth/me 带工号与权限清单',
      /^E\d{4}$/.test(me.body?.data?.employeeId || '') && (me.body?.data?.permissions || []).length > 0,
      `employeeId=${brief(me.body?.data?.employeeId)} permissions=${(me.body?.data?.permissions || []).length}`);
  } else {
    checkBlocked('/api/auth/me 返回当前身份', '前置不可用：admin 账号未就绪');
    checkBlocked('/api/auth/me 带工号与权限清单', '前置不可用：admin 账号未就绪');
  }

  console.log('\n== 注册接口的准入控制 ==');
  const anonymousRegister = await call('POST', '/api/auth/register', {
    body: { userName: 'nobody01', password: 'Nobody#2026abc', displayName: '匿名' },
  });
  check('未登录调用注册接口被拒（401）', anonymousRegister.status === 401,
    `status=${anonymousRegister.status} body=${brief(anonymousRegister.body, 120)}`);

  for (const key of ['finance', 'employee']) {
    const s = sessions[key];
    if (!s.token) {
      checkBlocked(`${key} 角色调用注册接口被拒（403）`, `前置不可用：${key} 账号未就绪`);
      checkBlocked(`${key} 注册被拒文案来自 AccountPolicy`, `前置不可用：${key} 账号未就绪`);
      checkBlocked(`${key} 越权注册留下 DENIED 审计`, `前置不可用：${key} 账号未就绪`);
      continue;
    }
    const attemptUser = key + '_reg01';
    const res = await call('POST', '/api/auth/register', {
      token: s.token, signKey: s.signKey,
      body: { userName: attemptUser, password: 'TryReg#2026abc', displayName: '越权试注册' },
    });
    check(`${key} 角色调用注册接口被拒（403）`, res.status === 403, `status=${res.status}`);
    check(`${key} 注册被拒文案来自 AccountPolicy`, (res.body?.message || '') === '当前角色无权创建账号',
      `message=${brief(res.body?.message)}`);
    const denied = await auditEntries(sessions.admin, 'auth.register');
    check(`${key} 越权注册留下 DENIED 审计`,
      denied.some(e => e.target === attemptUser && e.outcome === 'DENIED'),
      `audit=${brief(denied.slice(0, 3).map(e => e.target + ':' + e.outcome))}`);
  }

  console.log('\n== 创建账号与角色授予 ==');
  const createEmployee = await call('POST', '/api/auth/register', {
    token: adminToken, signKey: adminSign,
    body: { userName: 'newstaff01', password: 'NewStaff#2026abc', displayName: '新员工甲' },
  });
  check('管理员可创建员工账号', createEmployee.status === 200,
    `status=${createEmployee.status} body=${brief(createEmployee.body)}`);
  const newStaffId = createEmployee.body?.data?.employeeId || '';
  check('新账号工号由服务端按 E0001 格式分配', /^E\d{4}$/.test(newStaffId), `employeeId=${brief(newStaffId)}`);

  const maxBefore = usersBefore
    .map(u => Number((u.employeeId || '').replace(/^E/, '')))
    .filter(n => Number.isFinite(n))
    .reduce((a, b) => Math.max(a, b), 0);
  check('工号在上一个最大号基础上递增', Number(newStaffId.replace(/^E/, '')) === maxBefore + 1,
    `before=${maxBefore} got=${brief(newStaffId)}`);

  const adminCreateAdmin = await call('POST', '/api/auth/register', {
    token: adminToken, signKey: adminSign,
    body: { userName: 'newadmin01', password: 'Operator#2026ak', displayName: '新管理员甲', role: 'admin' },
  });
  check('管理员创建管理员账号被拒（403）', adminCreateAdmin.status === 403, `status=${adminCreateAdmin.status}`);
  check('越权创建管理员被拒文案来自 AccountPolicy',
    (adminCreateAdmin.body?.message || '') === '只有超级管理员可以创建管理员账号',
    `message=${brief(adminCreateAdmin.body?.message)}`);

  const adminCreateSuper = await call('POST', '/api/auth/register', {
    token: adminToken, signKey: adminSign,
    body: { userName: 'newsuper01', password: 'NewSuper#2026ak', displayName: '新超管', role: 'superadmin' },
  });
  check('创建超级管理员账号被拒（只能系统初始化）', adminCreateSuper.status === 403,
    `status=${adminCreateSuper.status} message=${brief(adminCreateSuper.body?.message)}`);

  let newAdminName = '';
  if (sessions.root.token) {
    const rootCreateAdmin = await call('POST', '/api/auth/register', {
      token: sessions.root.token, signKey: sessions.root.signKey,
      body: { userName: 'newadmin02', password: 'Operator#2026ak', displayName: '新管理员乙', role: 'admin' },
    });
    check('超级管理员创建管理员账号成功', rootCreateAdmin.status === 200,
      `status=${rootCreateAdmin.status} body=${brief(rootCreateAdmin.body)}`);
    newAdminName = rootCreateAdmin.body?.data?.userName || '';
  } else {
    checkBlocked('超级管理员创建管理员账号成功', '前置不可用：root（superadmin）账号未就绪');
  }

  const usersAfterCreate = await call('GET', '/api/admin/users', { token: adminToken });
  const allUsers = usersAfterCreate.body?.data || [];
  const targetEmployeeUserId = allUsers.find(u => u.userName === 'newstaff01')?.id || '';
  const adminUserId = allUsers.find(u => u.userName === 'admin')?.id || '';
  const rootUserId = allUsers.find(u => u.userName === 'root')?.id || '';

  console.log('\n== 变更角色的人际约束 ==');
  const selfRole = await call('POST', `/api/admin/users/${adminUserId}/role`, {
    token: adminToken, signKey: adminSign, body: { role: 'finance', reason: '自测自我提权' },
  });
  check('管理员修改自己的角色被拒（403）', selfRole.status === 403, `status=${selfRole.status}`);
  check('拒绝文案为"不能修改自己的角色"', (selfRole.body?.message || '') === '不能修改自己的角色（避免自我提权）',
    `message=${brief(selfRole.body?.message)}`);

  const promoteOther = await call('POST', `/api/admin/users/${targetEmployeeUserId}/role`, {
    token: adminToken, signKey: adminSign, body: { role: 'admin', reason: '普通管理员尝试提权他人' },
  });
  check('管理员把他人提升为管理员被拒（403）', promoteOther.status === 403, `status=${promoteOther.status}`);
  check('拒绝文案为"只有超级管理员可以授予或撤销管理员/超级管理员"',
    (promoteOther.body?.message || '') === '只有超级管理员可以授予或撤销管理员/超级管理员',
    `message=${brief(promoteOther.body?.message)}`);

  const roleDeniedAudit = await auditEntries(sessions.admin, 'user.role');
  check('越权改角色留下 DENIED 审计', roleDeniedAudit.some(e => e.outcome === 'DENIED'),
    `audit=${brief(roleDeniedAudit.slice(0, 3).map(e => e.target + ':' + e.outcome))}`);

  const newStaffLogin = await login('newstaff01', 'NewStaff#2026abc');
  check('新建的员工账号可登录', !!newStaffLogin.token, brief(newStaffLogin.error));
  const newStaffTokenBefore = newStaffLogin.token;

  if (sessions.root.token) {
    const changeRole = await call('POST', `/api/admin/users/${targetEmployeeUserId}/role`, {
      token: sessions.root.token, signKey: sessions.root.signKey,
      body: { role: 'finance', reason: '转岗到财务部' },
    });
    check('超级管理员变更角色成功', changeRole.status === 200,
      `status=${changeRole.status} body=${brief(changeRole.body)}`);

    const stale = await call('GET', '/api/auth/me', { token: newStaffTokenBefore });
    check('角色变更后旧令牌立即失效（401 TOKEN_STALE）',
      stale.status === 401 && ['TOKEN_STALE', 'BAD_TOKEN'].includes(stale.body?.code),
      `status=${stale.status} code=${brief(stale.body?.code)}`);
  } else {
    checkBlocked('超级管理员变更角色成功', '前置不可用：root（superadmin）账号未就绪');
    checkBlocked('角色变更后旧令牌立即失效（401 TOKEN_STALE）', '前置不可用：root（superadmin）账号未就绪');
  }

  console.log('\n== 停用与启用 ==');
  const targetLogin = await login('newstaff01', 'NewStaff#2026abc');
  const targetToken = targetLogin.token;
  const disableRes = await call('POST', `/api/admin/users/${targetEmployeeUserId}/status`, {
    token: adminToken, signKey: adminSign, body: { disabled: true, reason: '离职流程停用' },
  });
  check('管理员停用账号成功', disableRes.status === 200,
    `status=${disableRes.status} body=${brief(disableRes.body, 120)}`);

  const disabledLogin = await login('newstaff01', 'NewStaff#2026abc');
  check('停用后该账号登录被拒（401 AUTH_FAILED）',
    disabledLogin.status === 401 && disabledLogin.error?.code === 'AUTH_FAILED',
    `status=${disabledLogin.status} code=${brief(disabledLogin.error?.code)}`);
  check('停用文案为"该账号已被禁用，请联系管理员"',
    (disabledLogin.error?.message || '') === '该账号已被禁用，请联系管理员',
    `message=${brief(disabledLogin.error?.message)}`);

  const staleAfterDisable = await call('GET', '/api/auth/me', { token: targetToken });
  check('停用后旧令牌立即失效', staleAfterDisable.status === 401,
    `status=${staleAfterDisable.status} code=${brief(staleAfterDisable.body?.code)}`);

  const enableRes = await call('POST', `/api/admin/users/${targetEmployeeUserId}/status`, {
    token: adminToken, signKey: adminSign, body: { disabled: false, reason: '复职启用' },
  });
  check('管理员重新启用账号成功', enableRes.status === 200, `status=${enableRes.status}`);
  const reEnabledLogin = await login('newstaff01', 'NewStaff#2026abc');
  check('启用后可再次登录', !!reEnabledLogin.token, brief(reEnabledLogin.error));

  console.log('\n== 重置口令 ==');
  const newPassword = 'Rotated#2026abc';
  const resetRes = await call('POST', `/api/admin/users/${targetEmployeeUserId}/reset-password`, {
    token: adminToken, signKey: adminSign, body: { newPassword, reason: '员工遗忘口令' },
  });
  check('管理员重置他人口令成功', resetRes.status === 200,
    `status=${resetRes.status} body=${brief(resetRes.body, 120)}`);

  const oldPasswordLogin = await login('newstaff01', 'NewStaff#2026abc');
  check('重置后旧口令失效（401）', oldPasswordLogin.status === 401, `status=${oldPasswordLogin.status}`);
  const newPasswordLogin = await login('newstaff01', newPassword);
  check('重置后新口令可登录', !!newPasswordLogin.token, brief(newPasswordLogin.error));

  const weakReset = await call('POST', `/api/admin/users/${targetEmployeeUserId}/reset-password`, {
    token: adminToken, signKey: adminSign, body: { newPassword: 'abcdefghij', reason: '弱口令应被拒' },
  });
  check('重置为弱口令被拒（400）', weakReset.status === 400,
    `status=${weakReset.status} body=${brief(weakReset.body, 120)}`);

  if (rootUserId) {
    const adminResetSuper = await call('POST', `/api/admin/users/${rootUserId}/reset-password`, {
      token: adminToken, signKey: adminSign, body: { newPassword: 'Takeover#2026ak', reason: '尝试接管超管口令' },
    });
    check('管理员重置超级管理员口令被拒（403）', adminResetSuper.status === 403, `status=${adminResetSuper.status}`);
    check('拒绝文案为"只有超级管理员可以重置超级管理员的口令"',
      (adminResetSuper.body?.message || '') === '只有超级管理员可以重置超级管理员的口令',
      `message=${brief(adminResetSuper.body?.message)}`);
  } else {
    checkBlocked('管理员重置超级管理员口令被拒（403）', '前置不可用：root（superadmin）账号未就绪');
    checkBlocked('拒绝文案为"只有超级管理员可以重置超级管理员的口令"', '前置不可用：root（superadmin）账号未就绪');
  }

  console.log('\n== 飞书镜像：成功路径 ==');
  await mockCall('POST', '/__mock/mode', { fail: false, failFirst: 0 });
  const mirroredUser = 'mirrorok01';
  const mirroredPassword = 'MirrorOk#2026abc';
  const mirrorOk = await call('POST', '/api/auth/register', {
    token: adminToken, signKey: adminSign,
    body: { userName: mirroredUser, password: mirroredPassword, displayName: '镜像成功用例' },
  });
  check('镜像可用时创建账号成功（200）', mirrorOk.status === 200, `status=${mirrorOk.status}`);
  check('镜像可用时 mirrorState=synced', mirrorOk.body?.data?.mirrorState === 'synced',
    `mirrorState=${brief(mirrorOk.body?.data?.mirrorState)} mirrorNote=${brief(mirrorOk.body?.data?.mirrorNote)}`);

  const demoPassword = mirrorOk.body?.data?.mirrorDemoPassword || '';
  check('demo 模式回传演示口令（便于交付协作方）', demoPassword.length >= 8, `demoPassword.length=${demoPassword.length}`);

  const mockAfterOk = await mockCall('GET', '/__mock/records');
  const row = findMockRow(mockAfterOk, mirroredUser);
  check('假飞书用户表里出现该账号的镜像行', !!row, `row=${brief(row, 200)}`);

  if (row) {
    const fields = row.fields || {};
    const saltHex = String(fields['盐'] || '');
    const hashHex = String(fields['密码哈希'] || '');
    check('镜像行角色是协作方三项枚举之一', ['员工', '财务', '管理员'].includes(fields['角色']),
      `角色=${brief(fields['角色'])}`);
    check('镜像行带工号', /^E\d{4}$/.test(fields['工号'] || ''), `镜像工号=${brief(fields['工号'])}`);
    check('镜像行账号状态为启用', fields['账号状态'] === '启用', `账号状态=${brief(fields['账号状态'])}`);
    check('镜像行密码哈希是演示口令的哈希（demo 模式）',
      demoPassword.length > 0 && hashHex.length === 64 && saltHex.length === 32
      && pbkdf2Hex(demoPassword, saltHex, MIRROR_PBKDF2_ITERATIONS) === hashHex,
      `salt=${saltHex.slice(0, 8)} hash=${hashHex.slice(0, 12)}`);
    check('镜像行密码哈希不等于真实口令的哈希',
      pbkdf2Hex(mirroredPassword, saltHex, MIRROR_PBKDF2_ITERATIONS) !== hashHex,
      '真实口令的哈希与镜像哈希相同，说明 demo 模式没有生效');
    check('镜像写入不含任何明文口令材料',
      !brief(fields, 4000).includes(mirroredPassword) && !brief(fields, 4000).includes(demoPassword),
      '镜像字段里出现了明文口令');
  } else {
    ['镜像行角色是协作方三项枚举之一', '镜像行带工号', '镜像行账号状态为启用',
      '镜像行密码哈希是演示口令的哈希（demo 模式）', '镜像行密码哈希不等于真实口令的哈希',
      '镜像写入不含任何明文口令材料'].forEach(name =>
        checkBlocked(name, '前置不可用：假飞书里没有找到镜像行'));
  }

  console.log('\n== 飞书镜像：传输层断连后必须重试并最终成功 ==');
  const retryUser = 'mirrortry01';
  await mockCall('POST', '/__mock/mode', { fail: false, failFirst: 1 });
  const retryState0 = await mockCall('GET', '/__mock/state');
  const brokenBefore = retryState0?.data?.counters?.broken ?? 0;
  const retryRegister = await call('POST', '/api/auth/register', {
    token: adminToken, signKey: adminSign,
    body: { userName: retryUser, password: 'MirrorTry#2026abc', displayName: '镜像重试用例' },
  });
  const retryState1 = await mockCall('GET', '/__mock/state');
  const brokenAfter = retryState1?.data?.counters?.broken ?? 0;
  check('假飞书确实断开过一次连接（故障注入生效）', brokenAfter === brokenBefore + 1,
    `broken before=${brokenBefore} after=${brokenAfter}`);
  check('业务连接被断开一次后，镜像凭重试仍然写入成功（mirrorState=synced）',
    retryRegister.status === 200 && retryRegister.body?.data?.mirrorState === 'synced',
    `status=${retryRegister.status} mirrorState=${brief(retryRegister.body?.data?.mirrorState)} mirrorNote=${brief(retryRegister.body?.data?.mirrorNote)}`);
  const retryRow = findMockRow(await mockCall('GET', '/__mock/records'), retryUser);
  check('重试成功后假飞书里确实有该账号的行', !!retryRow, `row=${brief(retryRow, 200)}`);
  await mockCall('POST', '/__mock/mode', { fail: false, failFirst: 0 });

  console.log('\n== 飞书镜像：失败路径（注册仍须成功） ==');
  await mockCall('POST', '/__mock/mode', { fail: true });
  const degradedUser = 'mirrorfail01';
  const degradedPassword = 'MirrorFail#2026abc';
  const mirrorFail = await call('POST', '/api/auth/register', {
    token: adminToken, signKey: adminSign,
    body: { userName: degradedUser, password: degradedPassword, displayName: '镜像失败用例' },
  });
  check('镜像失败时创建账号仍然成功（200）', mirrorFail.status === 200,
    `status=${mirrorFail.status} body=${brief(mirrorFail.body)}`);
  check('镜像失败时 mirrorState=failed', mirrorFail.body?.data?.mirrorState === 'failed',
    `mirrorState=${brief(mirrorFail.body?.data?.mirrorState)}`);
  check('镜像失败时 mirrorNote 说明原因', (mirrorFail.body?.data?.mirrorNote || '').length > 0,
    `mirrorNote=${brief(mirrorFail.body?.data?.mirrorNote)}`);

  const degradedLogin = await login(degradedUser, degradedPassword);
  check('镜像失败的账号仍可正常登录', !!degradedLogin.token, brief(degradedLogin.error));

  const mirrorFailAudit = await auditEntries(sessions.admin, 'feishu.mirror.failed');
  check('镜像失败写入审计 feishu.mirror.failed',
    mirrorFailAudit.some(e => String(e.detail || '').includes(degradedUser)),
    `audit=${brief(mirrorFailAudit.slice(0, 2).map(e => e.detail))}`);

  const listAfterFail = await call('GET', '/api/admin/users', { token: adminToken });
  const degradedRow = (listAfterFail.body?.data || []).find(u => u.userName === degradedUser);
  check('账号列表能看到镜像失败状态与错误摘要',
    degradedRow?.mirrorState === 'failed' && String(degradedRow?.mirrorError || '').length > 0,
    `mirrorState=${brief(degradedRow?.mirrorState)} mirrorError=${brief(degradedRow?.mirrorError, 120)}`);

  await mockCall('POST', '/__mock/mode', { fail: false, failFirst: 0 });

  console.log('\n== 外部账号：飞书表里有、本机 JSON 里没有 ==');
  const foreignUser = 'ghost_user';
  await mockCall('POST', '/__mock/seed', {
    fields: { 工号: 'E0900', 姓名: '幽灵账号', 用户名: foreignUser, 角色: '管理员', 账号状态: '启用' },
  });
  const foreignState = await mockCall('GET', '/__mock/records');
  check('假飞书里已预置外部账号行', !!findMockRow(foreignState, foreignUser));
  const foreignLogin = await login(foreignUser, 'Ghost#2026abc');
  check('飞书表里的外部账号无法登录本系统（401）', foreignLogin.status === 401,
    `status=${foreignLogin.status} body=${brief(foreignLogin.error, 120)}`);
  const usersNow = await call('GET', '/api/admin/users', { token: adminToken });
  check('外部账号没有进入本机账号列表',
    !(usersNow.body?.data || []).some(u => u.userName === foreignUser),
    `users=${brief((usersNow.body?.data || []).map(u => u.userName))}`);

  console.log('\n== 权限矩阵抽查 ==');
  for (const key of ['finance', 'employee']) {
    const s = sessions[key];
    if (!s.token) {
      checkBlocked(`${key} 角色访问账号列表被拒（403）`, `前置不可用：${key} 账号未就绪`);
      continue;
    }
    const res = await call('GET', '/api/admin/users', { token: s.token });
    check(`${key} 角色访问账号列表被拒（403）`, res.status === 403, `status=${res.status}`);
  }

  const adminUsersAgain = await call('GET', '/api/admin/users', { token: adminToken });
  check('管理员访问账号列表成功（200）', adminUsersAgain.status === 200, `status=${adminUsersAgain.status}`);

  if (sessions.root.token) {
    const rootUsers = await call('GET', '/api/admin/users', { token: sessions.root.token });
    check('超级管理员访问账号列表成功（200）', rootUsers.status === 200,
      `status=${rootUsers.status} body=${brief(rootUsers.body)}`);
    if (adminUserId) {
      const rootResetAdmin = await call('POST', `/api/admin/users/${adminUserId}/reset-password`, {
        token: sessions.root.token, signKey: sessions.root.signKey,
        body: { newPassword: 'SysNew#2026ak', reason: '超管为管理员重置口令' },
      });
      check('超级管理员重置管理员口令成功（200）', rootResetAdmin.status === 200,
        `status=${rootResetAdmin.status} body=${brief(rootResetAdmin.body)}`);

      //用新口令重新登录 admin
      const staleAfterRootReset = await call('GET', '/api/auth/me', { token: adminToken });
      check('超管重置管理员口令后，管理员旧令牌立即失效', staleAfterRootReset.status === 401,
        `status=${staleAfterRootReset.status} code=${brief(staleAfterRootReset.body?.code)}`);
      const adminReloginAfterReset = await login('admin', 'SysNew#2026ak');
      check('管理员可用超管设置的新口令登录', !!adminReloginAfterReset.token, brief(adminReloginAfterReset.error));
      if (adminReloginAfterReset.token) sessions.admin = adminReloginAfterReset;
    } else {
      checkBlocked('超级管理员重置管理员口令成功（200）', '前置不可用：账号列表里找不到 admin');
    }
  } else {
    checkBlocked('超级管理员访问账号列表成功（200）', '前置不可用：root（superadmin）账号未就绪');
    checkBlocked('超级管理员重置管理员口令成功（200）', '前置不可用：root（superadmin）账号未就绪');
  }

  const selfReset = await call('POST', `/api/admin/users/${adminUserId}/reset-password`, {
    token: sessions.admin.token, signKey: sessions.admin.signKey,
    body: { newPassword: 'SelfNew#2026ak', reason: '管理员重置自己口令' },
  });
  check('管理员重置自己口令走管理端被允许（自身口令豁免）', selfReset.status === 200,
    `status=${selfReset.status} body=${brief(selfReset.body, 120)}`);

  //用新口令重新登录 admin
  const staleAdmin = await call('GET', '/api/auth/me', { token: adminToken });
  check('管理员重置自己口令后旧令牌立即失效', staleAdmin.status === 401,
    `status=${staleAdmin.status} code=${brief(staleAdmin.body?.code)}`);
  const adminRelogin = await login('admin', 'SelfNew#2026ak');
  check('管理员可用新口令重新登录', !!adminRelogin.token, brief(adminRelogin.error));
  if (adminRelogin.token) sessions.admin = adminRelogin;

  console.log('\n== 飞书凭证全空时服务仍须可启动 ==');
  await checkEmptyCredentialStartup();

  console.log('\n== 审计链完整性 ==');
  const verify = await call('GET', '/api/admin/audit/verify', { token: sessions.admin.token });
  check('审计哈希链校验通过', verify.status === 200 && verify.body?.data?.ok === true,
    `status=${verify.status} data=${brief(verify.body?.data)}`);

  const mirrorSyncAudit = await auditEntries(sessions.admin, 'feishu.mirror.sync');
  check('镜像成功路径留下 feishu.mirror.sync 审计',
    mirrorSyncAudit.some(e => String(e.detail || '').includes(mirroredUser)),
    `audit=${brief(mirrorSyncAudit.slice(0, 2).map(e => e.detail))}`);
}

//飞书凭证全空时服务仍要能启动
async function checkEmptyCredentialStartup() {
  const exe = serverExeCandidates.find(p => fs.existsSync(p));
  if (!exe) {
    checkBlocked('飞书凭证全空时服务能正常启动（只跳过镜像）',
      `找不到可执行文件，请先 dotnet build：${serverExeCandidates.join(' 或 ')}`);
    return;
  }

  const port = 8992;
  const dataRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'aiapproval-nofeishu-'));
  const env = { ...process.env, AISERVER_LISTEN_URL: `http://127.0.0.1:${port}`, AISERVER_DATA_ROOT: dataRoot };
  for (const key of ['AISERVER_FEISHU_APPID', 'AISERVER_FEISHU_APPSECRET', 'AISERVER_FEISHU_APPTOKEN',
    'AISERVER_FEISHU_USERTABLEID', 'AISERVER_FEISHU_TABLEID', 'Feishu__BaseUrl', 'Feishu__AppId',
    'Feishu__AppSecret', 'Feishu__AppToken', 'Feishu__UserTableId']) {
    delete env[key];
  }

  let child = null;
  try {
    child = spawn(exe, [], { env, stdio: 'ignore', windowsHide: true });
    const up = await waitForHealth(port, 30000);
    check('飞书凭证全空时服务能正常启动（只跳过镜像）', up,
      `未能在 30 秒内于端口 ${port} 拿到健康检查 200；子进程 exited=${child.exitCode !== null}`);
    if (up) {
      const res = await call('GET', '/api/public/health', { base: `http://127.0.0.1:${port}` });
      check('该实例健康检查返回 ok', res.status === 200 && res.body?.data?.status === 'ok', `status=${res.status}`);
    }
  } finally {
    if (child && child.exitCode === null) child.kill();
    await new Promise(r => setTimeout(r, 500));
    try { fs.rmSync(dataRoot, { recursive: true, force: true }); } catch { /* 清理临时目录 */ }
  }
}

const started = Date.now();
try {
  await main();
} catch (err) {
  fail++;
  failures.push('脚本异常：' + err.message);
  console.error('脚本异常：', err);
}

console.log('--------------------------------------------------------------');
console.log(`账号验收：通过 ${pass} 项，失败 ${fail} 项，耗时 ${((Date.now() - started) / 1000).toFixed(1)}s`);
if (failures.length) {
  console.log('失败项：');
  failures.forEach(f => console.log('  - ' + f));
}
if (blocked.length) {
  console.log('因前置条件不可用而失败（同一个根因通常只影响这里列出的项）：');
  blocked.forEach(b => console.log('  - ' + b));
}
process.exit(fail === 0 ? 0 : 1);
