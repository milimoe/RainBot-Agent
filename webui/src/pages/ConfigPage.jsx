import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { api, AuthError } from '../lib/api.js';
import { Badge, Toggle, toast } from '../components/ui.jsx';
import { IconSearch } from '../components/Icons.jsx';

const SECTION_TITLES = {
  LLM: '🤖 LLM 大模型',
  触发: '⚡ 触发与节流',
  画像: '👥 群友画像',
  上下文: '🧠 上下文与缓存',
  风控: '🛡️ 风控与合规',
  随机互动: '🎲 随机互动',
  搜索: '🔍 联网搜索',
  通用: '⚙️ 通用',
};

/** 把后端 string 值转换为控件展示值 */
function toDisplay(item) {
  switch (item.meta?.type) {
    case 'percent':
      return String(Math.round(Number(item.value) * 100));
    case 'stringlist':
      try {
        return JSON.parse(item.value || '[]').join('\n');
      } catch {
        return item.value || '';
      }
    default:
      return item.value || '';
  }
}

/** 把控件展示值转换为后端 SetAsync 需要的 string 值 */
function toWire(item, display) {
  switch (item.meta?.type) {
    case 'percent':
      return String(Number(display) / 100);
    case 'stringlist':
      return JSON.stringify(
        display
          .split('\n')
          .map((s) => s.trim())
          .filter(Boolean)
      );
    default:
      return display.trim();
  }
}

/**
 * 配置页：全部可热改参数，分组卡片 + 类型化控件 + 一键保存 / 恢复默认。
 */
export default function ConfigPage({ onAuthFail }) {
  const [items, setItems] = useState([]);
  const [loading, setLoading] = useState(true);
  const [query, setQuery] = useState('');
  const [values, setValues] = useState({});
  const [dirty, setDirty] = useState(new Set());
  const [busyKey, setBusyKey] = useState(null);
  const sectionRefs = useRef({});

  const load = useCallback(async () => {
    try {
      const data = await api('/api/webui/config');
      setItems(data.items || []);
      const v = {};
      for (const it of data.items) v[it.key] = toDisplay(it);
      setValues(v);
      setDirty(new Set());
    } catch (e) {
      if (e instanceof AuthError) onAuthFail?.();
      else toast(e.message, 'error');
    } finally {
      setLoading(false);
    }
  }, [onAuthFail]);

  useEffect(() => {
    load();
  }, [load]);

  const setValue = (key, display) => {
    setValues((v) => ({ ...v, [key]: display }));
    setDirty((d) => new Set(d).add(key));
  };

  const save = async (item) => {
    setBusyKey(item.key);
    try {
      await api(`/api/webui/config/${encodeURIComponent(item.key)}`, {
        method: 'PUT',
        body: { value: toWire(item, values[item.key]) },
      });
      toast(`${item.meta?.label || item.key} 已保存`, 'success');
      setDirty((d) => {
        const n = new Set(d);
        n.delete(item.key);
        return n;
      });
      await load();
    } catch (e) {
      toast(e.message, 'error');
    } finally {
      setBusyKey(null);
    }
  };

  const reset = async (item) => {
    if (!window.confirm(`恢复「${item.meta?.label || item.key}」为默认值？`)) return;
    setBusyKey(item.key);
    try {
      await api(`/api/webui/config/${encodeURIComponent(item.key)}`, { method: 'DELETE' });
      toast(`${item.meta?.label || item.key} 已恢复默认`, 'success');
      await load();
    } catch (e) {
      toast(e.message, 'error');
    } finally {
      setBusyKey(null);
    }
  };

  const sections = useMemo(() => {
    const map = {};
    for (const it of items) {
      const sec = it.meta?.section || '通用';
      (map[sec] = map[sec] || []).push(it);
    }
    // 已登记的分组按固定顺序在前；后端新增但前端未登记的分组按字典序追加，
    // 用分组名当标题兜底 —— 后端加新分组时前端不改也能显示出来。
    const known = Object.keys(SECTION_TITLES).filter((s) => map[s]);
    const unknown = Object.keys(map)
      .filter((s) => !SECTION_TITLES[s])
      .sort();
    return [...known, ...unknown].map((s) => ({ section: s, title: SECTION_TITLES[s] ?? s, items: map[s] }));
  }, [items]);

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return sections;
    return sections
      .map((s) => ({
        ...s,
        items: s.items.filter(
          (it) =>
            it.key.toLowerCase().includes(q) ||
            (it.meta?.label || '').toLowerCase().includes(q) ||
            (it.meta?.description || '').toLowerCase().includes(q)
        ),
      }))
      .filter((s) => s.items.length > 0);
  }, [sections, query]);

  if (loading) {
    return <div className="flex flex-1 items-center justify-center text-sm text-qq-sub">加载配置中…</div>;
  }

  return (
    <div className="flex h-full min-w-0 flex-1">
      {/* 分区导航（移动端隐藏，改为顶部横向 chips） */}
      <div className="qq-scroll hidden w-[168px] shrink-0 overflow-y-auto border-r border-qq-border bg-qq-panel py-4 md:block">
        <div className="px-3 pb-3 text-xs font-medium text-qq-sub">配置分区</div>
        {sections.map((s) => (
          <button
            key={s.section}
            className="block w-full px-4 py-2 text-left text-[13px] text-qq-text hover:bg-qq-hover"
            onClick={() => sectionRefs.current[s.section]?.scrollIntoView({ behavior: 'smooth', block: 'start' })}
          >
            {s.title}
            <span className="ml-1.5 text-[11px] text-qq-sub">{s.items.length}</span>
          </button>
        ))}
      </div>

      {/* 配置卡片 */}
      <div className="qq-scroll min-w-0 flex-1 overflow-y-auto">
        <div className="sticky top-0 z-10 bg-qq-bg/95 px-3 pb-3 pt-3 backdrop-blur md:px-6 md:pt-4">
          {/* 移动端分区 chips */}
          <div className="qq-scroll mb-2 flex gap-1.5 overflow-x-auto md:hidden">
            {sections.map((s) => (
              <button
                key={s.section}
                className="shrink-0 rounded-full border border-qq-border bg-white px-3 py-1 text-xs text-qq-text"
                onClick={() => sectionRefs.current[s.section]?.scrollIntoView({ behavior: 'smooth', block: 'start' })}
              >
                {s.title}
              </button>
            ))}
          </div>
          <div className="flex items-center gap-2 rounded-md border border-qq-border bg-white px-3 py-1.5 shadow-sm">
            <span className="text-qq-sub">
              <IconSearch size={14} />
            </span>
            <input
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              placeholder="搜索参数名 / 说明…"
              className="w-full bg-transparent text-[13px] outline-none"
            />
          </div>
        </div>

        <div className="space-y-5 px-3 pb-10 pt-1 md:px-6">
          {filtered.map((s) => (
            <div key={s.section} ref={(el) => (sectionRefs.current[s.section] = el)} className="scroll-mt-16">
              <div className="mb-2 text-[15px] font-medium">{s.title}</div>
              <div className="overflow-hidden rounded-xl border border-qq-border bg-white">
                {s.items.map((it, idx) => (
                  <ConfigRow
                    key={it.key}
                    item={it}
                    display={values[it.key] ?? ''}
                    isDirty={dirty.has(it.key)}
                    busy={busyKey === it.key}
                    onChange={(v) => setValue(it.key, v)}
                    onSave={() => save(it)}
                    onReset={() => reset(it)}
                    last={idx === s.items.length - 1}
                  />
                ))}
              </div>
            </div>
          ))}
          {filtered.length === 0 && <div className="py-16 text-center text-sm text-qq-sub">没有匹配的参数</div>}
        </div>
      </div>
    </div>
  );
}

