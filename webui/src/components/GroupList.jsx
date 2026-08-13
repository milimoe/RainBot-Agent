import { fmtAgo, groupName, shortId, SIM_GROUP } from '../lib/util.js';
import Avatar from './Avatar.jsx';
import { Badge } from './ui.jsx';
import { IconSearch } from './Icons.jsx';

/**
 * 聊天页左侧群列表（仿 NTQQ 会话列表）：
 * 试聊群置顶，条目含头像 / 群名 / 最后一条消息预览 / 时间 / 状态徽标。
 */
export default function GroupList({ groups, active, onSelect, filter, setFilter }) {
  return (
    <div className="flex h-full w-full flex-col bg-qq-panel">
      <div className="px-3 pb-2 pt-3">
        <div className="flex items-center gap-2 rounded-md bg-qq-bg px-2.5 py-1.5">
          <span className="text-qq-sub">
            <IconSearch size={14} />
          </span>
          <input
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
            placeholder="搜索群"
            className="w-full bg-transparent text-[13px] outline-none placeholder:text-qq-sub"
          />
        </div>
      </div>
      <div className="qq-scroll flex-1 overflow-y-auto pb-2">
        {groups.length === 0 && (
          <div className="px-6 py-10 text-center text-xs text-qq-sub">
            暂无群聊数据
            <div className="mt-1">机器人收到消息后会出现在这里</div>
          </div>
        )}
        {groups.map((g) => {
          const isSim = g.group === SIM_GROUP;
          const activeCls = active === g.group ? 'bg-qq-active' : 'hover:bg-qq-hover';
          const previewName = g.previewIsBot ? '雨' : g.previewSender ? shortId(g.previewSender) : '';
          return (
            <div
              key={g.group}
              onClick={() => onSelect(g.group)}
              className={`flex cursor-pointer items-center gap-2.5 px-3 py-2.5 transition-colors ${activeCls}`}
            >
              <div className="relative">
                <Avatar sender={g.group} name={groupName(g.group)} size={40} isBot={isSim} />
              </div>
              <div className="min-w-0 flex-1">
                <div className="flex items-center gap-1.5">
                  <span className="truncate text-[13.5px] font-medium">{groupName(g.group)}</span>
                  {isSim && <Badge tone="blue">试聊</Badge>}
                  {!isSim && g.simEnabled && <Badge tone="orange">拦截</Badge>}
                  {g.muted && <Badge tone="gray">静默</Badge>}
                  {g.degraded && <Badge tone="red">降级</Badge>}
                </div>
                <div className="mt-0.5 flex items-center gap-2">
                  <span className="min-w-0 flex-1 truncate text-xs text-qq-sub">
                    {g.preview ? (
                      <>
                        {previewName && <span className="text-qq-sub/80">{previewName}：</span>}
                        {g.preview.replace(/^\r?\n/, '').replace(/<@!?[^>]+>/g, '@…')}
                      </>
                    ) : (
                      <span className="italic opacity-70">还没有消息</span>
                    )}
                  </span>
                  <span className="shrink-0 text-[10px] text-qq-sub/70">{fmtAgo(g.previewTime || g.lastMessageUtc)}</span>
                </div>
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
}
