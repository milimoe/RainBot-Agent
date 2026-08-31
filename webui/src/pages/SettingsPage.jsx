import { useCallback, useEffect, useState } from 'react';
import { api, AuthError, getToken, setToken } from '../lib/api.js';
import { Badge, toast } from '../components/ui.jsx';
import { IconPlus, IconX, IconLock } from '../components/Icons.jsx';

/**
 * 设置页：人设（热重载）/ SayNo 词表 / 管理员 / OSM 梗图 / 访问安全。
 * 机器人凭据请在「机器人」页按实例维护（多实例架构，无全局网关配置）。
 */
export default function SettingsPage({ boot, onAuthFail }) {
  return (
    <div className="qq-scroll h-full min-w-0 flex-1 overflow-y-auto">
      <div className="mx-auto max-w-3xl space-y-5 px-6 py-6">
        <PageTitle title="设置" desc="人设与词表保存后即时热重载；机器人凭据请到「机器人」页按实例维护。" />
        <PersonaCard onAuthFail={onAuthFail} />
        <SayNoCard onAuthFail={onAuthFail} />
        <AdminCard onAuthFail={onAuthFail} />
        <OsmCard onAuthFail={onAuthFail} />
        <SecurityCard boot={boot} />
      </div>
    </div>
  );
}

function PageTitle({ title, desc }) {
  return (
    <div>
      <h1 className="text-lg font-semibold">{title}</h1>
      <p className="mt-0.5 text-xs text-qq-sub">{desc}</p>
    </div>
  );
}

function Card({ title, desc, children, right }) {
  return (
    <div className="overflow-hidden rounded-xl border border-qq-border bg-white">
      <div className="flex items-center justify-between border-b border-qq-border/70 px-4 py-3">
        <div>
          <div className="text-[14px] font-medium">{title}</div>
          {desc && <div className="mt-0.5 text-xs text-qq-sub">{desc}</div>}
        </div>
        {right}
      </div>
      <div className="p-4">{children}</div>
    </div>
  );
}

// ---------- 人设 ----------

function PersonaCard({ onAuthFail }) {
  const [data, setData] = useState({ path: '', content: '' });
  const [loaded, setLoaded] = useState(false);
  const [dirty, setDirty] = useState(false);
  const [saving, setSaving] = useState(false);

  const load = useCallback(async () => {
    try {
      const d = await api('/api/webui/settings/persona');
      setData(d);
      setLoaded(true);
      setDirty(false);
    } catch (e) {
      if (e instanceof AuthError) onAuthFail?.();
      else toast(e.message, 'error');
    }
  }, [onAuthFail]);

  useEffect(() => {
    load();
  }, [load]);

  const save = async () => {
    setSaving(true);
    try {
      await api('/api/webui/settings/persona', { method: 'PUT', body: { content: data.content } });
      toast('人设已保存（热重载生效）', 'success');
      setDirty(false);
    } catch (e) {
      toast(e.message, 'error');
    } finally {
      setSaving(false);
    }
  };

  return (
    <Card
      title="👤 人设（Persona）"
      desc={loaded ? `文件：${data.path} · 保存后即时热重载（Block A 前缀会变，缓存命中率短期下降属正常）` : '加载中…'}
      right={
        <button
          className="rounded-md bg-qq-blue px-3 py-1 text-xs text-white hover:bg-qq-blue-deep disabled:opacity-40"
          onClick={save}
          disabled={!dirty || saving}
        >
          {saving ? '保存中…' : '保存人设'}
        </button>
      }
    >
      <textarea
        value={data.content}
        onChange={(e) => {
          setData((d) => ({ ...d, content: e.target.value }));
          setDirty(true);
        }}
        rows={16}
        className="qq-scroll w-full resize-y rounded-lg border border-qq-border bg-qq-bg/50 p-3 font-mono text-[12.5px] leading-5 outline-none focus:border-qq-blue focus:ring-1 focus:ring-qq-blue/30"
        placeholder="# 雨&#10;你是雨，一个温柔灵动的 QQ 群聊机器人。"
      />
    </Card>
  );
}