function ConfigRow({ item, display, isDirty, busy, onChange, onSave, onReset, last }) {
  const meta = item.meta || {};
  const type = meta.type || 'text';
  const inputCls =
    'w-full rounded-md border border-qq-border bg-white px-2.5 py-1.5 text-[13px] outline-none focus:border-qq-blue focus:ring-1 focus:ring-qq-blue/30 disabled:opacity-50 sm:w-72';

  let control;
  if (type === 'bool') {
    control = <Toggle checked={display === 'true'} onChange={(v) => onChange(String(v))} />;
  } else if (type === 'percent') {
    control = (
      <div className="flex w-full items-center gap-2 sm:w-52">
        <input
          type="range"
          min={0}
          max={100}
          step={1}
          value={display}
          onChange={(e) => onChange(e.target.value)}
          className="w-full accent-qq-blue"
        />
        <span className={`w-11 text-right text-[13px] font-medium ${display === '0' ? 'text-qq-red' : 'text-qq-text'}`}>
          {display}%
        </span>
      </div>
    );
  } else if (type === 'secret') {
    control = (
      <div className="relative w-full sm:w-72">
        <input type="password" value={display} onChange={(e) => onChange(e.target.value)} className={`${inputCls} pr-8`} placeholder="••••••••" />
        <span className="absolute right-2 top-1/2 -translate-y-1/2 text-[10px] text-qq-sub">密钥</span>
      </div>
    );
  } else if (type === 'stringlist') {
    control = (
      <textarea
        value={display}
        onChange={(e) => onChange(e.target.value)}
        rows={Math.min(6, Math.max(2, display.split('\n').length + 1))}
        className={`${inputCls} qq-scroll resize-y font-mono text-xs leading-5`}
        placeholder="每行一个 URL"
      />
    );
  } else {
    control = (
      <input
        type={type === 'number' || type === 'double' ? 'number' : 'text'}
        min={meta.min}
        max={meta.max}
        step={meta.step || (type === 'double' ? 0.1 : 1)}
        value={display}
        onChange={(e) => onChange(e.target.value)}
        className={inputCls}
      />
    );
  }

  return (
    <div className={`flex flex-col gap-2 px-4 py-3 sm:flex-row sm:items-start sm:gap-4 ${last ? '' : 'border-b border-qq-border/70'} ${isDirty ? 'bg-[#f0f9ff]' : ''}`}>
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-1.5">
          <span className="text-[13.5px] font-medium">{meta.label || item.key}</span>
          {item.overridden && <Badge tone="orange">已覆盖</Badge>}
          {isDirty && <Badge tone="blue">未保存</Badge>}
        </div>
        <div className="mt-0.5 text-xs leading-4 text-qq-sub">{meta.description || ''}</div>
        <div className="mt-1 font-mono text-[11px] text-qq-sub/70">{item.key}</div>
      </div>
      <div className="flex shrink-0 items-center gap-2 sm:pt-0.5">{control}</div>
      <div className="flex shrink-0 items-center gap-2 sm:w-[104px] sm:flex-col sm:items-end sm:pt-0.5">
        {isDirty && (
          <button
            className="rounded-md bg-qq-blue px-3 py-1 text-xs text-white hover:bg-qq-blue-deep disabled:opacity-50"
            onClick={onSave}
            disabled={busy}
          >
            {busy ? '保存中…' : '保存'}
          </button>
        )}
        {item.overridden && (
          <button
            className="rounded-md border border-qq-border px-3 py-1 text-xs text-qq-sub hover:border-qq-red hover:text-qq-red disabled:opacity-50"
            onClick={onReset}
            disabled={busy}
          >
            恢复默认
          </button>
        )}
      </div>
    </div>
  );
}
