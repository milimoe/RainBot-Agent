import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { api, AuthError } from '../lib/api.js';
import { fmtFull } from '../lib/util.js';
import { IconPause, IconPlay } from '../components/Icons.jsx';
import { toast } from '../components/ui.jsx';

const LEVELS = ['All', 'Trace', 'Debug', 'Info', 'Warn', 'Error', 'Critical'];

const LEVEL_COLOR = {
  Trace: 'text-qq-sub',
  Debug: 'text-qq-sub',
  Info: 'text-qq-blue-deep',
  Warn: 'text-qq-warn',
  Error: 'text-qq-red',
  Critical: 'text-[#8b00ff]',
};

/**
 * 日志页：轮询 /api/webui/logs，展示与 ILogger 同源的日志输出。
 * 支持级别过滤 / 关键字搜索 / 暂停 / 异常详情展开。
 */
export default function LogsPage({ onAuthFail }) {
  const [entries, setEntries] = useState([]);
  const [seq, setSeq] = useState(0);
  const [paused, setPaused] = useState(false);
  const [level, setLevel] = useState('All');
  const [query, setQuery] = useState('');
  const [expanded, setExpanded] = useState(null);
  const [connected, setConnected] = useState(false);

  const listRef = useRef(null);
  const stickRef = useRef(true);

  const load = useCallback(async () => {
    try {
      const data = await api(`/api/webui/logs?after=${seq}&limit=500`);
      setConnected(true);
      setSeq(data.seq);
      if ((data.entries || []).length > 0) {
        setEntries((prev) => {
          const seen = new Set(prev.map((e) => e.seq));
          return [...prev, ...data.entries.filter((e) => !seen.has(e.seq))].slice(-2000);
        });
      }
    } catch (e) {
      if (e instanceof AuthError) onAuthFail?.();
      else setConnected(false);
    }
  }, [seq, onAuthFail]);

  // 首次加载后每 2 秒轮询（暂停时跳过）
  useEffect(() => {
    load();
    const interval = setInterval(() => {
      if (!paused) load();
    }, 2000);
    return () => clearInterval(interval);
  }, [load, paused]);

  const handleScroll = () => {
    const el = listRef.current;
    if (!el) return;
    stickRef.current = el.scrollHeight - el.scrollTop - el.clientHeight < 60;
  };
  useEffect(() => {
    const el = listRef.current;
    if (el && stickRef.current) el.scrollTop = el.scrollHeight;
  }, [entries]);

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    return entries.filter((e) => {
      if (level !== 'All' && e.level !== level) return false;
      if (!q) return true;
      return (
        e.category.toLowerCase().includes(q) ||
        (e.message || '').toLowerCase().includes(q) ||
        (e.exception || '').toLowerCase().includes(q)
      );
    });
  }, [entries, level, query]);

  return (
    <div className="flex h-full min-w-0 flex-1 flex-col bg-qq-panel">
      {/* 工具栏 */}
      <div className="flex shrink-0 flex-wrap items-center gap-2 border-b border-qq-border px-3 py-2">
        <div className="flex items-center gap-1">
          {LEVELS.map((lv) => (
            <button
              key={lv}
              onClick={() => setLevel(lv)}
              className={`rounded px-2 py-0.5 text-xs transition-colors ${
                level === lv ? 'bg-qq-blue text-white' : 'bg-qq-bg text-qq-sub hover:text-qq-text'
              }`}
            >
              {lv}
            </button>
          ))}
        </div>
        <input
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          placeholder="搜索类别 / 内容…"
          className="min-w-0 flex-1 rounded-md border border-qq-border bg-white px-2.5 py-1 text-xs outline-none focus:border-qq-blue sm:max-w-xs"
        />
        <div className="ml-auto flex items-center gap-1.5">
          <span className="hidden text-[11px] text-qq-sub sm:inline">
            {connected ? `已捕获 ${entries.length} 条` : '连接中…'}
          </span>
          <button
            className="rounded border border-qq-border px-2 py-1 text-[11px] text-qq-sub hover:border-qq-blue hover:text-qq-blue-deep"
            onClick={() => {
              setEntries([]);
              setExpanded(null);
            }}
          >
            清空
          </button>
          <button
            className="flex items-center gap-1 rounded border border-qq-border px-2 py-1 text-[11px] text-qq-sub hover:border-qq-blue hover:text-qq-blue-deep"
            onClick={() => setPaused((v) => !v)}
          >
            {paused ? <IconPlay size={11} /> : <IconPause size={11} />}
            {paused ? '继续' : '暂停'}
          </button>
        </div>
      </div>

      {/* 日志列表 */}
      <div ref={listRef} onScroll={handleScroll} className="qq-scroll min-h-0 flex-1 overflow-auto bg-[#fafbfc]">
        {filtered.length === 0 ? (
          <div className="flex h-full items-center justify-center text-sm text-qq-sub">
            {paused ? '已暂停' : '暂无日志'}
          </div>
        ) : (
          <table className="w-full min-w-[560px] border-collapse font-mono text-[11.5px] leading-5">
            <tbody>
              {filtered.map((e) => (
                <LogRow
                  key={e.seq}
                  entry={e}
                  expanded={expanded === e.seq}
                  onToggle={() => setExpanded(expanded === e.seq ? null : e.seq)}
                />
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
}

function LogRow({ entry, expanded, onToggle }) {
  const hasException = !!entry.exception;
  return (
    <>
      <tr
        className={`cursor-pointer border-b border-qq-border/50 align-top hover:bg-qq-hover/50 ${expanded ? 'bg-qq-hover/60' : ''}`}
        onClick={onToggle}
        title={hasException ? '点击展开异常详情' : undefined}
      >
        <td className="w-24 shrink-0 whitespace-nowrap px-2 py-1 text-qq-sub">{fmtFull(entry.time).slice(5)}</td>
        <td className={`w-16 shrink-0 whitespace-nowrap px-1 py-1 font-semibold ${LEVEL_COLOR[entry.level] || 'text-qq-sub'}`}>
          {entry.level}
        </td>
        <td className="w-64 max-w-64 truncate px-1 py-1 text-qq-sub" title={entry.category}>
          {entry.category}
        </td>
        <td className="whitespace-pre-wrap break-all px-2 py-1 text-qq-text">
          {entry.message}
          {hasException && <span className="ml-1 text-qq-warn">ⓘ</span>}
        </td>
      </tr>
      {expanded && hasException && (
        <tr className="border-b border-qq-border/50 bg-[#fff7f7]">
          <td colSpan={4} className="whitespace-pre-wrap break-all px-4 py-2 text-[11px] leading-5 text-qq-red/90">
            {entry.exception}
          </td>
        </tr>
      )}
    </>
  );
}
