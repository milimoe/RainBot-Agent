import { useEffect, useRef, useState } from 'react';
import QRCode from 'qrcode';
import { api, AuthError } from '../lib/api.js';
import { toast } from './ui.jsx';

const labels = { pending: '请用 B 站 App 扫码', scanned: '已扫码，请在手机上确认', expired: '二维码已过期，请重新生成', failed: '登录失败，请重新生成二维码' };

export default function BilibiliLogin({ onAuthFail }) {
  const onAuthRef = useRef(onAuthFail);
  useEffect(() => { onAuthRef.current = onAuthFail; }, [onAuthFail]);
  const [status, setStatus] = useState(null);
  const [qr, setQr] = useState(null);
  const [image, setImage] = useState('');
  const [state, setState] = useState('pending');
  const [busy, setBusy] = useState(false);
  const report = (e) => { if (e instanceof AuthError) onAuthFail?.(); else toast(e.message, 'error'); };

  useEffect(() => {
    const controller = new AbortController();
    api('/api/webui/bilibili/login', { signal: controller.signal }).then(setStatus).catch(e => {
      if (e.name !== 'AbortError') { if (e instanceof AuthError) onAuthRef.current?.(); else toast(e.message, 'error'); }
    });
    return () => controller.abort();
  }, []);

  useEffect(() => {
    if (!qr) return;
    let active = true;
    let timer;
    const controller = new AbortController();
    QRCode.toDataURL(qr.url, { width: 224, margin: 4, errorCorrectionLevel: 'M' })
      .then(value => { if (active) setImage(value); }).catch(() => { if (active) setState('failed'); });
    const poll = async () => {
      try {
        if (Date.now() >= Date.parse(qr.expiresAt)) { if (active) setState('expired'); return; }
        const result = await api(`/api/webui/bilibili/login/qr/${qr.id}/poll`, { method: 'POST', signal: controller.signal });
        if (!active) return;
        if (result.state === 'confirmed') {
          setStatus(await api('/api/webui/bilibili/login?verify=true', { signal: controller.signal }));
          if (!active) return;
          setQr(null);
          setImage('');
          toast('B 站登录凭据已保存', 'success');
          return;
        }
        setState(result.state);
        if (result.state === 'expired' || result.state === 'failed') return;
        timer = setTimeout(poll, 2000);
      } catch (e) {
        if (!active) return;
        setState('failed');
        if (e instanceof AuthError) onAuthRef.current?.(); else toast(e.message, 'error');
      }
    };
    timer = setTimeout(poll, 2000);
    return () => {
      active = false;
      controller.abort();
      clearTimeout(timer);
      api(`/api/webui/bilibili/login/qr/${qr.id}`, { method: 'DELETE' }).catch(() => {});
    };
  }, [qr]);

  const run = async (action) => { setBusy(true); try { await action(); } catch (e) { report(e); } finally { setBusy(false); } };
  const start = () => run(async () => {
    setQr(null); setImage('');
    const result = await api('/api/webui/bilibili/login/qr', { method: 'POST' });
    setState('pending'); setQr(result);
  });
  const clear = () => run(async () => {
    setQr(null); setImage('');
    await api('/api/webui/bilibili/login', { method: 'DELETE' });
    setStatus(await api('/api/webui/bilibili/login'));
    toast('已清除扫码登录凭据', 'success');
  });
  const button = 'rounded-md border border-qq-border px-3 py-1.5 text-sm hover:bg-qq-hover disabled:opacity-50';
  return <section className="rounded-xl border border-qq-border bg-white p-4">
    <h2 className="mb-2 text-[15px] font-medium">B 站账号</h2>
    <p className="text-sm text-qq-sub" role="status">{status?.message || '正在读取登录状态…'}</p>
    {status?.source === 'manual' && <p className="mt-1 text-xs text-qq-sub">手动 Cookie 配置优先于扫码登录。清除扫码凭据不会清除手动配置。</p>}
    <div className="mt-3 flex flex-wrap gap-2">
      <button className={button} disabled={busy} onClick={start}>{qr ? '重新生成二维码' : '扫码登录 / 重新登录'}</button>
      <button className={button} disabled={busy} onClick={() => run(async () => setStatus(await api('/api/webui/bilibili/login?verify=true')))}>检查登录状态</button>
      <button className={button} disabled={busy} onClick={clear}>清除扫码凭据</button>
      {status?.source === 'manual' && <button className={button} disabled={busy} onClick={() => run(async () => {
        await api('/api/webui/config/Page.BilibiliCookies', { method: 'DELETE' });
        setStatus(await api('/api/webui/bilibili/login'));
      })}>恢复默认 Cookie 配置</button>}
    </div>
    {qr && <div className="mt-4">
      {image && <img src={image} width="224" height="224" alt="B 站扫码登录二维码" />}
      <p className="mt-2 text-sm" role="status">{labels[state] || '正在验证登录…'}</p>
      <button className={`${button} mt-2`} onClick={() => { setQr(null); setImage(''); }}>取消登录</button>
    </div>}
    <p className="mt-3 text-xs text-qq-sub">登录可改善解析成功率；遇到风控会暂停请求。凭据失效后请重新扫码。</p>
  </section>;
}
