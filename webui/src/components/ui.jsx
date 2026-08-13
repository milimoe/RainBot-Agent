import { useEffect, useState } from 'react';
import { IconX } from './Icons.jsx';

// ---------- 轻量 UI 元件：Toggle / Badge / Spinner / EmptyState / Toasts ----------

export function Toggle({ checked, onChange, disabled = false }) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      disabled={disabled}
      onClick={() => onChange?.(!checked)}
      className={`relative inline-flex h-[22px] w-[42px] shrink-0 items-center rounded-full transition-colors ${
        checked ? 'bg-qq-blue' : 'bg-[#dcdfe4]'
      } ${disabled ? 'opacity-50 cursor-not-allowed' : 'cursor-pointer'}`}
    >
      <span
        className={`inline-block h-[18px] w-[18px] transform rounded-full bg-white shadow transition-transform ${
          checked ? 'translate-x-[21px]' : 'translate-x-[2px]'
        }`}
      />
    </button>
  );
}

export function Badge({ children, tone = 'blue' }) {
  const tones = {
    blue: 'bg-[#e6f4ff] text-qq-blue-deep',
    gray: 'bg-[#f0f2f5] text-qq-sub',
    orange: 'bg-[#fff4e6] text-[#d46b08]',
    red: 'bg-[#fff1f0] text-qq-red',
    green: 'bg-[#f0fff5] text-qq-green',
  };
  return (
    <span className={`inline-flex items-center rounded px-1.5 py-px text-[11px] leading-4 ${tones[tone]}`}>
      {children}
    </span>
  );
}

export function Spinner({ className = '' }) {
  return (
    <span
      className={`inline-block h-4 w-4 animate-spin rounded-full border-2 border-qq-blue border-t-transparent ${className}`}
    />
  );
}

export function EmptyState({ icon = '☔', title, desc }) {
  return (
    <div className="flex h-full flex-col items-center justify-center gap-2 text-center text-qq-sub">
      <div className="text-4xl">{icon}</div>
      <div className="text-sm">{title}</div>
      {desc && <div className="max-w-xs text-xs leading-5">{desc}</div>}
    </div>
  );
}

// ---------- Toast（window 事件驱动，全局单例渲染） ----------

export function toast(msg, type = 'info') {
  window.dispatchEvent(
    new CustomEvent('webui:toast', { detail: { id: `${Date.now()}-${Math.random()}`, msg, type } })
  );
}

export function Toasts() {
  const [list, setList] = useState([]);
  useEffect(() => {
    const h = (e) => {
      const t = e.detail;
      setList((l) => [...l, t]);
      setTimeout(() => setList((l) => l.filter((x) => x.id !== t.id)), 3200);
    };
    window.addEventListener('webui:toast', h);
    return () => window.removeEventListener('webui:toast', h);
  }, []);

  const tone = (type) =>
    type === 'error' ? 'bg-qq-red' : type === 'success' ? 'bg-qq-green' : 'bg-[#333c47]';

  return (
    <div className="pointer-events-none fixed left-1/2 top-4 z-[200] flex -translate-x-1/2 flex-col items-center gap-2">
      {list.map((t) => (
        <div
          key={t.id}
          className={`flex items-center gap-2 rounded-lg px-4 py-2 text-sm text-white shadow-lg ${tone(t.type)}`}
        >
          <span className="max-w-md break-all">{t.msg}</span>
          <button className="pointer-events-auto opacity-70 hover:opacity-100" onClick={() => setList((l) => l.filter((x) => x.id !== t.id))}>
            <IconX />
          </button>
        </div>
      ))}
    </div>
  );
}
