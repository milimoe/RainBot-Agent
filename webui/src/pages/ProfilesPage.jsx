import { useCallback, useEffect, useMemo, useState } from 'react';
import { api, AuthError } from '../lib/api.js';
import { fmtAgo, groupName, isPrivateKey, userShortId } from '../lib/util.js';
import Avatar from '../components/Avatar.jsx';
import { Badge, EmptyState, Spinner, toast } from '../components/ui.jsx';
import { IconRefresh, IconSearch } from '../components/Icons.jsx';

const inputCls = 'w-full rounded-md border border-qq-border bg-white px-2.5 py-1.5 text-[13px] outline-none focus:border-qq-blue';
const buttonCls = 'rounded-md border border-qq-border bg-white px-3 py-1.5 text-xs hover:bg-qq-hover disabled:opacity-40';

/** 时间字符串 → 相对时间；无法解析时返回空串 */
function ago(t) {
  if (!t) return '';
  const d = new Date(t);
  return Number.isFinite(d.getTime()) ? fmtAgo(t) : '';
}

function Row({ label, value }) {
  return (
    <div className="flex gap-2 text-xs leading-5">
      <span className="shrink-0 text-qq-sub">{label}</span>
      <span className="min-w-0 break-words">{value}</span>
    </div>
  );
}

