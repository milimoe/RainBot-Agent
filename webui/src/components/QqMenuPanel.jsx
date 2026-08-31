import { useCallback, useEffect, useState } from 'react';
import { api } from '../lib/api.js';
import { Badge, Toggle, toast } from './ui.jsx';
import { IconPlus, IconX, IconRefresh } from './Icons.jsx';

/**
 * QQ 官方机器人「自定义菜单 + 指令面板」配置卡片（官方文档：
 * https://bot.q.qq.com/wiki/develop/api-v2/server-inter/menu-panel/）。
 * 仅 QqOfficial 平台实例显示；按实例凭据调用官方 API。
 * 表单字段统一使用官方 snake_case（与后端模型一致）。
 */

const MENU_TYPES = [
  { value: 'send_message', label: '发送消息', hint: '点击后文本自动填入输入框' },
  { value: 'link', label: '链接跳转', hint: '跳转 https:// 链接' },
  { value: 'switch', label: '开关', hint: '用户切换时携带标识消息' },
  { value: 'menu', label: '子菜单', hint: '折叠的子菜单（最多 5 项）' },
];
const SUB_TYPES = [
  { value: 'send_message', label: '发送消息' },
  { value: 'link', label: '链接跳转' },
];
const PANEL_SCOPES = [
  { value: 'c2c', label: '单聊（c2c）' },
  { value: 'group', label: '群聊（group）' },
  { value: 'channel', label: '文字子频道（channel）' },
  { value: 'dm', label: '频道私信（dm）' },
];

const inputCls =
  'w-full rounded-md border border-qq-border bg-white px-2.5 py-1.5 text-[13px] outline-none focus:border-qq-blue disabled:opacity-50';

function SectionCard({ title, desc, right, children }) {
  return (
    <div className="rounded-xl border border-qq-border bg-white p-4">
      <div className="mb-3 flex items-center justify-between">
        <div>
          <div className="text-[13px] font-medium">{title}</div>
          {desc ? <div className="mt-0.5 text-[11px] leading-snug text-qq-sub">{desc}</div> : null}
        </div>
        {right}
      </div>
      {children}
    </div>
  );
}

function Field({ label, hint, children, wide = false }) {
  return (
    <label className={`block ${wide ? 'col-span-2' : ''}`}>
      <div className="mb-1 text-xs font-medium text-qq-sub">{label}</div>
      {children}
      {hint ? <div className="mt-1 text-[11px] leading-snug text-qq-sub">{hint}</div> : null}
    </label>
  );
}

// ===================================================================
// 自定义菜单（单聊窗口底部按钮）
// ===================================================================

const emptyButton = () => ({
  type: 'send_message',
  name: '',
  sub_menu_items: [],
  send_message: '',
  link: '',
  switch: { switch_id: '', default: false },
});

