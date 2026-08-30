import { useCallback, useEffect, useMemo, useState } from 'react';
import { api, AuthError } from '../lib/api.js';
import { groupName, SIM_GROUP, throttle } from '../lib/util.js';
import GroupList from '../components/GroupList.jsx';
import ChatWindow from '../components/ChatWindow.jsx';
import { toast } from '../components/ui.jsx';

/**
 * 聊天页：左侧群列表 + 右侧仿 NTQQ 聊天窗口。
 * 移动端（<768px）单栏展示：列表与聊天窗口互斥切换，聊天窗口带返回按钮。
 */
export default function ChatPage({ boot, onAuthFail }) {
  const [groups, setGroups] = useState([]);
  const [active, setActive] = useState(SIM_GROUP);
  const [filter, setFilter] = useState('');
  const [botFilter, setBotFilter] = useState('');
  const [isMobile, setIsMobile] = useState(() => window.matchMedia('(max-width: 767px)').matches);
  const [mobileShowChat, setMobileShowChat] = useState(false);

  useEffect(() => {
    const mq = window.matchMedia('(max-width: 767px)');
    const onChange = () => setIsMobile(mq.matches);
    mq.addEventListener('change', onChange);
    return () => mq.removeEventListener('change', onChange);
  }, []);

  const loadGroups = useCallback(async () => {
    try {
      const data = await api('/api/webui/groups');
      const list = data.groups || [];
      // 试聊群始终展示（即使还没有消息记录）
      if (!list.find((g) => g.group === SIM_GROUP)) {
        list.unshift({
          group: SIM_GROUP,
          isSim: true,
          simEnabled: true,
          muted: false,
          degraded: false,
          totalMessages: 0,
          preview: null,
          previewTime: null,
          previewIsBot: false,
        });
      }
      setGroups(list);
    } catch (e) {
      if (e instanceof AuthError) onAuthFail?.();
      else toast(e.message, 'error');
    }
  }, [onAuthFail]);

  useEffect(() => {
    loadGroups();
    const interval = setInterval(loadGroups, 30000);
    const onEvent = throttle(loadGroups, 1500);
    window.addEventListener('webui:event', onEvent);
    return () => {
      clearInterval(interval);
      window.removeEventListener('webui:event', onEvent);
    };
  }, [loadGroups]);

  const filtered = useMemo(() => {
    // 实例筛选：只看该实例的会话（试聊群始终保留）；空值 = 全部实例
    let list = botFilter ? groups.filter((g) => g.group === SIM_GROUP || (g.botId || 'unknown') === botFilter) : groups;
    const q = filter.trim();
    if (!q) return list;
    return list.filter(
      (g) =>
        groupName(g.group).includes(q) || g.group.includes(q) || (g.botName || '').includes(q) || (g.botId || '').includes(q)
    );
  }, [groups, filter, botFilter]);

  // 实例列表（基于全量会话，不随筛选变化；否则选中实例后下拉选项坍缩无法切回）
  const bots = useMemo(() => {
    const map = new Map();
    for (const g of groups) {
      if (g.group === SIM_GROUP) continue;
      const key = g.botId || 'unknown';
      if (!map.has(key)) map.set(key, { id: key, name: g.botName || key });
    }
    return [...map.values()].sort((a, b) => a.name.localeCompare(b.name, 'zh'));
  }, [groups]);

  const activeGroup = groups.find((g) => g.group === active) || null;
  const showList = !isMobile || !mobileShowChat;
  const showChat = !isMobile || mobileShowChat;

  return (
    <div className="flex h-full min-w-0 flex-1">
      {showList && (
        <div className={`${isMobile ? 'w-full' : 'w-[250px]'} shrink-0 border-r border-qq-border`}>
          <GroupList
            groups={filtered}
            active={active}
            onSelect={(g) => {
              setActive(g);
              if (isMobile) setMobileShowChat(true);
            }}
            filter={filter}
            setFilter={setFilter}
            bots={bots}
            botFilter={botFilter}
            setBotFilter={setBotFilter}
          />
        </div>
      )}
      {showChat && (
        <ChatWindow
          group={activeGroup}
          boot={boot}
          onGroupsChanged={loadGroups}
          onBack={isMobile ? () => setMobileShowChat(false) : undefined}
        />
      )}
    </div>
  );
}
