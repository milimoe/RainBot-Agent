import { useEffect, useRef, useState } from 'react';
import { api, AuthError } from '../lib/api.js';
import { Badge, Toggle, toast } from '../components/ui.jsx';
import { IconPlus, IconX } from '../components/Icons.jsx';

const inputCls = 'w-full rounded-md border border-qq-border bg-white px-2.5 py-1.5 text-[13px] outline-none focus:border-qq-blue';
const buttonCls = 'rounded-md border border-qq-border bg-white px-3 py-1.5 text-xs hover:bg-qq-hover disabled:opacity-40';
const textFields = [
  ['botName', '机器人名称（bot_name）'], ['intro', '开场介绍'],
  ['personaSectionTitle', '人设章节标题'], ['rulesSectionTitle', '行为规则章节标题'],
  ['identitySectionTitle', '身份说明章节标题'],
];

function toForm(persona, defaults) {
  const settings = { ...defaults, ...persona?.settings };
  return {
    name: persona?.name || '', content: persona?.content || '',
    settings: { ...settings, rules: [...(settings.rules || [])], identityNotes: [...(settings.identityNotes || [])] },
  };
}

function Field({ label, children }) {
  return <label className="block"><div className="mb-1 text-xs text-qq-sub">{label}</div>{children}</label>;
}

function ListEditor({ title, values, onChange, disabled }) {
  return (
    <div className="space-y-2">
      <div className="flex items-center justify-between">
        <span className="text-xs font-medium">{title}</span>
        <button className={buttonCls} disabled={disabled} onClick={() => onChange([...values, ''])}><IconPlus size={12} /> 添加条目</button>
      </div>
      {values.length === 0 && <p className="text-xs text-qq-sub">暂无条目</p>}
      {values.map((value, i) => (
        <div key={i} className="flex items-start gap-2">
          <textarea aria-label={`${title} ${i + 1}`} className={inputCls} rows={2} value={value} disabled={disabled}
            onChange={(e) => onChange(values.map((v, index) => index === i ? e.target.value : v))} />
          <button className="mt-2 text-qq-sub hover:text-qq-red" aria-label={`删除${title} ${i + 1}`} disabled={disabled}
            onClick={() => onChange(values.filter((_, index) => index !== i))}><IconX size={14} /></button>
        </div>
      ))}
    </div>
  );
}