export function QqMenuCard({ botId }) {
  const [items, setItems] = useState([]);
  const [loaded, setLoaded] = useState(false);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    setBusy(true);
    try {
      const d = await api(`/api/webui/bots/${encodeURIComponent(botId)}/menu`);
      setItems(d?.menu?.items || []);
      setLoaded(true);
    } catch (e) {
      toast(e.message, 'error');
    } finally {
      setBusy(false);
    }
  }, [botId]);

  useEffect(() => {
    load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [botId]);

  const patch = (i, key, value) =>
    setItems((list) => list.map((it, idx) => (idx === i ? { ...it, [key]: value } : it)));

  const patchSub = (i, j, key, value) =>
    setItems((list) =>
      list.map((it, idx) =>
        idx === i
          ? { ...it, sub_menu_items: it.sub_menu_items.map((s, k) => (k === j ? { ...s, [key]: value } : s)) }
          : it
      )
    );

  const addItem = () => setItems((l) => [...l, emptyButton()]);
  const removeItem = (i) => setItems((l) => l.filter((_, idx) => idx !== i));
  const addSub = (i) =>
    setItems((l) =>
      l.map((it, idx) =>
        idx === i ? { ...it, sub_menu_items: [...(it.sub_menu_items || []), { type: 'send_message', name: '', send_message: '' }] } : it
      )
    );
  const removeSub = (i, j) =>
    setItems((l) => l.map((it, idx) => (idx === i ? { ...it, sub_menu_items: it.sub_menu_items.filter((_, k) => k !== j) } : it)));

  const save = async () => {
    setBusy(true);
    try {
      await api(`/api/webui/bots/${encodeURIComponent(botId)}/menu`, { method: 'PUT', body: { menu: { items } } });
      toast('自定义菜单已保存（对所有用户生效）', 'success');
      await load();
    } catch (e) {
      toast(e.message, 'error');
    } finally {
      setBusy(false);
    }
  };

  const clear = async () => {
    if (!window.confirm('清空自定义菜单？所有用户单聊窗口底部的按钮将全部移除。')) return;
    setBusy(true);
    try {
      await api(`/api/webui/bots/${encodeURIComponent(botId)}/menu`, { method: 'PUT', body: { menu: { items: [] } } });
      toast('自定义菜单已清空', 'success');
      await load();
    } catch (e) {
      toast(e.message, 'error');
    } finally {
      setBusy(false);
    }
  };

  return (
    <SectionCard
      title="📋 自定义菜单（单聊窗口底部）"
      desc="设置后对所有用户生效；最多 10 个一级按钮，子菜单最多 5 项且不可再嵌套"
      right={
        <div className="flex items-center gap-2">
          <button className="flex items-center gap-1 rounded-md border border-qq-border px-2 py-1 text-[11px] hover:bg-qq-hover disabled:opacity-50" onClick={load} disabled={busy}>
            <IconRefresh size={12} /> 刷新
          </button>
          <button className="rounded-md bg-qq-blue px-3 py-1 text-xs text-white hover:bg-qq-blue-deep disabled:opacity-50" onClick={save} disabled={busy || !loaded}>
            {busy ? '处理中…' : '保存菜单'}
          </button>
        </div>
      }
    >
      {!loaded ? (
        <div className="text-[13px] text-qq-sub">加载中…</div>
      ) : items.length === 0 ? (
        <div className="rounded-lg border border-dashed border-qq-border bg-qq-bg/40 p-4 text-center text-xs text-qq-sub">
          当前未设置菜单 —— 点下方「添加按钮」开始配置
        </div>
      ) : (
        <div className="space-y-2.5">
          {items.map((it, i) => (
            <div key={i} className="rounded-lg border border-qq-border p-3">
              <div className="grid grid-cols-2 gap-2.5 md:grid-cols-4">
                <Field label="按钮名称" hint="最多 10 字符（中文汉字算 2 个）">
                  <input className={inputCls} value={it.name} onChange={(e) => patch(i, 'name', e.target.value)} placeholder="如 帮助" />
                </Field>
                <Field label="类型">
                  <select className={inputCls} value={it.type} onChange={(e) => patch(i, 'type', e.target.value)}>
                    {MENU_TYPES.map((t) => (
                      <option key={t.value} value={t.value}>{t.label}</option>
                    ))}
                  </select>
                </Field>
                {it.type === 'send_message' && (
                  <Field label="发送内容" wide>
                    <input className={inputCls} value={it.send_message || ''} onChange={(e) => patch(i, 'send_message', e.target.value)} placeholder="如 /help" />
                  </Field>
                )}
                {it.type === 'link' && (
                  <Field label="跳转链接" hint="必须以 https:// 开头" wide>
                    <input className={inputCls} value={it.link || ''} onChange={(e) => patch(i, 'link', e.target.value)} placeholder="https://" />
                  </Field>
                )}
                {it.type === 'switch' && (
                  <>
                    <Field label="开关标识" hint="用户打开后消息 ext 携带 {id}=1">
                      <input className={inputCls} value={it.switch?.switch_id || ''} onChange={(e) => patch(i, 'switch', { ...it.switch, switch_id: e.target.value })} placeholder="如 search" />
                    </Field>
                    <Field label="默认状态">
                      <div className="py-1">
                        <Toggle checked={!!it.switch?.default} onChange={(v) => patch(i, 'switch', { ...it.switch, default: v })} />
                      </div>
                    </Field>
                  </>
                )}
                <div className="flex items-end justify-end gap-1.5 pb-0.5">
                  {it.type === 'menu' && (
                    <button className="flex items-center gap-1 rounded-md border border-qq-border px-2 py-1 text-[11px] hover:bg-qq-hover" onClick={() => addSub(i)}>
                      <IconPlus size={11} /> 子菜单
                    </button>
                  )}
                  <button className="rounded-md border border-red-200 px-2 py-1 text-[11px] text-red-600 hover:bg-red-50" onClick={() => removeItem(i)}>
                    删除
                  </button>
                </div>
              </div>

              {it.type === 'menu' && (
                <div className="mt-2.5 space-y-2 rounded-lg bg-qq-bg/50 p-2.5">
                  {(it.sub_menu_items || []).length === 0 && <div className="text-[11px] text-qq-sub">暂无子菜单（最多 5 项）</div>}
                  {(it.sub_menu_items || []).map((s, j) => (
                    <div key={j} className="grid grid-cols-2 gap-2.5 md:grid-cols-4">
                      <Field label="子菜单名称">
                        <input className={inputCls} value={s.name} onChange={(e) => patchSub(i, j, 'name', e.target.value)} />
                      </Field>
                      <Field label="类型">
                        <select className={inputCls} value={s.type} onChange={(e) => patchSub(i, j, 'type', e.target.value)}>
                          {SUB_TYPES.map((t) => (
                            <option key={t.value} value={t.value}>{t.label}</option>
                          ))}
                        </select>
                      </Field>
                      {s.type === 'send_message' ? (
                        <Field label="发送内容">
                          <input className={inputCls} value={s.send_message || ''} onChange={(e) => patchSub(i, j, 'send_message', e.target.value)} />
                        </Field>
                      ) : (
                        <Field label="链接">
                          <input className={inputCls} value={s.link || ''} onChange={(e) => patchSub(i, j, 'link', e.target.value)} />
                        </Field>
                      )}
                      <div className="flex items-end justify-end pb-0.5">
                        <button className="rounded-md border border-red-200 px-2 py-1 text-[11px] text-red-600 hover:bg-red-50" onClick={() => removeSub(i, j)}>
                          删除
                        </button>
                      </div>
                    </div>
                  ))}
                </div>
              )}
            </div>
          ))}
        </div>
      )}

      <div className="mt-3 flex items-center justify-between">
        <button
          className="flex items-center gap-1 rounded-md border border-qq-border px-2.5 py-1.5 text-xs hover:bg-qq-hover disabled:opacity-50"
          onClick={addItem}
          disabled={busy || items.length >= 10}
        >
          <IconPlus size={12} /> 添加按钮（{items.length}/10）
        </button>
        {items.length > 0 && (
          <button className="rounded-md border border-red-200 px-2.5 py-1.5 text-xs text-red-600 hover:bg-red-50" onClick={clear} disabled={busy}>
            清空菜单
          </button>
        )}
      </div>
    </SectionCard>
  );
}