// ---------- SayNo 词表 ----------

function SayNoCard({ onAuthFail }) {
  const [data, setData] = useState({ path: '', tables: {} });
  const [open, setOpen] = useState(null);
  const [word, setWord] = useState('');
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    try {
      const d = await api('/api/webui/settings/sayno');
      setData(d);
    } catch (e) {
      if (e instanceof AuthError) onAuthFail?.();
      else toast(e.message, 'error');
    }
  }, [onAuthFail]);

  useEffect(() => {
    load();
  }, [load]);

  const update = async (table, w, add) => {
    setBusy(true);
    try {
      await api('/api/webui/settings/sayno', { method: 'POST', body: { table, word: w, add } });
      toast(add ? '已添加词条' : '已移除词条', 'success');
      await load();
    } catch (e) {
      toast(e.message, 'error');
    } finally {
      setBusy(false);
    }
  };

  const tables = Object.entries(data.tables || {});

  return (
    <Card
      title="🎲 SayNo 反驳词表"
      desc={data.path ? `文件：${data.path} · 保存即热重载，字段名与原版 RainBOT 一致` : ''}
      right={<Badge tone="gray">{tables.length} 张表</Badge>}
    >
      <div className="space-y-2">
        {tables.map(([name, words]) => (
          <div key={name} className="overflow-hidden rounded-lg border border-qq-border">
            <button
              className="flex w-full items-center justify-between bg-qq-bg/60 px-3 py-2 text-left hover:bg-qq-hover"
              onClick={() => setOpen(open === name ? null : name)}
            >
              <span className="font-mono text-[12.5px] font-medium">{name}</span>
              <span className="text-xs text-qq-sub">
                {words.length} 词 {open === name ? '▴' : '▾'}
              </span>
            </button>
            {open === name && (
              <div className="px-3 py-2.5">
                <div className="mb-2 flex flex-wrap gap-1.5">
                  {words.length === 0 && <span className="text-xs text-qq-sub">（空表）</span>}
                  {words.map((w) => (
                    <span key={w} className="flex items-center gap-1 rounded-full border border-qq-border bg-white px-2 py-0.5 text-xs">
                      {w}
                      <button className="text-qq-sub hover:text-qq-red" disabled={busy} onClick={() => update(name, w, false)}>
                        <IconX size={11} />
                      </button>
                    </span>
                  ))}
                </div>
                <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
                  <input
                    value={word}
                    onChange={(e) => setWord(e.target.value)}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter' && word.trim()) {
                        update(name, word.trim(), true);
                        setWord('');
                      }
                    }}
                    placeholder={`向「${name}」添加词条…`}
                    className="w-full rounded-md border border-qq-border px-2.5 py-1.5 text-[13px] outline-none focus:border-qq-blue"
                  />
                  <button
                    className="flex shrink-0 items-center justify-center gap-1 rounded-md bg-qq-blue px-3 py-1.5 text-xs text-white hover:bg-qq-blue-deep disabled:opacity-40"
                    disabled={!word.trim() || busy}
                    onClick={() => {
                      update(name, word.trim(), true);
                      setWord('');
                    }}
                  >
                    <IconPlus size={12} /> 添加
                  </button>
                </div>
              </div>
            )}
          </div>
        ))}
      </div>
    </Card>
  );
}

// ---------- 管理员 ----------

