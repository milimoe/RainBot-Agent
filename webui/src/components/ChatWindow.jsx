import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { api } from '../lib/api.js';
import { BOT_SENDER, fmtDay, groupName, SIM_GROUP, SIM_MEMBERS } from '../lib/util.js';
import MessageBubble from './MessageBubble.jsx';
import Avatar from './Avatar.jsx';
import { Badge, toast } from './ui.jsx';
import { IconBack, IconMore, IconSend, IconSmile, IconX, IconRobot } from './Icons.jsx';

const EMOJIS = ['😀', '😂', '🤣', '😊', '😘', '🥰', '🤔', '😴', '🥺', '😭', '😤', '👍', '👏', '🙏', '❤️', '🔥', '🌧️', '☔', '🌈', '🎉', '🍉', '🧋'];

/**
 * 仿 NTQQ 聊天窗口：标题栏 + 消息区（日期分隔线 / 气泡 / 正在输入）+ 输入区。
 * 试聊群可模拟群友发言（走真实处理链，机器人回复只显示在网页）；
 * 真实群开启「试聊拦截」后同样支持网页内试聊。
 */
export default function ChatWindow({ group, onGroupsChanged, onBack }) {
  const [messages, setMessages] = useState([]);
  const [names, setNames] = useState({});
  const [loading, setLoading] = useState(true);
  const [typing, setTyping] = useState(false);
  const [sending, setSending] = useState(false);
  const [menuOpen, setMenuOpen] = useState(false);
  const [emojiOpen, setEmojiOpen] = useState(false);
  const [input, setInput] = useState('');
  const [memberIdx, setMemberIdx] = useState(0);
  const [isAt, setIsAt] = useState(true);
  const [isAdmin, setIsAdmin] = useState(true);

  const idsRef = useRef(new Set());
  const listRef = useRef(null);
  const stickRef = useRef(true); // 是否贴底
  const typingTimer = useRef(null);
  const activeGroupRef = useRef(group?.group);
  activeGroupRef.current = group?.group;
  const isSim = group?.group === SIM_GROUP;
  const canSim = isSim || group?.simEnabled;

  const appendMessage = useCallback((m) => {
    setMessages((prev) => {
      if (m.id && idsRef.current.has(m.id)) return prev;
      if (m.id) idsRef.current.add(m.id);
      return [...prev, m];
    });
  }, []);

  // 加载历史
  const loadMessages = useCallback(async () => {
    if (!group?.group) return;
    setLoading(true);
    try {
      const data = await api(`/api/webui/groups/${encodeURIComponent(group.group)}/messages?limit=300`);
      if (activeGroupRef.current !== group.group) return;
      const list = data.messages || [];
      setNames((previous) => ({ ...(data.names || {}), ...previous }));
      setMessages((previous) => {
        const loadedIds = new Set(list.filter((m) => m.id).map((m) => m.id));
        const merged = [...list, ...previous.filter((m) => !m.id || !loadedIds.has(m.id))]
          .sort((a, b) => new Date(a.time) - new Date(b.time));
        idsRef.current = new Set(merged.filter((m) => m.id).map((m) => m.id));
        return merged;
      });
      stickRef.current = true;
    } catch (e) {
      toast(e.message, 'error');
    } finally {
      if (activeGroupRef.current === group.group) setLoading(false);
    }
  }, [group?.group]);

  useEffect(() => {
    setMessages([]);
    setNames({});
    idsRef.current = new Set();
    setMenuOpen(false);
    setEmojiOpen(false);
    setInput('');
    setTyping(false);
    loadMessages();
  }, [group?.group, loadMessages]);

  // 实时事件（SSE → window 事件）
  useEffect(() => {
    const h = (e) => {
      const ev = e.detail || {};
      const d = ev.data || {};
      if (!d.group || d.group !== group?.group) return;
      if (ev.type === 'message') {
        setNames((previous) => ({ ...previous, ...(d.names || {}), ...(d.username ? { [d.sender]: d.username } : {}) }));
        appendMessage({ id: d.msgId, sender: d.sender, username: d.username, shortId: d.shortId, content: d.content, isBot: false, time: d.time });
        if (isSim || d.simulated) setTyping(true);
        if (typingTimer.current) clearTimeout(typingTimer.current);
        typingTimer.current = setTimeout(() => setTyping(false), 90000);
      } else if (ev.type === 'bot_message') {
        appendMessage({ id: `bot-${d.time}`, sender: d.sender, content: d.content, isBot: true, time: d.time });
        setTyping(false);
      } else if (ev.type === 'simulation' && d.group === group?.group) {
        onGroupsChanged?.();
      }
    };
    window.addEventListener('webui:event', h);
    return () => {
      window.removeEventListener('webui:event', h);
      if (typingTimer.current) clearTimeout(typingTimer.current);
    };
  }, [group?.group, appendMessage, isSim, onGroupsChanged]);

  // 贴底滚动
  const handleScroll = () => {
    const el = listRef.current;
    if (!el) return;
    stickRef.current = el.scrollHeight - el.scrollTop - el.clientHeight < 80;
  };
  useEffect(() => {
    const el = listRef.current;
    if (el && stickRef.current) el.scrollTop = el.scrollHeight;
  }, [messages, typing]);

  // 发送试聊消息
  const send = async () => {
    const text = input.trim();
    if (!text || sending || !group) return;
    setSending(true);
    try {
      const member = SIM_MEMBERS[memberIdx] || SIM_MEMBERS[0];
      const res = await api('/api/webui/simulate', {
        method: 'POST',
        body: { group: group.group, sender: member.id, username: member.name, content: text, isAt, isAdmin },
      });
      appendMessage({ id: res.msgId, sender: member.id, username: member.name, content: text, isBot: false, time: new Date().toISOString() });
      setInput('');
      setEmojiOpen(false);
      setTyping(true);
      if (typingTimer.current) clearTimeout(typingTimer.current);
      typingTimer.current = setTimeout(() => setTyping(false), 90000);
    } catch (e) {
      toast(e.message, 'error');
    } finally {
      setSending(false);
    }
  };

  // 群操作
  const doAction = async (action) => {
    setMenuOpen(false);
    if (!group) return;
    try {
      if (action === 'mute') {
        await api(`/api/webui/groups/${encodeURIComponent(group.group)}/mute`, { method: 'POST', body: { muted: !group.muted } });
        toast(group.muted ? '已解除静默' : '已静默该群', 'success');
      } else if (action === 'reset') {
        if (!window.confirm('重置该群上下文？（清空上下文缓存，机器人将重建历史）')) return;
        await api(`/api/webui/groups/${encodeURIComponent(group.group)}/reset-context`, { method: 'POST' });
        toast('上下文已重置', 'success');
      } else if (action === 'sim') {
        await api('/api/webui/sim-mode', { method: 'POST', body: { group: group.group, enabled: !group.simEnabled } });
        toast(group.simEnabled ? '已关闭试聊拦截' : '已开启试聊拦截（机器人回复只显示在网页）', 'success');
      } else if (action === 'refresh') {
        await loadMessages();
        onGroupsChanged?.();
      }
      onGroupsChanged?.();
    } catch (e) {
      toast(e.message, 'error');
    }
  };

  // 带日期分隔线的消息列表
  const messageNodes = useMemo(() => {
    const nodes = [];
    let lastDay = null;
    for (const m of messages) {
      const day = new Date(m.time).toDateString();
      if (day !== lastDay) {
        nodes.push(
          <div key={`day-${m.id || m.time}`} className="my-3 flex justify-center">
            <span className="rounded bg-[#e3e6ea]/80 px-2 py-px text-[10px] text-qq-sub">{fmtDay(m.time)}</span>
          </div>
        );
        lastDay = day;
      }
      nodes.push(
        <div key={m.id || `${m.sender}-${m.time}`} className="mb-3">
          <MessageBubble msg={m} names={names} />
        </div>
      );
    }
    return nodes;
  }, [messages, names]);

  if (!group) {
    return <div className="flex flex-1 items-center justify-center bg-qq-panel text-sm text-qq-sub">选择一个群开始查看</div>;
  }

  return (
    <div className="flex h-full min-w-0 flex-1 flex-col bg-qq-panel">
      {/* 标题栏 */}
      <div className="flex h-[56px] shrink-0 items-center justify-between border-b border-qq-border px-3 md:px-4">
        <div className="flex min-w-0 items-center gap-2.5">
          {onBack && (
            <button className="shrink-0 rounded p-1 text-qq-sub hover:bg-qq-hover hover:text-qq-text" onClick={onBack} title="返回群列表">
              <IconBack />
            </button>
          )}
          <Avatar sender={group.group} name={groupName(group.group)} size={34} isBot={isSim} />
          <div className="min-w-0">
            <div className="flex items-center gap-1.5 text-[15px] font-medium">
              {groupName(group.group)}
              {isSim && <Badge tone="blue">试聊群</Badge>}
              {!isSim && group.simEnabled && <Badge tone="orange">试聊拦截</Badge>}
              {group.muted && <Badge tone="gray">静默</Badge>}
              {group.degraded && <Badge tone="red">降级</Badge>}
            </div>
            <div className="truncate text-[11px] text-qq-sub">
              {isSim
                ? '网页内模拟群友发言，机器人回复仅显示在此处（不走 QQ）'
                : `群 OpenID ${group.group.slice(0, 8)}… · 累计 ${group.totalMessages || 0} 条消息`}
            </div>
          </div>
        </div>
        <div className="relative">
          <button className="rounded p-1.5 text-qq-sub hover:bg-qq-hover hover:text-qq-text" onClick={() => setMenuOpen((v) => !v)}>
            <IconMore />
          </button>
          {menuOpen && (
            <>
              <div className="fixed inset-0 z-10" onClick={() => setMenuOpen(false)} />
              <div className="absolute right-0 top-9 z-20 w-44 overflow-hidden rounded-lg border border-qq-border bg-white py-1 shadow-lg">
                {!isSim && (
                  <MenuItem onClick={() => doAction('sim')}>
                    <span>{group.simEnabled ? '关闭试聊拦截' : '开启试聊拦截'}</span>
                  </MenuItem>
                )}
                <MenuItem onClick={() => doAction('mute')}>{group.muted ? '解除静默' : '静默该群'}</MenuItem>
                <MenuItem onClick={() => doAction('reset')}>重置上下文</MenuItem>
                <MenuItem onClick={() => doAction('refresh')}>刷新消息</MenuItem>
              </div>
            </>
          )}
        </div>
      </div>

      {/* 消息区 */}
      <div ref={listRef} onScroll={handleScroll} className="qq-scroll min-h-0 flex-1 overflow-y-auto bg-qq-bg py-4">
        {loading ? (
          <div className="flex h-full items-center justify-center text-sm text-qq-sub">加载中…</div>
        ) : messages.length === 0 && !typing ? (
          <div className="flex h-full flex-col items-center justify-center gap-2 text-center text-qq-sub">
            <div className="text-4xl">☁️</div>
            <div className="text-sm">{isSim ? '在下方输入消息，开始和「雨」试聊吧' : '该群暂无消息记录'}</div>
            {isSim && <div className="max-w-xs text-xs leading-5">消息会进入真实的处理链（风控 → 历史 → 随机互动 → LLM），机器人回复只显示在网页。</div>}
          </div>
        ) : (
          messageNodes
        )}
        {typing && (
          <div className="mb-3 flex flex-row-reverse items-center gap-2.5 px-4">
            <Avatar sender={BOT_SENDER} name="雨" size={38} isBot />
            <div className="flex items-center gap-1.5 rounded-xl rounded-tr-[4px] border border-[#b8defa] bg-qq-self px-3.5 py-2.5 text-qq-sub">
              <span className="text-xs">雨 正在输入</span>
              <span className="flex gap-0.5">
                {[0, 1, 2].map((i) => (
                  <span
                    key={i}
                    className="h-1.5 w-1.5 animate-bounce rounded-full bg-qq-blue"
                    style={{ animationDelay: `${i * 0.15}s` }}
                  />
                ))}
              </span>
            </div>
          </div>
        )}
      </div>

      {/* 试聊控制条（移动端自动换行） */}
      {canSim ? (
        <div className="qq-scroll flex shrink-0 flex-wrap items-center gap-x-2 gap-y-1.5 overflow-x-auto border-t border-qq-border bg-[#fafbfc] px-3 py-1.5 text-xs md:px-4">
          <span className="shrink-0 text-qq-sub">模拟身份</span>
          <div className="flex shrink-0 items-center gap-1.5">
            {SIM_MEMBERS.map((m, i) => (
              <button
                key={m.id}
                onClick={() => setMemberIdx(i)}
                className={`flex items-center gap-1.5 rounded-full border px-2 py-0.5 transition-colors ${
                  memberIdx === i ? 'border-qq-blue bg-[#e6f4ff] text-qq-blue-deep' : 'border-qq-border bg-white text-qq-text hover:border-qq-blue/50'
                }`}
              >
                <Avatar sender={m.id} name={m.name} size={16} />
                {m.name}
              </button>
            ))}
          </div>
          <label className="ml-auto flex cursor-pointer items-center gap-1">
            <input type="checkbox" checked={isAt} onChange={(e) => setIsAt(e.target.checked)} className="accent-qq-blue" />
            <span className={isAt ? 'text-qq-blue-deep' : 'text-qq-sub'}>@机器人</span>
          </label>
          <label className="flex cursor-pointer items-center gap-1">
            <input type="checkbox" checked={isAdmin} onChange={(e) => setIsAdmin(e.target.checked)} className="accent-qq-blue" />
            <span className={isAdmin ? 'text-qq-blue-deep' : 'text-qq-sub'}>管理员</span>
          </label>
        </div>
      ) : (
        <div className="flex shrink-0 items-center gap-2 border-t border-qq-border bg-[#fffbe6] px-4 py-2 text-xs text-[#ad6800]">
          <IconRobot size={15} />
          <span>开启「试聊拦截」后，可以在网页里以群友身份注入消息试聊（机器人回复仅显示在网页，不发送到 QQ）。</span>
          <button
            className="ml-auto rounded bg-qq-blue px-2.5 py-1 text-white hover:bg-qq-blue-deep"
            onClick={() => doAction('sim')}
          >
            开启试聊拦截
          </button>
        </div>
      )}

      {/* 输入区 */}
      <div className="relative shrink-0 border-t border-qq-border bg-white">
        <div className="flex items-center gap-1 px-3 pt-1.5">
          <button
            className={`rounded p-1.5 ${emojiOpen ? 'bg-qq-hover text-qq-blue' : 'text-qq-sub hover:bg-qq-hover hover:text-qq-text'}`}
            onClick={() => setEmojiOpen((v) => !v)}
            title="表情"
          >
            <IconSmile />
          </button>
          {!canSim && (
            <span className="ml-1 text-[11px] text-qq-sub">只读视图（未开启试聊拦截）</span>
          )}
        </div>
        {emojiOpen && (
          <div className="absolute bottom-full left-3 z-20 mb-1 grid w-[264px] grid-cols-8 gap-0.5 rounded-lg border border-qq-border bg-white p-2 shadow-lg">
            {EMOJIS.map((em) => (
              <button
                key={em}
                className="rounded p-1 text-lg hover:bg-qq-hover"
                onClick={() => {
                  setInput((v) => v + em);
                }}
              >
                {em}
              </button>
            ))}
            <button className="col-span-8 mt-1 rounded p-0.5 text-center text-xs text-qq-sub hover:bg-qq-hover" onClick={() => setEmojiOpen(false)}>
              收起 <IconX size={10} />
            </button>
          </div>
        )}
        <textarea
          value={input}
          onChange={(e) => setInput(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter' && !e.shiftKey) {
              e.preventDefault();
              send();
            }
          }}
          disabled={!canSim}
          rows={3}
          placeholder={
            canSim
              ? isAt
                ? `以「${SIM_MEMBERS[memberIdx].name}」身份 @雨 发送…（Enter 发送，Shift+Enter 换行）`
                : `以「${SIM_MEMBERS[memberIdx].name}」身份发送…（Enter 发送，Shift+Enter 换行）`
              : '未开启试聊拦截，无法发送'
          }
          className="qq-scroll w-full resize-none bg-transparent px-4 py-2 text-[14px] leading-5 outline-none placeholder:text-qq-sub disabled:opacity-50"
        />
        <div className="flex items-center justify-end px-4 pb-3">
          <button
            onClick={send}
            disabled={!canSim || !input.trim() || sending}
            className="flex items-center gap-1.5 rounded-md bg-qq-blue px-4 py-1.5 text-[13px] text-white shadow-sm transition-colors hover:bg-qq-blue-deep disabled:cursor-not-allowed disabled:opacity-40"
          >
            {sending ? '发送中…' : '发送'}
            {!sending && <IconSend size={14} />}
          </button>
        </div>
      </div>
    </div>
  );
}

function MenuItem({ children, onClick }) {
  return (
    <button className="block w-full px-3.5 py-2 text-left text-[13px] hover:bg-qq-hover" onClick={onClick}>
      {children}
    </button>
  );
}
