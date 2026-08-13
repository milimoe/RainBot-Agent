import { BOT_SENDER, displayNameOf, fmtFull, shortId } from '../lib/util.js';
import Avatar from './Avatar.jsx';

/**
 * 把 <@openid>（新格式）或 <@!openid>（历史格式）富文本标签渲染为蓝色 @提及。
 */
export function renderContent(content, botOpenId) {
  const text = (content || '').replace(/^\r?\n/, '');
  const parts = text.split(/(<@!?[^>]+>)/g);
  return parts.map((part, i) => {
    const m = part.match(/^<@!?(.+)>$/);
    if (m) {
      const isBot = botOpenId && m[1] === botOpenId;
      return (
        <span key={i} className="mention">
          {isBot ? '@机器人' : `@${shortId(m[1])}`}
        </span>
      );
    }
    return <span key={i}>{part}</span>;
  });
}

/**
 * 一条聊天气泡（仿 NTQQ：对方白底描边靠左，机器人浅蓝靠右；
 * 群聊中对方气泡上方带彩色昵称 + 时间）。
 */
export default function MessageBubble({ msg, botOpenId, names }) {
  const isBot = msg.isBot || msg.sender === BOT_SENDER;
  const name = isBot ? '雨' : names?.[msg.sender] || displayNameOf(msg);
  const timeStr = fmtFull(msg.time);

  return (
    <div className={`flex w-full gap-2.5 px-4 ${isBot ? 'flex-row-reverse' : ''}`}>
      <Avatar sender={msg.sender} name={name} size={38} isBot={isBot} />
      <div className={`flex max-w-[68%] min-w-0 flex-col ${isBot ? 'items-end' : 'items-start'}`}>
        <div className={`mb-0.5 flex items-baseline gap-1.5 text-xs ${isBot ? 'flex-row-reverse' : ''}`}>
          <span className="font-medium" style={isBot ? { color: '#12b7f5' } : { color: '#576b95' }}>
            {name}
          </span>
          {!isBot && !!msg.sender && (
            <span className="max-w-24 truncate font-mono text-[10px] text-qq-sub/70" title={`OpenID：${msg.sender}`}>
              {shortId(msg.sender)}
            </span>
          )}
          <span className="text-[10px] text-qq-sub/70">{timeStr}</span>
        </div>
        <div
          className={`whitespace-pre-wrap break-words px-3 py-2 text-[14px] leading-[1.55] ${
            isBot ? 'bubble-out' : 'bubble-in'
          }`}
        >
          {renderContent(msg.content, botOpenId)}
        </div>
      </div>
    </div>
  );
}
