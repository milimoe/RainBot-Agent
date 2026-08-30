import { useCallback, useEffect, useState } from 'react';
import { api } from '../lib/api.js';
import { Badge, Toggle, toast } from '../components/ui.jsx';
import { IconCopy, IconPlus, IconRobot } from '../components/Icons.jsx';

const PLATFORMS = [
  { value: 'QqOfficial', label: 'QQ 官方机器人', hint: '开放平台 AppID + Secret；群/用户使用 openid' },
  { value: 'OneBot11', label: 'OneBot11', hint: 'go-cqhttp / NapCat / Lagrange；群/用户使用 QQ 号' },
];

function emptyForm() {
  return {
    id: '',
    name: '',
    platform: 'QqOfficial',
    enabled: true,
    personaPath: '',
    qq: { appId: '', secret: '', useSandbox: false, selfOpenId: '' },
    oneBot: {
      selfQq: '',
      http: { enabled: false, apiUrl: '', token: '' },
      wsForward: { enabled: false, url: '', token: '' },
      wsReverse: { enabled: false, token: '' },
    },
  };
}

/** 后端实例 → 表单（缺字段补默认，防后端新增字段时前端报错） */
function toForm(b) {
  const base = emptyForm();
  if (!b) return base;
  return {
    id: b.id || '',
    name: b.name || '',
    platform: b.platform || 'QqOfficial',
    enabled: b.enabled !== false,
    personaPath: b.personaPath || '',
    qq: { ...base.qq, ...(b.qq || {}) },
    oneBot: {
      selfQq: b.oneBot?.selfQq || '',
      http: { ...base.oneBot.http, ...(b.oneBot?.http || {}) },
      wsForward: { ...base.oneBot.wsForward, ...(b.oneBot?.wsForward || {}) },
      wsReverse: { ...base.oneBot.wsReverse, ...(b.oneBot?.wsReverse || {}) },
    },
  };
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

const inputCls =
  'w-full rounded-md border border-qq-border bg-white px-2.5 py-1.5 text-[13px] outline-none focus:border-qq-blue';

function CopyRow({ label, value }) {
  if (!value) return null;
  return (
    <div className="flex items-center gap-2 rounded-md border border-qq-border bg-qq-panel px-2.5 py-1.5">
      <span className="shrink-0 text-[11px] text-qq-sub">{label}</span>
      <code className="min-w-0 flex-1 truncate text-[11px]">{value}</code>
      <button
        className="shrink-0 text-qq-sub hover:text-qq-text"
        title="复制"
        onClick={() => {
          navigator.clipboard?.writeText(value);
          toast('已复制', 'success');
        }}
      >
        <IconCopy />
      </button>
    </div>
  );
}

function Channel({ title, checked, onToggle, children }) {
  return (
    <div className="rounded-lg border border-qq-border p-3">
      <div className="mb-2 flex items-center justify-between">
        <span className="text-[13px] font-medium">{title}</span>
        <Toggle checked={checked} onChange={onToggle} />
      </div>
      {checked ? <div className="grid grid-cols-2 gap-3">{children}</div> : null}
    </div>
  );
}

/**
 * 机器人管理页：注册/编辑机器人实例与监听方式，查看连接状态与 OneBot 接入地址。
 * 实例改动即时生效（网关按需启停），历史与画像按实例隔离（群键 = {实例Id}:{群号}）。
 */
export default function BotsPage() {
  const [data, setData] = useState({ bots: [], status: [] });
  const [loading, setLoading] = useState(true);
  const [form, setForm] = useState(emptyForm());
  const [isNew, setIsNew] = useState(false);
  const [saving, setSaving] = useState(false);

  const load = useCallback(async () => {
    try {
      setData(await api('/api/webui/bots'));
    } catch (e) {
      toast(e.message, 'error');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    load();
    const t = setInterval(load, 10000); // 连接状态定期刷新
    return () => clearInterval(t);
  }, [load]);

  const statusOf = (id) => data.status?.find((s) => s.id === id);
  const statsOf = (id) => data.sendStats?.find((s) => s.botId === id);
  const saved = (key, value) => setForm((f) => ({ ...f, [key]: value }));
  const patch = (path, value) =>
    setForm((f) => {
      const next = { ...f };
      const seg = path.split('.');
      let cur = next;
      for (let i = 0; i < seg.length - 1; i++) cur = cur[seg[i]] = { ...cur[seg[i]] };
      cur[seg[seg.length - 1]] = value;
      return next;
    });

  const select = (b) => {
    setForm(toForm(b));
    setIsNew(false);
  };

  const create = () => {
    setForm(emptyForm());
    setIsNew(true);
  };

  const save = async () => {
    setSaving(true);
    try {
      await api('/api/webui/bots', { method: 'POST', body: form });
      toast(isNew ? '实例已创建' : '实例已保存', 'success');
      setIsNew(false);
      await load();
    } catch (e) {
      toast(e.message, 'error');
    } finally {
      setSaving(false);
    }
  };

  const remove = async (b) => {
    if (!window.confirm(`删除实例「${b.name || b.id}」？其历史与画像仍保留在库中。`)) return;
    try {
      await api(`/api/webui/bots/${encodeURIComponent(b.id)}`, { method: 'DELETE' });
      toast('已删除', 'success');
      if (form.id === b.id) setForm(emptyForm());
      await load();
    } catch (e) {
      toast(e.message, 'error');
    }
  };

  const toggleEnabled = async (b, enabled) => {
    try {
      await api(`/api/webui/bots/${encodeURIComponent(b.id)}/enabled`, { method: 'POST', body: { enabled } });
      await load();
    } catch (e) {
      toast(e.message, 'error');
    }
  };

  const isOneBot = form.platform === 'OneBot11';
  const endpoints = data.bots?.find((b) => b.id === form.id)?.endpoints;

  return (
    <div className="flex h-full min-w-0 flex-1">
      {/* 实例列表 */}
      <div className="qq-scroll w-[280px] shrink-0 overflow-y-auto border-r border-qq-border bg-qq-panel py-4">
        <div className="mb-3 flex items-center justify-between px-4">
          <div className="text-xs font-medium text-qq-sub">机器人实例</div>
          <button
            className="flex items-center gap-1 rounded-md border border-qq-border bg-white px-2 py-1 text-[11px] hover:bg-qq-hover"
            onClick={create}
          >
            <IconPlus /> 新增
          </button>
        </div>

        {loading ? (
          <div className="px-4 text-[13px] text-qq-sub">加载中…</div>
        ) : data.bots.length === 0 ? (
          <div className="px-4 text-[13px] text-qq-sub">暂无实例，点「新增」创建。</div>
        ) : (
          data.bots.map((b) => {
            const st = statusOf(b.id);
            const active = form.id === b.id && !isNew;
            return (
              <button
                key={b.id}
                onClick={() => select(b)}
                className={`block w-full border-l-2 px-4 py-2.5 text-left ${
                  active ? 'border-qq-blue bg-white' : 'border-transparent hover:bg-qq-hover'
                }`}
              >
                <div className="flex items-center gap-2">
                  <span className="shrink-0 text-qq-sub">
                    <IconRobot size={16} />
                  </span>
                  <span className="min-w-0 flex-1 truncate text-[13px] font-medium">{b.name || b.id}</span>
                  {b.enabled ? (
                    <Badge tone={st?.connected ? 'green' : 'gray'}>{st?.connected ? '已连接' : '未连接'}</Badge>
                  ) : (
                    <Badge tone="gray">已禁用</Badge>
                  )}
                </div>
                <div className="mt-1 truncate text-[11px] text-qq-sub">
                  {b.id} · {b.platform === 'OneBot11' ? 'OneBot11' : 'QQ 官方'}
                </div>
                {statsOf(b.id) ? (
                  <div className="mt-0.5 text-[11px] text-qq-sub">
                    发送 {statsOf(b.id).sent}
                    {statsOf(b.id).failed > 0 ? (
                      <span className="ml-1.5 text-red-500">失败 {statsOf(b.id).failed}</span>
                    ) : null}
                  </div>
                ) : null}
              </button>
            );
          })
        )}
      </div>

      {/* 编辑区 */}
      <div className="qq-scroll min-w-0 flex-1 overflow-y-auto px-4 py-5 md:px-8">
        <div className="mb-4 flex items-center justify-between">
          <div>
            <div className="text-[15px] font-medium">
              {isNew ? '新增机器人实例' : form.id ? `编辑：${form.name || form.id}` : '机器人管理'}
            </div>
            <div className="mt-0.5 text-[11px] leading-snug text-qq-sub">
              每个实例有独立的数据命名空间（群键 = 实例Id:群号），画像 / 历史 / 统计互不影响。
            </div>
          </div>
          {form.id ? (
            <button
              className="rounded-md bg-qq-blue px-3 py-1.5 text-[13px] text-white disabled:opacity-50"
              onClick={save}
              disabled={saving}
            >
              {saving ? '保存中…' : '保存'}
            </button>
          ) : null}
        </div>

        {!form.id && !isNew ? (
          <div className="rounded-xl border border-qq-border bg-white p-6 text-[13px] text-qq-sub">
            选择左侧实例进行编辑，或点「新增」注册一个机器人。
          </div>
        ) : (
          <div className="space-y-4">
            <div className="rounded-xl border border-qq-border bg-white p-4">
              <div className="mb-3 text-[13px] font-medium">基础信息</div>
              <div className="grid grid-cols-2 gap-3">
                <Field label="实例 Id" hint="字母/数字/-/_，作为存储前缀，创建后不建议改">
                  <input
                    className={inputCls}
                    value={form.id}
                    disabled={!isNew}
                    onChange={(e) => saved('id', e.target.value)}
                    placeholder="如 ob-napcat"
                  />
                </Field>
                <Field label="显示名">
                  <input className={inputCls} value={form.name} onChange={(e) => saved('name', e.target.value)} />
                </Field>
                <Field label="平台" hint={PLATFORMS.find((p) => p.value === form.platform)?.hint} wide>
                  <select className={inputCls} value={form.platform} onChange={(e) => saved('platform', e.target.value)}>
                    {PLATFORMS.map((p) => (
                      <option key={p.value} value={p.value}>
                        {p.label}
                      </option>
                    ))}
                  </select>
                </Field>
                <Field label="人设文件（留空用全局）">
                  <input
                    className={inputCls}
                    value={form.personaPath}
                    onChange={(e) => saved('personaPath', e.target.value)}
                    placeholder="Persona/persona.md"
                  />
                </Field>
                <Field label="启用">
                  <div className="py-1">
                    <Toggle checked={form.enabled} onChange={(v) => saved('enabled', v)} />
                  </div>
                </Field>
              </div>
            </div>

            {isOneBot ? (
              <>
                <div className="rounded-xl border border-qq-border bg-white p-4">
                  <div className="mb-3 text-[13px] font-medium">OneBot11 身份</div>
                  <div className="grid grid-cols-2 gap-3">
                    <Field label="机器人 QQ 号" hint="留空则收到第一条消息时从 self_id 自动学习">
                      <input
                        className={inputCls}
                        value={form.oneBot.selfQq}
                        onChange={(e) => patch('oneBot.selfQq', e.target.value)}
                        placeholder="123456789"
                      />
                    </Field>
                  </div>
                </div>

                <div className="rounded-xl border border-qq-border bg-white p-4">
                  <div className="mb-3 text-[13px] font-medium">监听与发送通道</div>
                  <div className="space-y-3">
                    <Channel
                      title="HTTP（上报 + API 调用）"
                      checked={form.oneBot.http.enabled}
                      onToggle={(v) => patch('oneBot.http.enabled', v)}
                    >
                      <Field
                        label="API 基址"
                        hint="本服务调用它发消息，如 http://127.0.0.1:3000"
                      >
                        <input
                          className={inputCls}
                          value={form.oneBot.http.apiUrl}
                          onChange={(e) => patch('oneBot.http.apiUrl', e.target.value)}
                        />
                      </Field>
                      <Field label="访问令牌（选填）">
                        <input
                          className={inputCls}
                          value={form.oneBot.http.token}
                          onChange={(e) => patch('oneBot.http.token', e.target.value)}
                        />
                      </Field>
                    </Channel>

                    <Channel
                      title="WS 正向（本服务主动连接）"
                      checked={form.oneBot.wsForward.enabled}
                      onToggle={(v) => patch('oneBot.wsForward.enabled', v)}
                    >
                      <Field label="WS 地址" hint="如 ws://127.0.0.1:3001">
                        <input
                          className={inputCls}
                          value={form.oneBot.wsForward.url}
                          onChange={(e) => patch('oneBot.wsForward.url', e.target.value)}
                        />
                      </Field>
                      <Field label="访问令牌（选填）">
                        <input
                          className={inputCls}
                          value={form.oneBot.wsForward.token}
                          onChange={(e) => patch('oneBot.wsForward.token', e.target.value)}
                        />
                      </Field>
                    </Channel>

                    <Channel
                      title="WS 反向（OneBot 主动连入本服务）"
                      checked={form.oneBot.wsReverse.enabled}
                      onToggle={(v) => patch('oneBot.wsReverse.enabled', v)}
                    >
                      <Field label="访问令牌（选填）" hint="OneBot 侧需配置相同的 token">
                        <input
                          className={inputCls}
                          value={form.oneBot.wsReverse.token}
                          onChange={(e) => patch('oneBot.wsReverse.token', e.target.value)}
                        />
                      </Field>
                    </Channel>
                  </div>
                </div>

                {endpoints ? (
                  <div className="rounded-xl border border-qq-border bg-white p-4">
                    <div className="mb-3 text-[13px] font-medium">接入地址（复制到 OneBot 实现配置）</div>
                    <div className="space-y-2">
                      <CopyRow label="HTTP 上报" value={endpoints.httpReport} />
                      <CopyRow label="反向 WS" value={endpoints.reverseWs} />
                    </div>
                    <div className="mt-2 text-[11px] leading-snug text-qq-sub">
                      未设置公网域名时显示的是相对路径，实际地址需拼上你的域名。
                    </div>
                  </div>
                ) : null}
              </>
            ) : (
              <div className="rounded-xl border border-qq-border bg-white p-4">
                <div className="mb-3 text-[13px] font-medium">QQ 官方凭据</div>
                <div className="grid grid-cols-2 gap-3">
                  <Field label="AppID">
                    <input
                      className={inputCls}
                      value={form.qq.appId}
                      onChange={(e) => patch('qq.appId', e.target.value)}
                    />
                  </Field>
                  <Field label="AppSecret">
                    <input
                      className={inputCls}
                      type="password"
                      value={form.qq.secret}
                      onChange={(e) => patch('qq.secret', e.target.value)}
                    />
                  </Field>
                  <Field label="机器人群内 openid" hint="留空则首次被 @ 时自动学习">
                    <input
                      className={inputCls}
                      value={form.qq.selfOpenId}
                      onChange={(e) => patch('qq.selfOpenId', e.target.value)}
                    />
                  </Field>
                  <Field label="沙箱环境">
                    <div className="py-1">
                      <Toggle checked={form.qq.useSandbox} onChange={(v) => patch('qq.useSandbox', v)} />
                    </div>
                  </Field>
                </div>
              </div>
            )}

            {!isNew && statsOf(form.id) ? (
              <div className="rounded-xl border border-qq-border bg-white p-4">
                <div className="mb-2 text-[13px] font-medium">发送统计（累计）</div>
                <div className="flex items-center gap-5 text-[13px]">
                  <span>
                    成功 <span className="font-medium">{statsOf(form.id).sent}</span>
                  </span>
                  <span>
                    失败{' '}
                    <span className={statsOf(form.id).failed > 0 ? 'font-medium text-red-500' : 'font-medium'}>
                      {statsOf(form.id).failed}
                    </span>
                  </span>
                </div>
                {statsOf(form.id).lastError ? (
                  <div className="mt-1.5 text-[11px] leading-snug text-qq-sub">
                    最后错误：{statsOf(form.id).lastError}
                  </div>
                ) : null}
              </div>
            ) : null}

            {!isNew ? (
              <div className="flex items-center justify-between rounded-xl border border-qq-border bg-white p-4">
                <div className="text-[12px] text-qq-sub">
                  删除后该实例停止收发；历史与画像仍保留在库中。
                </div>
                <div className="flex gap-2">
                  <button
                    className="rounded-md border border-qq-border px-3 py-1.5 text-[13px] hover:bg-qq-hover"
                    onClick={() => toggleEnabled({ id: form.id }, !form.enabled)}
                  >
                    {form.enabled ? '禁用' : '启用'}
                  </button>
                  <button
                    className="rounded-md border border-red-200 px-3 py-1.5 text-[13px] text-red-600 hover:bg-red-50"
                    onClick={() => remove({ id: form.id, name: form.name })}
                  >
                    删除
                  </button>
                </div>
              </div>
            ) : null}
          </div>
        )}
      </div>
    </div>
  );
}
