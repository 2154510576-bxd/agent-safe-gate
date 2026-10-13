//前端 API 客户端与请求签名
(function (global) {
  'use strict';

  var SIGNED_METHODS = { POST: 1, PUT: 1, PATCH: 1, DELETE: 1 };
  var STORAGE_KEY = 'aiapproval.session.v1';
  var session = { token: '', signKey: '', user: null, expiresAt: '' };

  try {
    var raw = global.sessionStorage.getItem(STORAGE_KEY);
    if (raw) session = JSON.parse(raw);
  } catch (e) { session = { token: '', signKey: '', user: null, expiresAt: '' }; }

  function persist() {
    try { global.sessionStorage.setItem(STORAGE_KEY, JSON.stringify(session)); } catch (e) { /* 隐私模式忽略 */ }
  }

  //字符串转 UTF-8 字节
  function utf8(text) {
    return (global.TextEncoder) ? new global.TextEncoder().encode(text) : global.CryptoLite.utf8Bytes(text);
  }

  //字节转 base64
  function toBase64(bytes) {
    var binary = '';
    for (var i = 0; i < bytes.length; i++) binary += String.fromCharCode(bytes[i]);
    return global.btoa(binary);
  }

  //字节转十六进制
  function toHex(bytes) {
    var s = '';
    for (var i = 0; i < bytes.length; i++) s += (bytes[i] < 16 ? '0' : '') + bytes[i].toString(16);
    return s;
  }

  var subtle = global.crypto && global.crypto.subtle ? global.crypto.subtle : null;

  //摘要转十六进制
  function sha256Hex(text) {
    if (subtle) {
      return subtle.digest('SHA-256', utf8(text)).then(function (buf) {
        return toHex(new Uint8Array(buf));
      }).catch(function () { return global.CryptoLite.sha256Hex(text); });
    }
    return Promise.resolve(global.CryptoLite.sha256Hex(text));
  }

  //计算 HMAC 签名
  function hmacBase64(keyText, message) {
    if (subtle) {
      return subtle.importKey('raw', utf8(keyText), { name: 'HMAC', hash: 'SHA-256' }, false, ['sign'])
        .then(function (key) { return subtle.sign('HMAC', key, utf8(message)); })
        .then(function (buf) { return toBase64(new Uint8Array(buf)); })
        .catch(function () { return global.CryptoLite.hmacSha256Base64(keyText, message); });
    }
    return Promise.resolve(global.CryptoLite.hmacSha256Base64(keyText, message));
  }

  //生成随机 nonce
  function randomNonce() {
    var bytes = new Uint8Array(18);
    if (global.crypto && global.crypto.getRandomValues) global.crypto.getRandomValues(bytes);
    else for (var i = 0; i < bytes.length; i++) bytes[i] = Math.floor(Math.random() * 256);

    var s = '';
    for (var j = 0; j < bytes.length; j++) s += (bytes[j] < 16 ? '0' : '') + bytes[j].toString(16);
    return s;
  }

  //构造请求头与签名
  function buildHeaders(method, pathWithQuery, bodyText) {
    var headers = { 'Accept': 'application/json' };
    if (session.token) headers['Authorization'] = 'Bearer ' + session.token;
    if (bodyText !== null) headers['Content-Type'] = 'application/json';

    if (!SIGNED_METHODS[method] || !session.signKey) return Promise.resolve(headers);

    var ts = String(Date.now());
    var nonce = randomNonce();

    return sha256Hex(bodyText === null ? '' : bodyText).then(function (bodyHash) {
      var canonical = [method, pathWithQuery, ts, nonce, bodyHash].join('\n');
      return hmacBase64(session.signKey, canonical);
    }).then(function (signature) {
      headers['X-Timestamp'] = ts;
      headers['X-Nonce'] = nonce;
      headers['X-Signature'] = signature;
      return headers;
    });
  }

  //发送请求并处理错误
  function request(path, options) {
    options = options || {};
    var method = (options.method || 'GET').toUpperCase();
    var bodyText = (options.body === undefined || options.body === null) ? null : JSON.stringify(options.body);
    var skipAuth = !!options.skipAuth;

    var authHeaderPromise = skipAuth
      ? Promise.resolve({ 'Accept': 'application/json', 'Content-Type': 'application/json' })
      : buildHeaders(method, path, bodyText);

    return authHeaderPromise.then(function (headers) {
      return fetch(path, {
        method: method,
        headers: headers,
        body: bodyText === null ? undefined : bodyText,
        credentials: 'omit',
        cache: 'no-store',
        redirect: 'error'
      });
    }).then(function (response) {
      return response.text().then(function (text) {
        var payload = null;
        try { payload = text ? JSON.parse(text) : null; } catch (e) { payload = { raw: text }; }

        if (!response.ok) {
          var error = new Error((payload && payload.message) || ('请求失败（HTTP ' + response.status + '）'));
          error.status = response.status;
          error.code = payload && payload.code;
          if (response.status === 401 && !options.keepSession) {
            //登录失效，通知页面
            API.clear();
            global.dispatchEvent(new CustomEvent('session-expired', { detail: error.code }));
          }
          throw error;
        }

        return payload;
      });
    });
  }

  var API = {
    get: function (path) { return request(path, { method: 'GET' }); },
    post: function (path, body, options) {
      options = options || {};
      options.method = 'POST';
      options.body = body === undefined ? {} : body;
      return request(path, options);
    },
    public: function (path, body) { return request(path, { method: 'POST', body: body, skipAuth: true }); },
    publicGet: function (path) { return request(path, { method: 'GET', skipAuth: true }); },

    session: function () { return session; },
    isLoggedIn: function () { return !!session.token; },
    setSession: function (data) {
      session = {
        token: data.token,
        signKey: data.signKey,
        user: data.user,
        expiresAt: data.expiresAt
      };
      persist();
    },
    clear: function () {
      session = { token: '', signKey: '', user: null, expiresAt: '' };
      try { global.sessionStorage.removeItem(STORAGE_KEY); } catch (e) { /* ignore */ }
    }
  };

  //极简 DOM 与格式化工具
  //创建元素并写入文本
  function el(tag, className, text) {
    var node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null) node.textContent = String(text);
    return node;
  }

  //格式化 JSON
  function json(value) {
    try { return JSON.stringify(value, null, 2); } catch (e) { return String(value); }
  }

  //格式化时间
  function fmtTime(value) {
    if (!value) return '-';
    var d = new Date(value);
    if (isNaN(d.getTime())) return String(value);
    var pad = function (n) { return n < 10 ? '0' + n : '' + n; };
    return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate()) + ' ' +
           pad(d.getHours()) + ':' + pad(d.getMinutes()) + ':' + pad(d.getSeconds());
  }

  //任务状态配色
  function statusClass(status) {
    switch (status) {
      case 'EXECUTED': return 'ok';
      case 'PENDING_APPROVAL': return 'warn';
      case 'EXECUTING': return 'info';
      case 'REJECTED_RULE': case 'REJECTED_AI': case 'REJECTED_ADMIN': return 'bad';
      case 'FAILED': case 'EXPIRED': return 'muted';
      default: return 'info';
    }
  }

  //风险等级配色
  function riskClass(risk) {
    switch (risk) {
      case 'LOW': return 'ok';
      case 'MEDIUM': return 'info';
      case 'HIGH': return 'warn';
      case 'CRITICAL': return 'bad';
      default: return 'muted';
    }
  }

  //弹出提示条
  function toast(message, kind) {
    var host = document.getElementById('toast-host');
    if (!host) return;
    var box = el('div', 'toast ' + (kind || 'info'), message);
    host.appendChild(box);
    setTimeout(function () {
      box.classList.add('fade');
      setTimeout(function () { if (box.parentNode) box.parentNode.removeChild(box); }, 400);
    }, 4200);
  }

  //清空子节点
  function clear(node) {
    while (node.firstChild) node.removeChild(node.firstChild);
    return node;
  }

  //还原 \uXXXX 转义
  function unescapeUnicode(value) {
    if (typeof value !== 'string' || value.indexOf('\\u') < 0) return value;

    return value.replace(/\\u([0-9a-fA-F]{4})/g, function (match, hex) {
      return String.fromCharCode(parseInt(hex, 16));
    }).replace(/\\u\{([0-9a-fA-F]{1,6})\}/g, function (match, hex) {
      return String.fromCodePoint(parseInt(hex, 16));
    });
  }

  //递归还原转义
  function unescapeDeep(value) {
    if (typeof value === 'string') return unescapeUnicode(value);
    if (Array.isArray(value)) return value.map(unescapeDeep);
    if (value && typeof value === 'object') {
      var out = {};
      Object.keys(value).forEach(function (k) { out[k] = unescapeDeep(value[k]); });
      return out;
    }
    return value;
  }

  //导出全局 API 与工具
  global.API = API;
  global.U = {
    el: el,
    json: json,
    fmtTime: fmtTime,
    statusClass: statusClass,
    riskClass: riskClass,
    toast: toast,
    clear: clear,
    unescapeUnicode: unescapeUnicode,
    unescapeDeep: unescapeDeep
  };
})(globalThis);
