//生成超管口令哈希
import crypto from 'node:crypto';

//口令与迭代次数
const password = process.argv[2];
const iterations = Number(process.argv[3] || 210000);

//必须给口令
if (!password) {
  console.error('用法：node tools/hash-password.mjs <口令> [迭代次数]');
  console.error('迭代次数缺省 210000，必须与服务端一致，否则登录校验不会通过。');
  process.exit(1);
}

//迭代次数下限
if (!Number.isInteger(iterations) || iterations < 10000) {
  console.error('迭代次数必须是大于等于 10000 的整数。');
  process.exit(1);
}

//算哈希并输出
const salt = crypto.randomBytes(16);
const hash = crypto.pbkdf2Sync(password, salt, iterations, 32, 'sha256');

console.log('PasswordHash=' + hash.toString('base64'));
console.log('PasswordSalt=' + salt.toString('base64'));
console.log('PasswordIterations=' + iterations);