function ProfileCard({ user }) {
  const name = user.nickname?.trim() || user.shortId;
  return (
    <div className="flex gap-3 rounded-xl border border-qq-border bg-white p-4">
      <Avatar sender={user.userOpenId} name={name} size={40} />
      <div className="min-w-0 flex-1 space-y-1.5">
        <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
          <span className="truncate text-[13px] font-medium">{name}</span>
          <code className="rounded bg-qq-bg px-1 text-[11px] text-qq-sub">{user.shortId}</code>
          {user.interactionCount > 0 && <Badge tone="blue">互动 {user.interactionCount} 次</Badge>}
          {user.tags.map((tag) => <Badge key={tag} tone="green">#{tag}</Badge>)}
        </div>
        {user.interests && <Row label="兴趣" value={user.interests} />}
        {user.habits && <Row label="习惯" value={user.habits} />}
        {user.summary && <Row label="过往" value={user.summary} />}
        {!user.interests && !user.habits && !user.summary && user.tags.length === 0 && (
          <p className="text-xs text-qq-sub">已建档，暂无画像内容；群友继续发言后由机器人沉淀。</p>
        )}
        <div className="flex gap-3 text-[11px] text-qq-sub">
          {ago(user.lastActive) && <span>最近活跃 {ago(user.lastActive)}</span>}
          {ago(user.profileUpdatedAt) && <span>画像更新于 {ago(user.profileUpdatedAt)}</span>}
        </div>
      </div>
    </div>
  );
}

/**
 * 画像页：左侧会话列表 + 右侧画像查看（群画像 Block C + 成员画像 L0，只读）。
 */
export default function ProfilesPage({ onAuthFail }) {
  const [groups, setGroups] = useState([]);
  const [active, setActive] = useState('');
  const [data, setData] = useState(null);
  const [loading, setLoading] = useState(false);
  const [filter, setFilter] = useState('');
  const [groupFilter, setGroupFilter] = useState('');

  const report = (e) => {
    if (e instanceof AuthError) onAuthFail?.();
    else toast(e.message, 'error');
  };

  const loadGroups = useCallback(async () => {
    try {
      const result = await api('/api/webui/groups');
      setGroups(result.groups || []);
      setActive((current) => current || (result.groups || [])[0]?.group || '');
    } catch (e) { report(e); }
  }, [onAuthFail]);

  const loadProfiles = useCallback(async (group) => {
    if (!group) return;
    setLoading(true);
    try {
      setData(await api(`/api/webui/groups/${encodeURIComponent(group)}/profiles`));
    } catch (e) { report(e); } finally { setLoading(false); }
  }, [onAuthFail]);

  useEffect(() => { loadGroups(); }, [loadGroups]);
  useEffect(() => { loadProfiles(active); }, [active, loadProfiles]);

  const filteredGroups = useMemo(() => {
    const q = groupFilter.trim();
    if (!q) return groups;
    return groups.filter((g) => groupName(g.group).includes(q) || g.group.includes(q) || (g.botName || '').includes(q));
  }, [groups, groupFilter]);

  const users = useMemo(() => {
    const list = data?.users || [];
    const q = filter.trim().toLowerCase();
    if (!q) return list;
    return list.filter((u) =>
      (u.nickname || '').toLowerCase().includes(q)
      || u.shortId.toLowerCase().includes(q)
      || u.userOpenId.toLowerCase().includes(q)
      || u.tags.some((tag) => tag.toLowerCase().includes(q))
    );
  }, [data, filter]);

  const groupProfile = data?.groupProfile;

  return (
    <div className="flex h-full min-w-0 flex-1 flex-col md:flex-row">
      {/* 会话列表 */}
      <aside className="qq-scroll max-h-48 shrink-0 overflow-y-auto border-b border-qq-border bg-qq-panel py-4 md:max-h-none md:w-[240px] md:border-b-0 md:border-r">
        <div className="mb-2 px-4 text-xs font-medium text-qq-sub">会话</div>
        <div className="px-3 pb-2">
          <div className="relative">
            <span className="absolute left-2.5 top-1/2 -translate-y-1/2 text-qq-sub"><IconSearch size={14} /></span>
            <input className={`${inputCls} pl-7`} value={groupFilter} placeholder="搜索会话" onChange={(e) => setGroupFilter(e.target.value)} />
          </div>
        </div>
        {filteredGroups.map((g) => (
          <button key={g.group} onClick={() => setActive(g.group)}
            className={`block w-full border-l-2 px-4 py-2.5 text-left ${active === g.group ? 'border-qq-blue bg-white' : 'border-transparent hover:bg-qq-hover'}`}>
            <div className="flex items-center gap-1.5 text-[13px] font-medium">
              <span className="truncate">{groupName(g.group)}</span>
              {g.isSim && <Badge tone="blue">试聊</Badge>}
              {isPrivateKey(g.group) && <Badge tone="gray">私聊</Badge>}
              {g.muted && <Badge tone="orange">静默</Badge>}
            </div>
            <div className="mt-0.5 truncate text-xs text-qq-sub">{g.botName || ''}</div>
          </button>
        ))}
        {!filteredGroups.length && <p className="px-4 py-2 text-xs text-qq-sub">没有匹配的会话</p>}
      </aside>

      {/* 画像内容 */}
      <main className="qq-scroll min-w-0 flex-1 overflow-y-auto px-4 py-5 md:px-8">
        <div className="mx-auto max-w-4xl space-y-4">
          <div className="flex items-center justify-between gap-3">
            <div className="min-w-0">
              <h1 className="text-lg font-semibold">{active ? groupName(active) : '画像'}</h1>
              <p className="mt-1 text-xs text-qq-sub">查看机器人沉淀的群画像与成员画像（只读；敏感字段不在此展示）。</p>
            </div>
            <button className={`${buttonCls} flex shrink-0 items-center gap-1.5`} disabled={loading || !active} onClick={() => loadProfiles(active)}>
              {loading ? <Spinner className="h-3.5 w-3.5" /> : <IconRefresh size={14} />} 刷新
            </button>
          </div>

          {!active && <EmptyState icon="👤" title="选择一个会话" desc="左侧选择要查看画像的会话。" />}
          {active && loading && !data && <p className="text-xs text-qq-sub">加载中…</p>}

          {active && data && <>
            <section className="space-y-2 rounded-xl border border-qq-border bg-white p-4">
              <div className="flex flex-wrap items-center gap-2">
                <h2 className="text-[14px] font-medium">群画像</h2>
                {groupProfile?.type && <Badge tone="gray">{groupProfile.type}</Badge>}
                {groupProfile?.muted && <Badge tone="orange">静默</Badge>}
                {groupProfile?.degraded && <Badge tone="orange">降级</Badge>}
                <span className="text-xs text-qq-sub">已建档 {data.total} 位成员</span>
              </div>
              {groupProfile?.summary && <Row label="概要" value={groupProfile.summary} />}
              {groupProfile?.taboos && <Row label="禁忌" value={groupProfile.taboos} />}
              {!groupProfile?.summary && !groupProfile?.taboos && (
                <p className="text-xs text-qq-sub">暂无群画像；机器人会在对话中逐步沉淀群概要与禁忌话题。</p>
              )}
            </section>

            <div className="relative">
              <span className="absolute left-3 top-1/2 -translate-y-1/2 text-qq-sub"><IconSearch size={14} /></span>
              <input className={`${inputCls} pl-8`} value={filter} placeholder="搜索昵称 / 标识 / 标签" onChange={(e) => setFilter(e.target.value)} />
            </div>

            {data.truncated && !filter && <p className="text-xs text-qq-sub">成员较多，仅显示互动最活跃的前 {data.users.length} 位，可用搜索查找其他成员。</p>}
            <div className="grid gap-3 lg:grid-cols-2">
              {users.map((u) => <ProfileCard key={u.userOpenId} user={u} />)}
            </div>
            {!loading && !users.length && (
              <EmptyState icon="👤" title={filter ? '没有匹配的成员' : '暂无成员画像'}
                desc={filter ? '换个关键词试试。' : '群友发言后机器人会自动建档并累计互动。'} />
            )}
          </>}
        </div>
      </main>
    </div>
  );
}