function AdminCard({ onAuthFail }) {
  const [admins, setAdmins] = useState([]);
  const [input, setInput] = useState('');
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    try {
      const d = await api('/api/webui/settings/admins');
      setAdmins(d.openIds || []);
    } catch (e) {
      if (e instanceof AuthError) onAuthFail?.();
      else toast(e.message, 'error');
    }
  }, [onAuthFail]);

  useEffect(() => {
    load();
  }, [load]);

  const add = async () => {
    if (!input.trim() || busy) return;
    setBusy(true);
    try {
      await api('/api/webui/settings/admins', { method: 'POST', body: { openId: input.trim() } });
      setInput('');
      toast('已添加管理员', 'success');
      await load();
    } catch (e) {
      toast(e.message, 'error');
    } finally {
      setBusy(false);
    }
  };

  const remove = async (id) => {
    if (busy) return;
    setBusy(true);
    try {
      await api(`/api/webui/settings/admins/${encodeURIComponent(id)}`, { method: 'DELETE' });
      toast('已移除管理员', 'success');
      await load();
    } catch (e) {
      toast(e.message, 'error');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Card title="👮 管理员 OpenID" desc="机器人自我维护的管理员列表（与群管理员无关），管理员可用全部 /admin 指令">
      <div className="mb-2.5 flex flex-wrap gap-1.5">
        {admins.length === 0 && <span className="text-xs text-qq-sub">暂无管理员（机器人首次被 @ 不会自动添加，请手动维护）</span>}
        {admins.map((id) => (
          <span key={id} className="flex items-center gap-1.5 rounded-full bg-qq-bg px-2.5 py-1 font-mono text-xs">
            {id}
            <button className="text-qq-sub hover:text-qq-red" disabled={busy} onClick={() => remove(id)}>
              <IconX size={11} />
            </button>
          </span>
        ))}
      </div>
      <div className="flex items-center gap-2">
        <input
          value={input}
          onChange={(e) => setInput(e.target.value)}
          onKeyDown={(e) => e.key === 'Enter' && add()}
          placeholder="粘贴群友 OpenID…"
          className="w-full rounded-md border border-qq-border px-2.5 py-1.5 font-mono text-[12.5px] outline-none focus:border-qq-blue"
        />
        <button
          className="flex shrink-0 items-center gap-1 rounded-md bg-qq-blue px-3 py-1.5 text-xs text-white hover:bg-qq-blue-deep disabled:opacity-40"
          disabled={!input.trim() || busy}
          onClick={add}
        >
          <IconPlus size={12} /> 添加
        </button>
      </div>
    </Card>
  );
}

// ---------- OSM 梗图（域名 + 目录自动扫描） ----------

