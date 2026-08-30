import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { api, AuthError } from '../lib/api.js';
import { fmtFull, fmtTokens, fmtUptime, groupName, SIM_GROUP } from '../lib/util.js';
import { Badge, Spinner, toast } from '../components/ui.jsx';
import { IconRefresh } from '../components/Icons.jsx';

/**
 * 状态页：连接状态 / 运行概览 / 缓存命中率 / 每群统计与运维操作。
 */
export default function StatusPage({ onAuthFail }) {
  const [health, setHealth] = useState(null);
  const [status, setStatus] = useState(null);
  const [stats, setStats] = useState(null);
  const [groups, setGroups] = useState([]);
  const [threshold, setThreshold] = useState(0.6);
  const [busy, setBusy] = useState(null);
  const [tick, setTick] = useState(Date.now()); // 每秒触发，让运行时长本地跳动
  const fetchedAtRef = useRef(Date.now());

  const load = useCallback(async () => {
    try {
      const [h, s, st, g] = await Promise.all([
        api('/health'),
        api('/api/webui/status'),
        api('/api/webui/stats'),
        api('/api/webui/groups'),
      ]);
      setHealth(h);
      setStatus(s);
      setStats(st);
      setGroups(g.groups || []);
      fetchedAtRef.current = Date.now();
      const cfg = await api('/api/webui/config');
      const t = (cfg.items || []).find((i) => i.key === 'Context.CacheAlertThreshold');
      if (t) setThreshold(Number(t.value) || 0.6);
    } catch (e) {
      if (e instanceof AuthError) onAuthFail?.();
      else toast(e.message, 'error');
    }
  }, [onAuthFail]);

  useEffect(() => {
    load();
    const interval = setInterval(load, 15000);
    const ticker = setInterval(() => setTick(Date.now()), 1000);
    const onEvent = () => load();
    window.addEventListener('webui:event', onEvent);
    return () => {
      clearInterval(interval);
      clearInterval(ticker);
      window.removeEventListener('webui:event', onEvent);
    };
  }, [load]);

  // 运行时长：以最近一次 /health 返回值为基准，本地每秒递增（后端提供 uptimeSeconds 数值）
  const uptimeSeconds = health
    ? (typeof health.uptimeSeconds === 'number' ? health.uptimeSeconds : 0) + (tick - fetchedAtRef.current) / 1000
    : null;

  const doAction = async (group, action) => {
    setBusy(group);
    try {
      if (action === 'mute') {
        await api(`/api/webui/groups/${encodeURIComponent(group)}/mute`, { method: 'POST', body: { muted: true } });
        toast('已静默该群', 'success');
      } else if (action === 'unmute') {
        await api(`/api/webui/groups/${encodeURIComponent(group)}/mute`, { method: 'POST', body: { muted: false } });
        toast('已解除静默', 'success');
      } else if (action === 'reset') {
        if (!window.confirm('重置该群上下文？')) return;
        await api(`/api/webui/groups/${encodeURIComponent(group)}/reset-context`, { method: 'POST' });
        toast('上下文已重置', 'success');
      }
      await load();
    } catch (e) {
      toast(e.message, 'error');
    } finally {
      setBusy(null);
    }
  };

  const ws = !!health?.wsConnected || !!status?.bot?.wsConnected;
  const groupStats = useMemo(() => {
    const list = stats?.groups || [];
    return list.sort((a, b) => b.messages - a.messages);
  }, [stats]);

  const totals = stats?.totals;
  const hitRate = totals ? totals.hitRate : 0;
  const alert = hitRate < threshold;

  return (
    <div className="qq-scroll h-full min-w-0 flex-1 overflow-y-auto">
      <div className="mx-auto max-w-4xl space-y-4 px-6 py-6">
        {/* 顶部状态横幅 */}
        <div className={`rounded-xl border p-4 ${ws ? 'border-[#b7ebc5] bg-[#f0fff5]' : 'border-[#ffd6d6] bg-[#fff7f7]'}`}>
          <div className="flex flex-wrap items-center gap-x-6 gap-y-2">
            <div className="flex items-center gap-2">
              <span className="relative flex h-2.5 w-2.5">
                {ws && <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-qq-green opacity-60" />}
                <span className={`relative inline-flex h-2.5 w-2.5 rounded-full ${ws ? 'bg-qq-green' : 'bg-qq-red'}`} />
              </span>
              <span className="text-sm font-medium">{ws ? 'QQ 网关已连接' : 'QQ 网关未连接'}</span>
            </div>
            <div className="text-xs text-qq-sub">
              机器人 <b className="text-qq-text">{status?.bot?.botName || '雨'}</b> · 模型 <b className="text-qq-text">{status?.bot?.model || '—'}</b>
            </div>
            <div className="text-xs text-qq-sub">
              运行时长 <b className="text-qq-text">{fmtUptime(uptimeSeconds)}</b>
            </div>
            <div className="text-xs text-qq-sub">
              消息队列 <b className="text-qq-text">{status?.queuePending ?? '—'}</b> · 在线面板 <b className="text-qq-text">{status?.sseSubscribers ?? 0}</b>
            </div>
            <button className="ml-auto flex items-center gap-1 text-xs text-qq-blue hover:underline" onClick={load}>
              <IconRefresh size={13} /> 刷新
            </button>
          </div>
        </div>

        {/* 概览卡片 */}
        <div className="grid grid-cols-2 gap-3 sm:grid-cols-5">
          <StatCard label="群数量" value={totals?.groups ?? groups.length} />
          <StatCard label="累计消息" value={fmtTokens(totals?.messages || 0)} />
          <StatCard label="机器人回复" value={fmtTokens(totals?.botMessages || 0)} />
          <StatCard label="群友画像" value={fmtTokens(totals?.users || 0)} />
          <StatCard label="LLM 调用" value={fmtTokens(totals?.calls || 0)} />
        </div>

        {/* 缓存命中率 + DeepSeek 余额（两行分开显示） */}
        <div className="space-y-4">
          <div className="rounded-xl border border-qq-border bg-white p-4">
            <div className="mb-2 flex items-center justify-between">
              <div className="text-sm font-medium">💾 DeepSeek 磁盘缓存命中率（成本风控）</div>
              <div className="flex items-center gap-2">
                {alert && <Badge tone="red">低于告警阈值</Badge>}
                <span className={`text-lg font-semibold ${alert ? 'text-qq-red' : 'text-qq-green'}`}>{(hitRate * 100).toFixed(1)}%</span>
              </div>
            </div>
            <div className="relative h-2.5 overflow-hidden rounded-full bg-qq-bg">
              <div
                className={`h-full rounded-full transition-all ${alert ? 'bg-gradient-to-r from-qq-warn to-qq-red' : 'bg-gradient-to-r from-qq-blue to-qq-green'}`}
                style={{ width: `${Math.min(100, hitRate * 100)}%` }}
              />
              <div className="absolute top-0 h-full w-px bg-qq-text/60" style={{ left: `${threshold * 100}%` }} title={`告警阈值 ${(threshold * 100).toFixed(0)}%`} />
            </div>
            <div className="mt-1.5 flex justify-between text-[11px] text-qq-sub">
              <span>命中 {fmtTokens(totals?.hitTokens || 0)} tokens · 未命中 {fmtTokens(totals?.missTokens || 0)} tokens</span>
              <span>告警阈值 {(threshold * 100).toFixed(0)}%</span>
            </div>
          </div>

          <BalanceCard onAuthFail={onAuthFail} />
        </div>

        {/* MCP 工具（仅配置了 MCP server 时显示） */}
        {(health?.mcp?.servers?.length ?? 0) > 0 && (
          <div className="overflow-hidden rounded-xl border border-qq-border bg-white">
            <div className="flex items-center justify-between border-b border-qq-border/70 px-4 py-3">
              <div className="text-sm font-medium">🔌 MCP 工具服务</div>
              <span className="text-xs text-qq-sub">
                共 {health.mcp.toolCount} 个工具 · {health.mcp.servers.filter((s) => s.connected).length}/{health.mcp.servers.length} 已连接
              </span>
            </div>
            <div className="divide-y divide-qq-border/50">
              {health.mcp.servers.map((s) => (
                <div key={s.name} className="flex flex-wrap items-center gap-x-3 gap-y-1 px-4 py-2.5 text-[12.5px]">
                  <span className="font-medium">{s.name}</span>
                  <Badge tone={s.connected ? 'green' : 'red'}>{s.connected ? '已连接' : '失败'}</Badge>
                  <span className="text-qq-sub">{s.toolCount} 个工具</span>
                  {s.error && <span className="min-w-0 flex-1 truncate text-xs text-qq-red/80" title={s.error}>{s.error}</span>}
                </div>
              ))}
            </div>
          </div>
        )}

        {/* 每群统计 */}
        <div className="overflow-hidden rounded-xl border border-qq-border bg-white">
          <div className="border-b border-qq-border/70 px-4 py-3 text-sm font-medium">每群运行状态</div>
          {groupStats.length === 0 ? (
            <div className="px-4 py-10 text-center text-xs text-qq-sub">暂无统计数据（等机器人处理消息后出现）</div>
          ) : (
            <div className="overflow-x-auto">
            <table className="w-full min-w-[640px] text-[12.5px]">
              <thead>
                <tr className="bg-qq-bg/60 text-left text-xs text-qq-sub">
                  <th className="px-4 py-2 font-normal">群</th>
                  <th className="px-2 py-2 font-normal">消息</th>
                  <th className="px-2 py-2 font-normal">画像</th>
                  <th className="px-2 py-2 font-normal">LLM</th>
                  <th className="px-2 py-2 font-normal">命中率</th>
                  <th className="px-2 py-2 font-normal">Tokens</th>
                  <th className="px-4 py-2 text-right font-normal">操作</th>
                </tr>
              </thead>
              <tbody>
                {groupStats.map((g) => {
                  const meta = groups.find((x) => x.group === g.group);
                  const muted = !!meta?.muted;
                  const isSim = g.group === SIM_GROUP;
                  return (
                    <tr key={g.group} className="border-t border-qq-border/60 hover:bg-qq-hover/50">
                      <td className="px-4 py-2.5">
                        <div className="flex items-center gap-1.5">
                          <span className="max-w-[180px] truncate font-medium">{groupName(g.group)}</span>
                          {isSim && <Badge tone="blue">试聊</Badge>}
                          {!isSim && meta?.simEnabled && <Badge tone="orange">拦截</Badge>}
                          {muted && <Badge tone="gray">静默</Badge>}
                          {meta?.degraded && <Badge tone="red">降级</Badge>}
                        </div>
                        <div className="max-w-[200px] truncate font-mono text-[10px] text-qq-sub">{g.group}</div>
                      </td>
                      <td className="px-2 py-2.5">{g.messages}</td>
                      <td className="px-2 py-2.5">{g.users}</td>
                      <td className="px-2 py-2.5">{g.calls}</td>
                      <td className="px-2 py-2.5">
                        <div className="flex items-center gap-1.5">
                          <div className="h-1.5 w-16 overflow-hidden rounded-full bg-qq-bg">
                            <div
                              className={`h-full rounded-full ${g.hitRate < threshold ? 'bg-qq-warn' : 'bg-qq-green'}`}
                              style={{ width: `${Math.min(100, g.hitRate * 100)}%` }}
                            />
                          </div>
                          <span className="text-xs">{(g.hitRate * 100).toFixed(0)}%</span>
                        </div>
                      </td>
                      <td className="px-2 py-2.5 text-qq-sub">{fmtTokens(g.inputTokens)} / {fmtTokens(g.outputTokens)}</td>
                      <td className="px-4 py-2.5 text-right">
                        <button
                          className="mr-2 text-xs text-qq-blue hover:underline disabled:opacity-40"
                          disabled={busy === g.group}
                          onClick={() => doAction(g.group, muted ? 'unmute' : 'mute')}
                        >
                          {muted ? '解除静默' : '静默'}
                        </button>
                        <button
                          className="text-xs text-qq-warn hover:underline disabled:opacity-40"
                          disabled={busy === g.group}
                          onClick={() => doAction(g.group, 'reset')}
                        >
                          重置上下文
                        </button>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
            </div>
          )}
        </div>

        {/* 试聊拦截说明 */}
        {status?.simulationGroups?.length > 0 && (
          <div className="rounded-xl border border-[#ffe1b3] bg-[#fffaf0] p-4 text-xs leading-5 text-[#8c5a00]">
            ⚠️ 以下群的「试聊拦截」已开启，机器人回复只会显示在网页、不会发送到 QQ：
            <div className="mt-1 flex flex-wrap gap-1.5">
              {status.simulationGroups.map((g) => (
                <code key={g} className="rounded bg-white/80 px-1.5 py-0.5 font-mono">
                  {groupName(g)}
                </code>
              ))}
            </div>
            关闭方法：聊天页 → 该群右上角「⋯」菜单。
          </div>
        )}
      </div>
    </div>
  );
}

function StatCard({ label, value }) {
  return (
    <div className="rounded-xl border border-qq-border bg-white p-3.5">
      <div className="text-lg font-semibold">{value}</div>
      <div className="mt-0.5 text-xs text-qq-sub">{label}</div>
    </div>
  );
}

/**
 * DeepSeek 余额卡片：首次打开自动查询一次（服务端缓存），
 * 之后只在点击刷新按钮时重新查询；显示最后一次刷新时间。
 */
function BalanceCard({ onAuthFail }) {
  const [bal, setBal] = useState(null);
  const [loading, setLoading] = useState(false);

  const load = useCallback(
    async (refresh = false) => {
      setLoading(true);
      try {
        const d = await api(`/api/webui/deepseek/balance${refresh ? '?refresh=true' : ''}`);
        setBal(d);
      } catch (e) {
        if (e instanceof AuthError) onAuthFail?.();
        else toast(e.message, 'error');
      } finally {
        setLoading(false);
      }
    },
    [onAuthFail]
  );

  // 仅首次打开获取一次（服务端有缓存，不会重复打接口）
  useEffect(() => {
    load(false);
  }, [load]);

  const hasBalance = !!bal?.totalBalance;

  return (
    <div className="rounded-xl border border-qq-border bg-white p-4">
      <div className="mb-2 flex items-center justify-between">
        <div className="flex items-center gap-2 text-sm font-medium">
          💰 DeepSeek 余额
          {bal && (
            <Badge tone={bal.available ? 'green' : 'red'}>{bal.available ? '账户可用' : '不可用'}</Badge>
          )}
        </div>
        <button
          className="flex items-center gap-1 rounded-md border border-qq-border px-2.5 py-1 text-xs text-qq-text hover:border-qq-blue hover:text-qq-blue-deep disabled:opacity-40"
          onClick={() => load(true)}
          disabled={loading}
          title="重新查询余额"
        >
          {loading ? <Spinner className="h-3 w-3" /> : <IconRefresh size={13} />}
          刷新
        </button>
      </div>

      {bal?.error && !hasBalance ? (
        <div className="py-2">
          <div className="text-sm text-qq-red">{bal.error}</div>
          <div className="mt-1 text-[11px] text-qq-sub">请先在「配置 → LLM → API Key」填写 DeepSeek API Key，再点刷新重试。</div>
        </div>
      ) : hasBalance ? (
        <>
          <div className="flex items-baseline gap-1.5">
            <span className="text-[26px] font-semibold leading-none">{bal.totalBalance}</span>
            <span className="text-sm text-qq-sub">{bal.currency}</span>
          </div>
          <div className="mt-2 grid grid-cols-2 gap-2 text-xs text-qq-sub">
            <div className="rounded-lg bg-qq-bg px-2.5 py-1.5">
              充值余额 <span className="ml-1 font-medium text-qq-text">{bal.toppedUpBalance || '—'}</span>
            </div>
            <div className="rounded-lg bg-qq-bg px-2.5 py-1.5">
              赠送余额 <span className="ml-1 font-medium text-qq-text">{bal.grantedBalance || '—'}</span>
            </div>
          </div>
        </>
      ) : (
        <div className="py-2 text-sm text-qq-sub">{loading ? '查询中…' : '尚未查询余额'}</div>
      )}

      <div className="mt-2 text-[11px] text-qq-sub">
        最后刷新：{bal?.fetchedAt ? fmtFull(bal.fetchedAt) : '—'}
      </div>
    </div>
  );
}
