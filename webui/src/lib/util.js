// ---------- 通用工具：时间格式化 / 短ID / 头像颜色 / 试聊群 ----------

export const SIM_GROUP = 'webui-sim';
export const BOT_SENDER = '$bot';

export function shortId(id) {
  if (!id) return '';
  return id.length > 10 ? `${id.slice(0, 6)}…` : id;
}

/** 与服务端 UserIdentityResolver.ShortId 保持一致，仅用于群友身份。 */
export function userShortId(id) {
  return id ? `u${id.slice(0, 8)}` : '';
}

/** 会话键 → 对端原始 ID：去掉 {实例Id}: 前缀与私聊的 p 标记 */
export function peerIdOf(group) {
  if (!group) return '';
  const i = group.indexOf(':');
  const raw = i >= 0 ? group.slice(i + 1) : group;
  return raw.startsWith('p') ? raw.slice(1) : raw;
}

/** 会话键 → 实例 Id（无前缀返回空） */
export function botIdOf(group) {
  if (!group) return '';
  const i = group.indexOf(':');
  return i > 0 ? group.slice(0, i) : '';
}

/** 会话键是否为私聊（{实例Id}:p{用户号}） */
export function isPrivateKey(group) {
  const i = group.indexOf(':');
  return i >= 0 && group[i + 1] === 'p';
}

/** 会话显示名：试聊群 / 群 {短ID} / 私聊 {短ID} */
export function groupName(group) {
  if (group === SIM_GROUP) return 'WebUI 试聊群';
  const i = group.indexOf(':');
  const raw = i >= 0 ? group.slice(i + 1) : group;
  if (raw.startsWith('p')) return `私聊 ${shortId(raw.slice(1))}`;
  return `群 ${shortId(raw)}`;
}

const AVATAR_COLORS = [
  '#12b7f5', '#f759ab', '#fa8c16', '#52c41a', '#722ed1',
  '#13c2c2', '#eb2f96', '#a0d911', '#fa541c', '#2f54eb',
];

export function colorOf(seed) {
  let h = 0;
  for (let i = 0; i < seed.length; i++) h = (h * 31 + seed.charCodeAt(i)) >>> 0;
  return AVATAR_COLORS[h % AVATAR_COLORS.length];
}

export function displayNameOf(user) {
  return user?.username?.trim() || (user?.sender && user.sender.startsWith('u_webui_sim_') ? simNameOf(user.sender) : userShortId(user?.sender || ''));
}

export function simNameOf(id) {
  const SIM = [
    ['u_webui_sim_01', '雨夜听风'],
    ['u_webui_sim_02', '蓝莓气泡'],
    ['u_webui_sim_03', '橙子汽水'],
    ['u_webui_sim_04', '薄荷凉茶'],
  ];
  return SIM.find(([i]) => i === id)?.[1] || 'WebUI 群友';
}

export const SIM_MEMBERS = [
  { id: 'u_webui_sim_01', name: '雨夜听风' },
  { id: 'u_webui_sim_02', name: '蓝莓气泡' },
  { id: 'u_webui_sim_03', name: '橙子汽水' },
  { id: 'u_webui_sim_04', name: '薄荷凉茶' },
];

export function fmtTime(t) {
  const d = new Date(t);
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}

export function fmtFull(t) {
  const d = new Date(t);
  return `${d.getFullYear()}/${d.getMonth() + 1}/${d.getDate()} ${fmtTime(t)}`;
}

/** 日期分隔线文案：今天 / 昨天 / 日期 */
export function fmtDay(t) {
  const d = new Date(t);
  const now = new Date();
  const day0 = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime();
  const day = new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
  const diff = Math.round((day0 - day) / 86400000);
  if (diff === 0) return '今天';
  if (diff === 1) return '昨天';
  return `${d.getFullYear()}/${d.getMonth() + 1}/${d.getDate()}`;
}

export function fmtAgo(t) {
  if (!t) return '';
  const diff = (Date.now() - new Date(t).getTime()) / 1000;
  if (diff < 60) return '刚刚';
  if (diff < 3600) return `${Math.floor(diff / 60)} 分钟前`;
  if (diff < 86400) return `${Math.floor(diff / 3600)} 小时前`;
  if (diff < 86400 * 30) return `${Math.floor(diff / 86400)} 天前`;
  return `${Math.floor(diff / 86400 / 30)} 月前`;
}

export function fmtUptime(ts) {
  let s = null;
  if (typeof ts === 'number') {
    s = Math.max(0, Math.floor(ts));
  } else if (typeof ts === 'string' && ts) {
    // 后端 TimeSpan 序列化格式："dd.hh:mm:ss.fffffff" 或 "hh:mm:ss.fffffff"
    const m = ts.match(/^(?:(\d+)\.)?(\d+):(\d+):(\d+)/);
    if (m) {
      s = (parseInt(m[1] || '0', 10) * 86400) + parseInt(m[2], 10) * 3600 + parseInt(m[3], 10) * 60 + parseInt(m[4], 10);
    }
  }
  if (s === null) return '—';
  const d = Math.floor(s / 86400);
  const h = Math.floor((s % 86400) / 3600);
  const m = Math.floor((s % 3600) / 60);
  if (d > 0) return `${d} 天 ${h} 小时`;
  if (h > 0) return `${h} 小时 ${m} 分`;
  return `${m} 分 ${s % 60} 秒`;
}

export function fmtTokens(n) {
  if (n >= 1e6) return `${(n / 1e6).toFixed(1)}M`;
  if (n >= 1e3) return `${(n / 1e3).toFixed(1)}K`;
  return String(n);
}

export function throttle(fn, wait) {
  let last = 0;
  let timer = null;
  return (...args) => {
    const now = Date.now();
    const remaining = wait - (now - last);
    if (remaining <= 0) {
      clearTimeout(timer);
      last = now;
      fn(...args);
    } else if (!timer) {
      timer = setTimeout(() => {
        last = Date.now();
        timer = null;
        fn(...args);
      }, remaining);
    }
  };
}