function OsmCard({ onAuthFail }) {
  const [data, setData] = useState(null);
  const [domain, setDomain] = useState('');
  const [domainDirty, setDomainDirty] = useState(false);
  const [saving, setSaving] = useState(false);

  const load = useCallback(async () => {
    try {
      const d = await api('/api/webui/settings/osm');
      setData(d);
      setDomain(d.baseUrl || '');
      setDomainDirty(false);
    } catch (e) {
      if (e instanceof AuthError) onAuthFail?.();
      else toast(e.message, 'error');
    }
  }, [onAuthFail]);

  useEffect(() => {
    load();
  }, [load]);

  const saveDomain = async () => {
    setSaving(true);
    try {
      await api('/api/webui/config/PublicBaseUrl', { method: 'PUT', body: { value: domain.trim() } });
      toast('公网域名已保存（静态资源立即生效）', 'success');
      await load();
    } catch (e) {
      toast(e.message, 'error');
    } finally {
      setSaving(false);
    }
  };

  const files = data?.files || [];
  const urls = data?.urls || [];

  return (
    <Card
      title="🖼️ OSM 梗图（自动扫描，无需配置图片路径）"
      desc="把图片放进 wwwroot/osm/ 目录（支持子目录）即自动生效；对外地址 = 公网域名 + 相对路径"
      right={<Badge tone={files.length > 0 ? 'blue' : 'gray'}>{files.length} 张图</Badge>}
    >
      <div className="space-y-3">
        <div>
          <label className="mb-1 block text-xs text-qq-sub">
            公网域名（只需设置一次，所有静态资源共用）
          </label>
          <div className="flex items-center gap-2">
            <input
              value={domain}
              onChange={(e) => {
                setDomain(e.target.value);
                setDomainDirty(true);
              }}
              placeholder="https://你的域名"
              className="w-full rounded-md border border-qq-border px-2.5 py-1.5 font-mono text-[12.5px] outline-none focus:border-qq-blue focus:ring-1 focus:ring-qq-blue/30"
            />
            <button
              className="shrink-0 rounded-md bg-qq-blue px-3 py-1.5 text-xs text-white hover:bg-qq-blue-deep disabled:opacity-40"
              onClick={saveDomain}
              disabled={!domainDirty || saving}
            >
              {saving ? '保存中…' : '保存域名'}
            </button>
          </div>
          <div className="mt-1.5 text-[11px] text-qq-sub">
            目录：<code className="rounded bg-qq-bg px-1">{data?.directory || 'wwwroot/osm'}</code>
            {!domain && ' · 未设置域名时 OSM 自动禁用'}
          </div>
        </div>

        {files.length === 0 ? (
          <div className="rounded-lg border border-dashed border-qq-border bg-qq-bg/40 p-4 text-center text-xs leading-5 text-qq-sub">
            暂无图片 —— 把梗图（jpg/png/gif/webp）放进 <code className="rounded bg-qq-border/50 px-1">wwwroot/osm/</code>{' '}
            目录即自动生效，无需重启
          </div>
        ) : (
          <div className="qq-scroll max-h-56 space-y-1.5 overflow-y-auto rounded-lg border border-qq-border p-2.5">
            {files.map((f, i) => (
              <div key={f} className="flex items-center gap-2 rounded px-2 py-1 text-xs hover:bg-qq-hover">
                <span className="w-5 text-center text-qq-sub">{i + 1}</span>
                <span className="min-w-0 flex-1 truncate font-mono">{f}</span>
                <span className="min-w-0 flex-1 truncate font-mono text-qq-sub">
                  {urls[i] ? <a href={urls[i]} target="_blank" rel="noreferrer" className="text-qq-blue hover:underline">{urls[i]}</a> : '（未设置域名）'}
                </span>
              </div>
            ))}
          </div>
        )}
      </div>
    </Card>
  );
}

// ---------- 访问安全 ----------

function SecurityCard({ boot }) {
  const [showKey, setShowKey] = useState(false);
  const token = getToken();
  const authRequired = !!boot?.authRequired;

  return (
    <Card title="🔐 访问安全" desc="WebUI 控制台鉴权（Rain:WebUi:Token，配置于 appsettings 或环境变量 RAIN__WEBUI__TOKEN）">
      <div className="space-y-3 text-[13px]">
        <div className="flex items-center gap-2">
          <span className="text-qq-sub">服务端鉴权：</span>
          {authRequired ? <Badge tone="green">已启用（需要令牌）</Badge> : <Badge tone="orange">未启用（任何能访问端口的人都可操作，仅建议内网）</Badge>}
        </div>
        <div className="flex items-center gap-2">
          <span className="text-qq-sub">本浏览器令牌：</span>
          {token ? (
            <span className="flex items-center gap-2 font-mono text-xs">
              {showKey ? token : '••••••••（已保存）'}
              <button className="text-qq-blue hover:underline" onClick={() => setShowKey((v) => !v)}>
                {showKey ? '隐藏' : '显示'}
              </button>
              <button
                className="text-qq-red hover:underline"
                onClick={() => {
                  setToken('');
                  window.location.reload();
                }}
              >
                清除并重新登录
              </button>
            </span>
          ) : (
            <span className="text-xs text-qq-sub">未保存（服务端开启鉴权时会弹窗输入）</span>
          )}
        </div>
        <div className="flex items-start gap-2 rounded-lg bg-qq-bg p-3 text-xs leading-5 text-qq-sub">
          <span className="mt-0.5 text-qq-warn">
            <IconLock size={14} />
          </span>
          <span>
            提示：配置页包含 DeepSeek API Key 等敏感信息。公网部署时请务必设置
            <code className="mx-1 rounded bg-qq-border/60 px-1">Rain:WebUi:Token</code>
            并将 WebUI 置于反代之后。
          </span>
        </div>
      </div>
    </Card>
  );
}
