// ---------- API 封装：Token 鉴权 + 错误处理 ----------

const TOKEN_KEY = 'rainbot-webui-token';

export function getToken() {
  return localStorage.getItem(TOKEN_KEY) || '';
}

export function setToken(t) {
  if (t) localStorage.setItem(TOKEN_KEY, t);
  else localStorage.removeItem(TOKEN_KEY);
}

/** 401：需要访问令牌 */
export class AuthError extends Error {
  constructor() {
    super('需要访问令牌');
    this.name = 'AuthError';
  }
}

export async function api(path, { method = 'GET', body } = {}) {
  const headers = {};
  const token = getToken();
  if (token) headers['X-WebUi-Token'] = token;
  if (body !== undefined) headers['Content-Type'] = 'application/json';

  const res = await fetch(path, {
    method,
    headers,
    body: body !== undefined ? JSON.stringify(body) : undefined,
  });

  if (res.status === 401) throw new AuthError();

  let data = {};
  try {
    data = await res.json();
  } catch {
    /* 非 JSON 响应 */
  }
  if (!res.ok) throw new Error(data.error || `HTTP ${res.status}`);
  return data;
}

// ---------- SSE 实时事件流 ----------

/**
 * 打开 /api/webui/events 的 SSE 连接。
 * onEvent({type, time, data}) 每条事件回调；onState('open'|'retry') 连接状态回调。
 * 返回关闭函数；EventSource 会自动重连。
 */
export function openEventStream(onEvent, onState) {
  const token = getToken();
  const url = '/api/webui/events' + (token ? `?token=${encodeURIComponent(token)}` : '');
  const es = new EventSource(url);
  es.onopen = () => onState?.('open');
  es.onmessage = (e) => {
    try {
      onEvent(JSON.parse(e.data));
    } catch {
      /* 忽略坏事件 */
    }
  };
  es.onerror = () => onState?.('retry');
  return () => es.close();
}
