//前端静态自检脚本
//检查 DOM id 与 CSP 相关硬约束
import fs from 'node:fs';
import path from 'node:path';

const root = process.argv[2] || '.';
//页面与脚本配对
const pairs = [
  ['assets/app.js', 'index.html'],
  ['assets/admin.js', 'admin.html'],
  ['assets/finance.js', 'finance.html'],
  ['assets/net.js', 'index.html'],
  ['assets/net.js', 'admin.html'],
  ['assets/net.js', 'finance.html'],
];

const emojiRx = /[\u{1F300}-\u{1FAFF}\u{2600}-\u{27BF}\u{2B00}-\u{2BFF}\u{FE0F}\u{2190}-\u{21FF}]/gu;

let problems = 0;

//DOM id 一致性检查
console.log('== DOM id 一致性 ==');
for (const [js, html] of pairs) {
  const jsPath = path.join(root, js);
  const htmlPath = path.join(root, html);
  if (!fs.existsSync(jsPath) || !fs.existsSync(htmlPath)) continue;

  const code = fs.readFileSync(jsPath, 'utf8');
  const page = fs.readFileSync(htmlPath, 'utf8');
  const ids = [...new Set([...code.matchAll(/getElementById\('([^']+)'\)/g)].map(m => m[1]))];
  const missing = ids.filter(id => !page.includes(`id="${id}"`));
  console.log(`  ${js} -> ${html}：引用 ${ids.length} 个 id，缺失 ${missing.length ? missing.join(', ') : '无'}`);
  if (missing.length) problems++;
}

//静态安全检查
console.log('== 静态安全检查 ==');
const files = fs.readdirSync(root).filter(f => f.endsWith('.html'))
  .concat(fs.readdirSync(path.join(root, 'assets')).filter(f => /\.(js|css)$/.test(f)).map(f => 'assets/' + f));

for (const rel of files) {
  const raw = fs.readFileSync(path.join(root, rel), 'utf8');
  //先剥掉注释再扫描
  const text = raw
    .replace(/\/\*[\s\S]*?\*\//g, ' ')
    .replace(/^\s*\/\/.*$/gm, ' ')
    .replace(/([^:'"\\])\/\/[^\n]*/g, '$1 ');

  const emoji = (text.match(emojiRx) || []).length;
  const dangerous = (text.match(/innerHTML|outerHTML|document\.write|eval\(/g) || []).length;
  const styleAttr = (text.match(/\sstyle="/g) || []).length;
  const styleTag = (text.match(/<style[\s>]/g) || []).length;
  const inlineScript = (text.match(/<script(?![^>]*\bsrc=)/g) || []).length;
  const inlineEvent = (text.match(/\son(click|load|error|submit|change|input)=/g) || []).length;
  const external = (text.match(/https?:\/\/(?!127\.0\.0\.1|localhost)[a-z0-9.-]+/gi) || []).length;

  const notes = [];
  if (emoji) notes.push(`emoji=${emoji}`);
  if (dangerous) notes.push(`危险写法=${dangerous}`);
  if (styleAttr) notes.push(`inline style=${styleAttr}`);
  if (styleTag) notes.push(`<style>=${styleTag}`);
  if (inlineScript) notes.push(`内联脚本=${inlineScript}`);
  if (inlineEvent) notes.push(`内联事件=${inlineEvent}`);
  if (external) notes.push(`外部链接=${external}`);

  if (notes.length) { problems++; console.log(`  [注意] ${rel}：${notes.join('，')}`); }
  else console.log(`  [OK] ${rel}`);
}

console.log('--------------------------------------------------------------');
console.log(problems === 0 ? '前端自检通过' : `前端自检发现 ${problems} 处需要确认`);
process.exit(problems === 0 ? 0 : 1);