export default function PersonasPage({ onAuthFail }) {
  const [data, setData] = useState({ personas: [], defaults: {} });
  const [form, setForm] = useState(null);
  const [original, setOriginal] = useState('');
  const [isNew, setIsNew] = useState(false);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [styles, setStyles] = useState([]);
  const [importName, setImportName] = useState('');
  const [styleManagerOpen, setStyleManagerOpen] = useState(false);
  const styleDialog = useRef(null);
  const [styleError, setStyleError] = useState('');
  const dirty = form !== null && JSON.stringify(form) !== original;

  useEffect(() => {
    if (styleManagerOpen) styleDialog.current?.showModal();
    else styleDialog.current?.close();
  }, [styleManagerOpen]);

  const report = (e) => {
    setError(e.message);
    if (e instanceof AuthError) onAuthFail?.();
    else toast(e.message, 'error');
  };
  const install = (persona, defaults, fresh = false) => {
    const next = toForm(persona, defaults);
    setForm(next); setOriginal(JSON.stringify(next)); setIsNew(fresh); setError('');
  };
  useEffect(() => {
    let active = true;
    api('/api/webui/personas').then((result) => {
      if (!active) return;
      setData(result);
      install(result.personas.find((p) => p.isDefault) || result.personas[0], result.defaults);
    }).catch((e) => { if (active) report(e); }).finally(() => { if (active) setLoading(false); });
    api('/api/webui/language-styles').then((result) => {
      if (active) setStyles(result.styles || []);
    }).catch((e) => {
      if (active) { setStyleError(e.message); if (e instanceof AuthError) onAuthFail?.(); }
    });
    return () => { active = false; };
  }, []);

  useEffect(() => {
    if (!dirty) return;
    const unload = (event) => { event.preventDefault(); event.returnValue = ''; };
    const navigate = (event) => { if (!window.confirm('人设有未保存的修改，确定离开？')) event.preventDefault(); };
    window.addEventListener('beforeunload', unload);
    window.addEventListener('webui:navigate', navigate);
    return () => { window.removeEventListener('beforeunload', unload); window.removeEventListener('webui:navigate', navigate); };
  }, [dirty]);

  const switchTo = (persona, fresh = false) => {
    if (busy || (dirty && !window.confirm('人设有未保存的修改，确定放弃？'))) return;
    install(persona, data.defaults, fresh);
  };
  const change = (key, value) => setForm((f) => ({ ...f, [key]: value }));
  const changeSetting = (key, value) => setForm((f) => ({ ...f, settings: { ...f.settings, [key]: value } }));
  const current = data.personas.find((p) => p.name === form?.name);
  const validName = /^[\p{L}\p{Nd}_-]{1,64}$/u.test(form?.name || '') && form?.name !== 'default';
  const validation = !form ? '' : isNew && !validName ? '请填写有效的人设标识名，default 为保留名称。'
    : form.settings.languageStyleEnabled && !styles.some((style) => style.name === form.settings.languageStyleName) ? '请先上传并选择语言风格 Skill。'
    : !form.settings.languageStyleEnabled && !form.content.trim() ? '请填写或导入 Markdown 人设正文。'
    : textFields.some(([key]) => !form.settings[key]?.trim()) ? '机器人名称、开场介绍和章节标题不能为空。'
    : [...form.settings.rules, ...form.settings.identityNotes].some((v) => !v.trim()) ? '请填写或删除空的提示词条目。' : '';
  const valid = !!form && !validation;

  const save = async () => {
    if (!valid || busy) return;
    setBusy(true); setError('');
    try {
      await api(isNew ? '/api/webui/personas' : `/api/webui/personas/${encodeURIComponent(form.name)}`, {
        method: isNew ? 'POST' : 'PUT', body: { name: form.name, content: form.content, settings: form.settings },
      });
      const result = await api('/api/webui/personas');
      setData(result);
      install(result.personas.find((p) => p.name === form.name), result.defaults);
      toast('人设已保存，关联实例即时生效', 'success');
    } catch (e) { report(e); } finally { setBusy(false); }
  };
  const remove = async () => {
    if (!current || current.isDefault || busy || !window.confirm(`删除人设「${form.name}」？`)) return;
    setBusy(true); setError('');
    try {
      await api(`/api/webui/personas/${encodeURIComponent(form.name)}`, { method: 'DELETE' });
      const result = await api('/api/webui/personas'); setData(result);
      install(result.personas.find((p) => p.isDefault) || result.personas[0], result.defaults);
      toast('人设已删除', 'success');
    } catch (e) { report(e); } finally { setBusy(false); }
  };
  const upload = async (event) => {
    const file = event.target.files?.[0]; event.target.value = '';
    if (!file) return;
    try { change('content', await file.text()); } catch { report(new Error('无法读取文档，请重新选择 Markdown 文件')); }
  };
  const importStyleContent = async (skillMarkdown) => {
    if (busy || !form) return;
    setBusy(true); setError('');
    try {
      const result = await api('/api/webui/language-styles', {
        method: 'POST', body: { name: importName.trim(), skillMarkdown },
      });
      setStyles(result.styles || []); setStyleError(''); setImportName('');
      changeSetting('languageStyleName', result.style.name);
      toast('Skill 已上传，开启开关并保存人设后生效', 'success');
    } catch (e) { report(e); } finally { setBusy(false); }
  };
  const importStyle = async (event) => {
    const file = event.target.files?.[0]; event.target.value = '';
    if (!file || busy) return;
    if (!/^[\p{L}\p{Nd}_-]{1,64}$/u.test(importName.trim())) { report(new Error('请先填写有效的风格名称')); return; }
    if (file.size > 120000) { report(new Error('Skill 文件过大，请使用不超过 30000 字符的 Markdown 文档')); return; }
    try { await importStyleContent(await file.text()); } catch { report(new Error('无法读取 Skill 文档')); }
  };
  const openStyleManager = async () => {
    setStyleManagerOpen(true); setStyleError('');
    try {
      const result = await api('/api/webui/language-styles');
      setStyles(result.styles || []);
    } catch (e) { setStyleError(e.message); if (e instanceof AuthError) onAuthFail?.(); }
  };
  const deleteStyle = async (name) => {
    if (busy || !window.confirm(`删除语言风格「${name}」？文件将被删除，需要重新上传才能恢复。`)) return;
    setBusy(true); setError(''); setStyleError('');
    try {
      const result = await api(`/api/webui/language-styles/${encodeURIComponent(name)}`, { method: 'DELETE' });
      setStyles(result.styles || []);
      if (form?.settings.languageStyleName === name) changeSetting('languageStyleName', '');
      toast('语言风格已删除', 'success');
    } catch (e) { setStyleError(e.message); if (e instanceof AuthError) onAuthFail?.(); else toast(e.message, 'error'); }
    finally { setBusy(false); }
  };

  return (
    <div className="flex h-full min-w-0 flex-1 flex-col md:flex-row">
      <aside className="qq-scroll max-h-48 shrink-0 overflow-y-auto border-b border-qq-border bg-qq-panel py-4 md:max-h-none md:w-[240px] md:border-b-0 md:border-r">
        <div className="mb-3 flex items-center justify-between px-4">
          <span className="text-xs font-medium text-qq-sub">人设库</span>
          <button className={buttonCls} disabled={busy || loading} onClick={() => switchTo(null, true)}><IconPlus size={12} /> 新增</button>
        </div>
        {loading && <p className="px-4 text-xs text-qq-sub">加载中…</p>}
        {data.personas.map((p) => (
          <button key={p.name} disabled={busy} onClick={() => switchTo(p)}
            className={`block w-full border-l-2 px-4 py-3 text-left ${!isNew && form?.name === p.name ? 'border-qq-blue bg-white' : 'border-transparent hover:bg-qq-hover'}`}>
            <div className="flex items-center justify-between gap-2 text-[13px] font-medium"><span className="truncate">{p.name}</span>{p.isDefault && <Badge tone="blue">默认模板</Badge>}</div>
            <div className="mt-1 truncate text-xs text-qq-sub">{p.botName || p.settings?.botName || '未设置机器人名称'}</div>
          </button>
        ))}
      </aside>
      <main className="qq-scroll min-w-0 flex-1 overflow-y-auto px-4 py-5 md:px-8">
        <div className="mx-auto max-w-4xl space-y-4">
          <div className="flex items-center justify-between gap-3">
            <div><h1 className="text-lg font-semibold">{isNew ? '新增人设' : '人设'}</h1><p className="mt-1 text-xs text-qq-sub">用标识名管理人设，在「机器人」页为实例选择；机器人名称独立设置。</p></div>
            {form && <div className="flex shrink-0 items-center gap-2">{dirty && <Badge tone="orange">未保存</Badge>}<button className="rounded-md bg-qq-blue px-3 py-1.5 text-xs text-white hover:bg-qq-blue-deep disabled:opacity-40" disabled={busy || !valid || (!dirty && !isNew)} onClick={save}>{busy ? '处理中…' : '保存人设'}</button></div>}
          </div>
          {error && <div role="alert" className="rounded-lg border border-red-200 bg-red-50 p-3 text-xs text-red-600">{error}</div>}
          {(dirty || isNew) && validation && <div role="status" className="rounded-lg bg-qq-bg p-3 text-xs text-qq-sub">{validation}</div>}
          {form && <>
            <section className="space-y-3 rounded-xl border border-qq-border bg-white p-4">
              <div className="flex items-center justify-between gap-3">
                <h2 className="text-[14px] font-medium">应用语言风格 Skill</h2>
                <Toggle checked={!!form.settings.languageStyleEnabled} disabled={busy} onChange={(value) => changeSetting('languageStyleEnabled', value)} />
              </div>
              <p className="text-xs text-qq-sub">开启后使用固定群聊伙伴人设与所选 Skill，无需自己编写人设。关闭后使用保存的人设正文。开关与选择保存后，关联实例下一轮对话生效。</p>
              {styleError && <p role="alert" className="text-xs text-red-600">语言风格列表加载失败：{styleError}</p>}
              <div className="flex flex-wrap items-center gap-2">
                <select aria-label="语言风格 Skill" className={`${inputCls} sm:w-auto`} value={form.settings.languageStyleName || ''} disabled={busy || !styles.length} onChange={(e) => changeSetting('languageStyleName', e.target.value)}>
                  <option value="">选择已上传的风格</option>
                  {form.settings.languageStyleName && !styles.some((style) => style.name === form.settings.languageStyleName) && <option value={form.settings.languageStyleName}>{form.settings.languageStyleName}（不可用）</option>}
                  {styles.map((style) => <option key={style.name} value={style.name}>{style.name}</option>)}
                </select>
                <button className={buttonCls} disabled={busy} onClick={openStyleManager}>语言风格管理</button>
              </div>
              {!styles.length && <p className="text-xs text-qq-sub">暂无语言风格，请在「语言风格管理」中上传 Skill 文件。</p>}
            </section>
            <section className="space-y-3 rounded-xl border border-qq-border bg-white p-4">
              <Field label="人设标识名（name）"><input className={inputCls} value={form.name} disabled={!isNew || busy} maxLength={64} placeholder="例如 rain、温柔小雨" onChange={(e) => change('name', e.target.value)} /></Field>
              <p className="text-[11px] text-qq-sub">{isNew ? '必填，最多 64 个字，支持字母、数字、中文、下划线和连字符；创建后不可修改。default 为保留名称。' : current?.isDefault ? 'RainBot 默认人设模板，未选择人设的实例使用此模板。' : '标识名用于实例关联，机器人名称用于对话身份。'}</p>
              <div className="break-all text-xs text-qq-sub">文件：{isNew ? `Persona/persona_${form.name || '{name}'}.md` : current?.path}</div>
              <div className="flex items-center justify-between"><span className="text-xs font-medium">Markdown 人设正文{form.settings.languageStyleEnabled ? '（风格模式使用固定模板）' : '（必填）'}</span><label className={`${buttonCls} cursor-pointer`}>导入 .md<input type="file" accept=".md,.markdown,text/markdown,text/plain" className="hidden" disabled={busy || form.settings.languageStyleEnabled} onChange={upload} /></label></div>
              <textarea className={`${inputCls} qq-scroll resize-y bg-qq-bg/40 font-mono leading-5`} rows={16} value={form.content} disabled={busy || form.settings.languageStyleEnabled} placeholder="# 人设\n输入完整的人设 Markdown 文档…" onChange={(e) => change('content', e.target.value)} />
            </section>
            <section className="space-y-4 rounded-xl border border-qq-border bg-white p-4">
              <div><h2 className="text-[14px] font-medium">提示词设置</h2><p className="mt-1 text-xs text-qq-sub">与 Markdown 人设一同保存，支持独立调整介绍、章节标题、行为规则和身份说明；开场介绍中的 {'{name}'} 会替换为机器人名称。</p></div>
              <div className="grid gap-3 sm:grid-cols-2">{textFields.map(([key, label]) => <Field key={key} label={label}>{key === 'intro' ? <textarea className={inputCls} rows={3} disabled={busy} value={form.settings[key] || ''} onChange={(e) => changeSetting(key, e.target.value)} /> : <input className={inputCls} disabled={busy} value={form.settings[key] || ''} onChange={(e) => changeSetting(key, e.target.value)} />}</Field>)}</div>
              <p className="whitespace-pre-wrap text-xs text-qq-sub">开场预览：{(form.settings.intro || '').replaceAll('{name}', form.settings.botName || '')}</p>
              <ListEditor title="行为规则" values={form.settings.rules} disabled={busy} onChange={(v) => changeSetting('rules', v)} />
              <ListEditor title="身份说明" values={form.settings.identityNotes} disabled={busy} onChange={(v) => changeSetting('identityNotes', v)} />
            </section>
            {!isNew && !current?.isDefault && <div className="flex justify-end"><button className="rounded-md border border-red-200 px-3 py-1.5 text-xs text-red-600 hover:bg-red-50 disabled:opacity-40" disabled={busy} onClick={remove}>删除人设</button></div>}
          </>}
        </div>
      </main>
      <dialog ref={styleDialog} aria-labelledby="style-manager-title" className="m-auto w-[calc(100%-2rem)] max-w-xl rounded-xl border border-qq-border bg-white p-0 text-qq-text shadow-xl backdrop:bg-black/40"
        onCancel={(event) => { event.preventDefault(); if (!busy) setStyleManagerOpen(false); }}
        onClick={(event) => { if (event.target === event.currentTarget && !busy) setStyleManagerOpen(false); }}>
        <div className="flex max-h-[85vh] flex-col">
          <div className="flex items-center justify-between border-b border-qq-border px-5 py-4">
            <h2 id="style-manager-title" className="text-base font-semibold">语言风格管理</h2>
            <button aria-label="关闭语言风格管理" className="text-qq-sub hover:text-qq-text disabled:opacity-40" disabled={busy} onClick={() => setStyleManagerOpen(false)}><IconX size={18} /></button>
          </div>
          <div className="qq-scroll space-y-4 overflow-y-auto p-5">
            {styleError && <div role="alert" className="rounded-lg bg-red-50 p-3 text-xs text-red-600">{styleError}</div>}
            {error && <div role="alert" className="rounded-lg bg-red-50 p-3 text-xs text-red-600">{error}</div>}
            <div className="space-y-3 rounded-lg border border-qq-border bg-qq-bg/40 p-3">
              <div className="flex flex-wrap items-end gap-2">
                <Field label="上传风格名称"><input className={inputCls} value={importName} maxLength={64} disabled={busy} placeholder="例如 my_style" onChange={(e) => setImportName(e.target.value)} /></Field>
                <label className={`${buttonCls} ${busy || !importName.trim() ? 'opacity-40' : 'cursor-pointer'}`}>上传 SKILL.md<input type="file" accept=".md,.markdown,text/markdown,text/plain" className="hidden" disabled={busy || !importName.trim()} onChange={importStyle} /></label>
              </div>
              <p className="text-xs text-qq-sub">保存为 /skills/styles/style_{'{name}'}.md，不自动开启风格；同名文件不覆盖。</p>
            </div>
            <div className="space-y-2">
              <h3 className="text-sm font-medium">已上传的风格（{styles.length}）</h3>
              {!styles.length && <p className="py-6 text-center text-xs text-qq-sub">暂无语言风格，请先上传 Skill 文件。</p>}
              {styles.map((style) => <div key={style.name} className="flex items-center justify-between gap-3 rounded-lg border border-qq-border p-3">
                <div className="min-w-0"><div className="truncate text-sm font-medium">{style.name}</div><div className="mt-1 break-all text-xs text-qq-sub">{style.fileName}</div></div>
                <button className="shrink-0 rounded-md border border-red-200 px-3 py-1.5 text-xs text-red-600 hover:bg-red-50 disabled:opacity-40" disabled={busy} onClick={() => deleteStyle(style.name)}>删除</button>
              </div>)}
              <p className="text-xs text-qq-sub">正在被已保存人设启用的风格不能删除，请先关闭或更换后保存。</p>
            </div>
          </div>
          <div className="flex justify-end border-t border-qq-border px-5 py-3"><button className={buttonCls} disabled={busy} onClick={() => setStyleManagerOpen(false)}>关闭</button></div>
        </div>
      </dialog>
    </div>
  );
}
