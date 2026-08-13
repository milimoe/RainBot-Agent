import { colorOf } from '../lib/util.js';
import { BOT_SENDER } from '../lib/util.js';

/**
 * QQ 风格圆角方形头像：机器人显示 🌧️，群友按名字哈希着色 + 首字。
 */
export default function Avatar({ sender, name, size = 40, isBot = false }) {
  const s = Math.round(size);
  if (isBot || sender === BOT_SENDER) {
    return (
      <div
        className="flex shrink-0 select-none items-center justify-center rounded-[8px] text-white"
        style={{
          width: s,
          height: s,
          fontSize: s * 0.52,
          background: 'linear-gradient(135deg, #36c7f7 0%, #12b7f5 55%, #0099ff 100%)',
        }}
        title="雨（机器人）"
      >
        🌧️
      </div>
    );
  }
  const color = colorOf(sender || name || '?');
  const ch = (name || '友').trim().charAt(0) || '友';
  return (
    <div
      className="flex shrink-0 select-none items-center justify-center rounded-[8px] font-medium text-white"
      style={{ width: s, height: s, fontSize: s * 0.45, background: `linear-gradient(135deg, ${color}cc, ${color})` }}
      title={name || sender}
    >
      {ch}
    </div>
  );
}
