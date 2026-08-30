import { useCallback, useEffect, useState } from 'react';
import { api, AuthError, getToken, openEventStream, setToken } from './lib/api.js';
import { IconBook, IconChat, IconGear, IconLog, IconPulse, IconRobot, IconSliders } from './components/Icons.jsx';
import { Toasts, toast } from './components/ui.jsx';
import ChatPage from './pages/ChatPage.jsx';
import CommandsPage from './pages/CommandsPage.jsx';
import ConfigPage from './pages/ConfigPage.jsx';
import SettingsPage from './pages/SettingsPage.jsx';
import StatusPage from './pages/StatusPage.jsx';
import LogsPage from './pages/LogsPage.jsx';
import BotsPage from './pages/BotsPage.jsx';

const PAGES = [
  { id: 'chat', label: '消息', icon: IconChat },
  { id: 'commands', label: '指令', icon: IconBook },
  { id: 'bots', label: '机器人', icon: IconRobot },
  { id: 'config', label: '配置', icon: IconSliders },
  { id: 'settings', label: '设置', icon: IconGear },
  { id: 'logs', label: '日志', icon: IconLog },
  { id: 'status', label: '状态', icon: IconPulse },
];

/**
 * RainBot 控制台外壳：左侧 NTQQ 风格导航 + 页面切换 + 全局 SSE + 令牌登录。
 */
export default function App() {
  const [page, setPage] = useState('chat');
  const [boot, setBoot] = useState(null);
  const [needToken, setNeedToken] = useState(false);
  const [tokenInput, setTokenInput] = useState('');
  const [streamState, setStreamState] = useState('closed');

  const bootstrap = useCallback(async () => {
    try {
      const s = await api('/api/webui/status');
      setBoot(s);
      setNeedToken(false);
    } catch (e) {
      if (e instanceof AuthError) {
        setNeedToken(true);
        if (getToken()) setToken('');
      } else if (!(e instanceof TypeError)) {
        toast(e.message, 'error');
      }
    }
  }, []);

  useEffect(() => {
    bootstrap();
  }, [bootstrap]);

  // 全局 SSE：事件转发为 window 事件，供各页面订阅
  useEffect(() => {
    if (needToken || (boot?.authRequired && !getToken())) return;
    const close = openEventStream(
      (ev) => {
        window.dispatchEvent(new CustomEvent('webui:event', { detail: ev }));
        if (ev.type === 'status') window.dispatchEvent(new CustomEvent('webui:status-event', { detail: ev.data }));
      },
      (state) => setStreamState(state)
    );
    return close;
  }, [needToken, boot?.authRequired]);

  const submitToken = () => {
    const t = tokenInput.trim();
    if (!t) return;
    setToken(t);
    setNeedToken(false);
    bootstrap();
  };

  const wsConnected = streamState !== 'closed';

  return (
    <div className="flex h-full w-full overflow-hidden">
      {/* 左侧导航栏（仿 NTQQ 极窄导航；移动端仅显示图标） */}
      <nav className="flex w-[56px] shrink-0 flex-col items-center gap-1 border-r border-qq-border bg-qq-panel py-3 md:w-[72px]">
        <div
          className="mb-3 flex h-10 w-10 select-none items-center justify-center rounded-[10px] text-xl text-white shadow-sm md:h-11 md:w-11 md:text-2xl"
          style={{ background: 'linear-gradient(135deg, #36c7f7 0%, #12b7f5 55%, #0099ff 100%)' }}
          title="RainBot「雨」"
        >
          🌧️
        </div>
        {PAGES.map(({ id, label, icon: Icon }) => (
          <button key={id} className={`nav-item ${page === id ? 'active' : ''}`} onClick={() => setPage(id)}>
            <Icon size={22} />
            <span className="hidden md:block">{label}</span>
          </button>
        ))}
        <div className="mt-auto flex flex-col items-center gap-1">
          {needToken ? (
            <button className="nav-item" onClick={() => setNeedToken(true)} title="需要访问令牌">
              <span className="text-qq-warn">🔑</span>
              <span className="hidden md:block">登录</span>
            </button>
          ) : (
            <div className="nav-item" title={wsConnected ? '实时事件已连接' : '实时事件连接中…'}>
              <span className={`h-2.5 w-2.5 rounded-full ${wsConnected ? 'bg-qq-green' : 'bg-qq-sub animate-pulse'}`} />
              <span className="hidden md:block">{wsConnected ? '在线' : '连接中'}</span>
            </div>
          )}
        </div>
      </nav>

      {/* 页面内容 */}
      {page === 'chat' && <ChatPage boot={boot} onAuthFail={() => setNeedToken(true)} />}
      {page === 'commands' && <CommandsPage />}
      {page === 'bots' && <BotsPage />}
      {page === 'config' && <ConfigPage onAuthFail={() => setNeedToken(true)} />}
      {page === 'settings' && <SettingsPage boot={boot} onAuthFail={() => setNeedToken(true)} />}
      {page === 'logs' && <LogsPage onAuthFail={() => setNeedToken(true)} />}
      {page === 'status' && <StatusPage onAuthFail={() => setNeedToken(true)} />}

      {/* 令牌登录弹窗 */}
      {needToken && (
        <div className="fixed inset-0 z-[300] flex items-center justify-center bg-black/30 backdrop-blur-sm">
          <div className="w-[340px] rounded-2xl bg-white p-6 shadow-2xl">
            <div className="mb-1 flex items-center gap-2 text-lg font-semibold">
              <span>🔐</span> 需要访问令牌
            </div>
            <p className="mb-4 text-xs leading-5 text-qq-sub">
              服务端已启用 <code className="rounded bg-qq-bg px-1">Rain:WebUi:Token</code>。请输入令牌（将保存在本浏览器）。
            </p>
            <input
              autoFocus
              type="password"
              value={tokenInput}
              onChange={(e) => setTokenInput(e.target.value)}
              onKeyDown={(e) => e.key === 'Enter' && submitToken()}
              placeholder="访问令牌"
              className="mb-4 w-full rounded-lg border border-qq-border px-3 py-2 text-sm outline-none focus:border-qq-blue focus:ring-1 focus:ring-qq-blue/30"
            />
            <div className="flex justify-end gap-2">
              <button
                className="rounded-lg px-4 py-2 text-sm text-qq-sub hover:bg-qq-hover"
                onClick={() => {
                  setNeedToken(false);
                  setTokenInput('');
                }}
              >
                取消
              </button>
              <button
                className="rounded-lg bg-qq-blue px-4 py-2 text-sm text-white hover:bg-qq-blue-deep disabled:opacity-40"
                disabled={!tokenInput.trim()}
                onClick={submitToken}
              >
                登录
              </button>
            </div>
          </div>
        </div>
      )}

      <Toasts />
    </div>
  );
}
