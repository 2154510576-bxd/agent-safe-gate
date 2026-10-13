//还原使用轨迹（只读本地数据）
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));

//解析命令行参数
const args = process.argv.slice(2);
let dataRoot = '';
let userFilter = '';
let lastN = 0;
for (let i = 0; i < args.length; i++) {
  if (args[i] === '--user') { userFilter = args[++i] || ''; continue; }
  if (args[i] === '--last') { lastN = Number(args[++i]) || 0; continue; }
  if (!args[i].startsWith('--') && !dataRoot) { dataRoot = args[i]; continue; }
}
if (!dataRoot) dataRoot = path.resolve('../AiApprovalServer/bin/Debug/net8.0/App_Data');

//读 JSON，失败取默认值
function readJson(name, fallback) {
  try { return JSON.parse(fs.readFileSync(path.join(dataRoot, name), 'utf8')); }
  catch { return fallback; }
}

//派生原始输入的解密密钥
function deriveRawInputKey(secrets, pepper) {
  const aesKey = Buffer.from(secrets.aes, 'base64');
  return crypto.createHmac('sha256', aesKey).update('aiapproval:raw-input:' + pepper, 'utf8').digest();
}

//解密任务原始输入
function decryptRawInput(cipherB64, key, taskId) {
  if (!cipherB64) return '';
  try {
    const blob = Buffer.from(cipherB64, 'base64');
    const nonce = blob.subarray(0, 12);
    const tag = blob.subarray(12, 28);
    const cipher = blob.subarray(28);
    const d = crypto.createDecipheriv('aes-256-gcm', key, nonce, { authTagLength: 16 });
    d.setAuthTag(tag);
    d.setAAD(Buffer.from('task-raw|' + taskId, 'utf8'));
    return Buffer.concat([d.update(cipher), d.final()]).toString('utf8');
  } catch (e) {
    return `（解密失败：${e.message}）`;
  }
}

const secrets = readJson('secrets.json', {});
//appsettings 按脚本位置找
let pepper = '';
try {
  const appSettings = JSON.parse(fs.readFileSync(path.join(scriptDir, '..', 'AiApprovalServer', 'appsettings.json'), 'utf8'));
  pepper = appSettings?.Security?.SecretPepper || '';
} catch { pepper = ''; }
const rawKey = secrets.aes ? deriveRawInputKey(secrets, pepper) : null;
console.log(`（解密密钥：${rawKey ? '已从 secrets.json 派生' : '缺失'}；pepper=${pepper ? '已读取' : '为空'}）`);

//读取任务与审计日志
const tasks = readJson('tasks.json', []);
const audit = fs.readFileSync(path.join(dataRoot, 'audit.jsonl'), 'utf8').trim().split('\n')
  .map(l => { try { return JSON.parse(l); } catch { return null; } }).filter(Boolean);

//按用户筛选并按时间排序
const filtered = (userFilter ? tasks.filter(t => t.userName === userFilter) : tasks)
  .sort((a, b) => new Date(a.createdAt) - new Date(b.createdAt));
const shown = lastN > 0 ? filtered.slice(-lastN) : filtered;

console.log(`数据目录：${dataRoot}`);
console.log(`任务 ${tasks.length} 条（本次展示 ${shown.length} 条）｜审计 ${audit.length} 条\n`);

//逐条输出任务时间线
for (const t of shown) {
  const raw = rawCipherOrPlain(t);
  const ruleReasons = (t.rule?.reasons || []).join('；');
  const translations = (t.rule?.translations || []).join('；');
  let result = '';
  try {
    const r = t.resultJson ? JSON.parse(t.resultJson) : null;
    result = r ? (r.summary || '') : '';
  } catch { /*忽略解析失败*/ }

  console.log('────────────────────────────────────────────────────────');
  console.log(`[${new Date(t.createdAt).toLocaleString('zh-CN')}] ${t.userName}（${t.userRole}）`);
  console.log(`  用户输入 : ${raw.replace(/\n/g, ' ⏎ ')}`);
  console.log(`  意图摘要 : ${t.intent}`);
  console.log(`  工具动作 : ${t.toolName}  ${JSON.stringify(t.rule?.normalizedArgs || {})}`);
  console.log(`  风险/状态: ${t.riskLevel} / ${t.status}${t.statusReason ? ' —— ' + t.statusReason : ''}`);
  if (ruleReasons) console.log(`  规则结论 : ${ruleReasons}`);
  if (translations) console.log(`  参数映射 : ${translations}`);
  if (t.review?.verdict) console.log(`  AI 审核  : ${t.review.verdict}（${t.review.reason || ''}）`);
  if ((t.injectionFlags || []).length) console.log(`  注入特征 : ${t.injectionFlags.join('、')}`);
  if (t.decidedBy) console.log(`  裁决     : ${t.decidedBy} —— ${t.decisionReason || ''}`);
  if (result) console.log(`  执行结果 : ${result.replace(/\n/g, ' ').slice(0, 400)}`);
  if ((t.versionIds || []).length) console.log(`  版本记录 : ${t.versionIds.join(', ')}`);

  //取用户原始输入
  function rawCipherOrPlain(task) {
    if (!task.rawInputCipher) return '（无）';
    if (!rawKey) return '（secrets.json 缺失，无法解密）';
    return decryptRawInput(task.rawInputCipher, rawKey, task.id);
  }
}

//AI 调用情况
const plans = audit.filter(a => a.action === 'ai.plan');
const fallbacks = plans.filter(p => p.outcome === 'FALLBACK');
console.log('\n────────────────────────────────────────────────────────');
console.log(`AI 意图解析调用 ${plans.length} 次，其中失败降级 ${fallbacks.length} 次`);
if (fallbacks.length) console.log('  最近一次失败原因：' + (fallbacks[fallbacks.length - 1].detail || '').slice(0, 200));
const tokens = plans.filter(p => /tokens=(\d+)\/(\d+)/.test(p.detail || ''))
  .map(p => { const m = p.detail.match(/tokens=(\d+)\/(\d+)/); return Number(m[1]) + Number(m[2]); });
if (tokens.length) console.log(`  模型 token 用量合计约 ${tokens.reduce((a, b) => a + b, 0)}（${tokens.length} 次成功调用）`);

//统计被拦与被打回
const rejects = audit.filter(a => a.action.startsWith('task.reject'));
console.log(`\n被拦/被打回 ${rejects.length} 次：`);
for (const r of rejects.slice(-10)) console.log(`  [${new Date(r.time).toLocaleString('zh-CN')}] ${r.actorName || '系统'} ${r.target} ${r.outcome} —— ${r.detail}`);