// ===================================================================
// 指令面板
// ===================================================================

export function QqPanelsCard({ botId }) {
  const [scope, setScope] = useState('c2c');
  const [panels, setPanels] = useState([]);
  const [loaded, setLoaded] = useState(false);
  const [busy, setBusy] = useState(false);
  const [editor, setEditor] = useState(null); // null | { mode: 'create' } | { mode: 'edit', panelId, form }

  const load = useCallback(async () => {
    setBusy(true);
    try {
      const d = await api(`/api/webui/bots/${encodeURIComponent(botId)}/panels?scope=${encodeURIComponent(scope)}`);
      setPanels(d?.records || []);
      setLoaded(true);
    } catch (e) {
      toast(e.message, 'error');
    } finally {
      setBusy(false);
    }
  }, [botId, scope]);

  useEffect(() => {
    load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [botId, scope]);

  const openCreate = () => setEditor({ mode: 'create' });
  const openEdit = async (panelId) => {
    try {
      const d = await api(`/api/webui/bots/${encodeURIComponent(botId)}/panels/${encodeURIComponent(panelId)}`);
      setEditor({
        mode: 'edit',
        panelId,
        form: {
          scope: d?.scope || 'c2c',
          target_type: d?.target_type || 'all',
          user_openids: '',
          group_openids: '',
          remark: d?.panel?.remark || '',
          items: d?.panel?.items || [],
        },
      });
    } catch (e) {
      toast(e.message, 'error');
    }
  };

  const remove = async (panelId) => {
    if (!window.confirm(`删除面板 ${panelId}？`)) return;
    setBusy(true);
    try {
      await api(`/api/webui/bots/${encodeURIComponent(botId)}/panels/${encodeURIComponent(panelId)}`, { method: 'DELETE' });
      toast('面板已删除', 'success');
      await load();
    } catch (e) {
      toast(e.message, 'error');
    } finally {
      setBusy(false);
    }
  };

  const fmtTime = (t) => (t ? new Date(t).toLocaleString() : '—');

  return (
    <SectionCard
      title="🗂️ 指令面板"
      desc="以面板形式展示指令/链接；支持按单聊/群聊/文字子频道/频道私信场景生效（最多 20 个面板）"
      right={
        <div className="flex items-center gap-2">
          <select
            className="rounded-md border border-qq-border bg-white px-2 py-1 text-xs outline-none focus:border-qq-blue"
            value={scope}
            onChange={(e) => setScope(e.target.value)}
          >
            {PANEL_SCOPES.map((s) => (
              <option key={s.value} value={s.value}>{s.label}</option>
            ))}
          </select>
          <button
            className="flex items-center gap-1 rounded-md border border-qq-border px-2 py-1 text-[11px] hover:bg-qq-hover disabled:opacity-50"
            onClick={load}
            disabled={busy}
          >
            <IconRefresh size={12} /> 刷新
          </button>
          <button
            className="flex items-center gap-1 rounded-md bg-qq-blue px-3 py-1 text-xs text-white hover:bg-qq-blue-deep disabled:opacity-50"
            onClick={openCreate}
            disabled={busy}
          >
            <IconPlus size={12} /> 新建面板
          </button>
        </div>
      }
    >
      {!loaded ? (
        <div className="text-[13px] text-qq-sub">加载中…</div>
      ) : panels.length === 0 ? (
        <div className="rounded-lg border border-dashed border-qq-border bg-qq-bg/40 p-4 text-center text-xs text-qq-sub">
          「{PANEL_SCOPES.find((s) => s.value === scope)?.label}」场景暂无面板
        </div>
      ) : (
        <div className="space-y-2">
          {panels.map((p) => (
            <div key={p.panel_id} className="flex items-center gap-3 rounded-lg border border-qq-border px-3 py-2">
              <div className="min-w-0 flex-1">
                <div className="flex flex-wrap items-center gap-1.5">
                  <span className="font-mono text-[12px] font-medium">{p.panel_id}</span>
                  {p.target_type === 'specific' && <Badge tone="orange">指定生效</Badge>}
                  <Badge tone="gray">{p.panel?.items?.length || 0} 项</Badge>
                </div>
                <div className="mt-0.5 truncate text-[11px] text-qq-sub">
                  {p.panel?.remark ? `备注：${p.panel.remark} · ` : ''}更新于 {fmtTime(p.updated_at)}
                </div>
              </div>
              <button className="shrink-0 rounded-md border border-qq-border px-2 py-1 text-[11px] hover:bg-qq-hover" onClick={() => openEdit(p.panel_id)}>
                编辑
              </button>
              <button className="shrink-0 rounded-md border border-red-200 px-2 py-1 text-[11px] text-red-600 hover:bg-red-50" onClick={() => remove(p.panel_id)}>
                删除
              </button>
            </div>
          ))}
        </div>
      )}

      {editor && (
        <PanelEditor
          botId={botId}
          editor={editor}
          busy={busy}
          onClose={() => setEditor(null)}
          onSaved={async () => {
            setEditor(null);
            await load();
          }}
        />
      )}
    </SectionCard>
  );
}

function PanelEditor({ botId, editor, busy, onClose, onSaved }) {
  const isEdit = editor.mode === 'edit';
  const [form, setForm] = useState(
    isEdit
      ? editor.form
      : {
          scope: 'c2c',
          target_type: 'all',
          user_openids: '',
          group_openids: '',
          remark: '',
          items: [],
        }
  );
  const [saving, setSaving] = useState(false);
  const specificAllowed = form.scope === 'c2c' || form.scope === 'group';

  const patch = (key, value) => setForm((f) => ({ ...f, [key]: value }));
  const patchItem = (i, key, value) =>
    setForm((f) => ({ ...f, items: f.items.map((it, idx) => (idx === i ? { ...it, [key]: value } : it)) }));
  const addItem = () =>
    setForm((f) => ({ ...f, items: [...f.items, { type: 'command', name: '', desc: '', only_admin: false, link: '' }] }));
  const removeItem = (i) => setForm((f) => ({ ...f, items: f.items.filter((_, idx) => idx !== i) }));

  const submit = async () => {
    setSaving(true);
    try {
      if (isEdit) {
        await api(`/api/webui/bots/${encodeURIComponent(botId)}/panels/${encodeURIComponent(editor.panelId)}`, {
          method: 'PUT',
          body: { panel: { items: form.items, remark: form.remark || undefined } },
        });
        toast('面板已更新', 'success');
      } else {
        const body = {
          scope: form.scope,
          target_type: specificAllowed ? form.target_type : 'all',
          panel: { items: form.items, remark: form.remark || undefined },
        };
        if (form.target_type === 'specific' && form.scope === 'c2c') {
          body.user_openids = form.user_openids.split(',').map((s) => s.trim()).filter(Boolean);
        }
        if (form.target_type === 'specific' && form.scope === 'group') {
          body.group_openids = form.group_openids.split(',').map((s) => s.trim()).filter(Boolean);
        }
        await api(`/api/webui/bots/${encodeURIComponent(botId)}/panels`, { method: 'POST', body });
        toast('面板已创建', 'success');
      }
      onSaved();
    } catch (e) {
      toast(e.message, 'error');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="mt-3 rounded-lg border border-qq-blue/40 bg-qq-bg/40 p-3">
      <div className="mb-2.5 flex items-center justify-between">
        <div className="text-[13px] font-medium">{isEdit ? `编辑面板：${editor.panelId}` : '新建指令面板'}</div>
        <button className="text-qq-sub hover:text-qq-text" onClick={onClose}>
          <IconX size={14} />
        </button>
      </div>

      <div className="grid grid-cols-2 gap-2.5 md:grid-cols-4">
        <Field label="生效场景">
          <select className={inputCls} value={form.scope} disabled={isEdit} onChange={(e) => patch('scope', e.target.value)}>
            {PANEL_SCOPES.map((s) => (
              <option key={s.value} value={s.value}>{s.label}</option>
            ))}
          </select>
        </Field>
        <Field label="作用范围" hint="channel/dm 仅支持全局">
          <select
            className={inputCls}
            value={specificAllowed ? form.target_type : 'all'}
            disabled={!specificAllowed}
            onChange={(e) => patch('target_type', e.target.value)}
          >
            <option value="all">全局（all）</option>
            {specificAllowed && <option value="specific">指定用户/群（specific）</option>}
          </select>
        </Field>
        {form.target_type === 'specific' && form.scope === 'c2c' && (
          <Field label="用户 openid" hint="逗号分隔，最多 20 个" wide>
            <input className={inputCls} value={form.user_openids} onChange={(e) => patch('user_openids', e.target.value)} placeholder="openid1, openid2" />
          </Field>
        )}
        {form.target_type === 'specific' && form.scope === 'group' && (
          <Field label="群 openid" hint="逗号分隔，最多 20 个" wide>
            <input className={inputCls} value={form.group_openids} onChange={(e) => patch('group_openids', e.target.value)} placeholder="openid1, openid2" />
          </Field>
        )}
        <Field label="备注" hint="仅开发者可见，最多 255 字符" wide>
          <input className={inputCls} value={form.remark} onChange={(e) => patch('remark', e.target.value)} placeholder="标记面板用途" />
        </Field>
      </div>

      <div className="mt-2.5">
        <div className="mb-1.5 flex items-center justify-between">
          <span className="text-xs font-medium text-qq-sub">面板元素（{form.items.length}/20）</span>
          <button
            className="flex items-center gap-1 rounded-md border border-qq-border px-2 py-1 text-[11px] hover:bg-qq-hover disabled:opacity-50"
            onClick={addItem}
            disabled={form.items.length >= 20}
          >
            <IconPlus size={11} /> 添加元素
          </button>
        </div>
        {form.items.length === 0 ? (
          <div className="rounded-lg border border-dashed border-qq-border p-3 text-center text-[11px] text-qq-sub">暂无元素</div>
        ) : (
          <div className="space-y-2">
            {form.items.map((it, i) => (
              <div key={i} className="grid grid-cols-2 gap-2.5 rounded-lg border border-qq-border bg-white p-2.5 md:grid-cols-6">
                <Field label="名称" hint="command 时点击填入输入框">
                  <input className={inputCls} value={it.name} onChange={(e) => patchItem(i, 'name', e.target.value)} placeholder="如 查询天气" />
                </Field>
                <Field label="类型">
                  <select className={inputCls} value={it.type} onChange={(e) => patchItem(i, 'type', e.target.value)}>
                    <option value="command">指令（command）</option>
                    <option value="link">链接（link）</option>
                  </select>
                </Field>
                <Field label="描述" hint="最多 30 字符">
                  <input className={inputCls} value={it.desc || ''} onChange={(e) => patchItem(i, 'desc', e.target.value)} />
                </Field>
                {it.type === 'link' ? (
                  <Field label="跳转链接">
                    <input className={inputCls} value={it.link || ''} onChange={(e) => patchItem(i, 'link', e.target.value)} placeholder="https://" />
                  </Field>
                ) : (
                  <div className="flex items-end gap-1.5 pb-0.5">
                    <label className="flex items-center gap-1.5 text-[11px] text-qq-sub">
                      <Toggle checked={!!it.only_admin} onChange={(v) => patchItem(i, 'only_admin', v)} /> 仅管理员
                    </label>
                  </div>
                )}
                <div className="flex items-end justify-end pb-0.5">
                  <button className="rounded-md border border-red-200 px-2 py-1 text-[11px] text-red-600 hover:bg-red-50" onClick={() => removeItem(i)}>
                    删除
                  </button>
                </div>
              </div>
            ))}
          </div>
        )}
      </div>

      <div className="mt-3 flex justify-end gap-2">
        <button className="rounded-md border border-qq-border px-3 py-1.5 text-xs hover:bg-qq-hover" onClick={onClose}>
          取消
        </button>
        <button
          className="rounded-md bg-qq-blue px-3 py-1.5 text-xs text-white hover:bg-qq-blue-deep disabled:opacity-50"
          onClick={submit}
          disabled={saving || busy}
        >
          {saving ? '保存中…' : isEdit ? '保存修改' : '创建面板'}
        </button>
      </div>
    </div>
  );
}
