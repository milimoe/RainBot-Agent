import { useState } from 'react';
import { Badge, toast } from '../components/ui.jsx';
import { IconCopy } from '../components/Icons.jsx';

// 指令目录（与 CommandParser 实现一致）
const COMMAND_GROUPS = [
  {
    title: '🙋 所有人可用',
    desc: '普通群友 @机器人 发送即可',
    tone: 'blue',
    items: [
      {
        cmd: '/忘掉我',
        alias: '/forgetme',
        desc: '清除机器人记住的关于你的画像（兴趣、习惯等），机器人会"忘记"你',
      },
      {
        cmd: '/fun',
        alias: '/娱乐',
        desc: '查看随机互动（反驳/复读/OSM/反向艾特/叫哥）的开关与概率一览',
      },
    ],
  },
  {
    title: '👮 管理员指令',
    desc: '机器人自我维护的管理员列表（可在「设置 → 管理员」维护）',
    tone: 'orange',
    items: [
      {
        cmd: '/admin help',
        alias: '/admin 帮助 · 直接发 /admin',
        desc: '机器人回发管理员指令帮助',
      },
      {
        cmd: '/admin list',
        alias: '/admin ls · /admin 参数',
        desc: '列出全部可热改参数及当前值（参数名可在「配置」页直接复制）',
      },
      {
        cmd: '/admin set 参数 值',
        alias: '/admin 设置 参数 值',
        desc: '热改参数，即时生效并落库持久化',
        example: '/admin set Trigger.PassiveCooldownSeconds 60',
      },
      {
        cmd: '/admin mute [分钟]',
        alias: '/admin 静默 [分钟]',
        desc: '一键静默本群；带分钟数到时自动解除，不带则永久静默',
        example: '/admin mute 30',
      },
      {
        cmd: '/admin unmute',
        alias: '/admin 解除静默',
        desc: '解除本群静默',
      },
      {
        cmd: '/admin stats',
        alias: '/admin 统计',
        desc: '查看本群消息量与缓存命中率',
      },
      {
        cmd: '/context',
        desc: '查看当前会话最近一次模型请求的窗口概览：估算 tokens、治理水位占用、历史条数及 A–F 区块大小。仅管理员可用，不调用模型。',
      },
      {
        cmd: '/context full [页码]',
        desc: '分页查看最近请求的脱敏上下文全文，包括工具调用和结果；默认第 1 页。图片只显示占位。仅管理员可用。',
        example: '/context full 2',
      },
      {
        cmd: '/admin admin add|remove openid',
        alias: '',
        desc: '维护管理员列表（openid 可在聊天页群友昵称旁看到）',
        example: '/admin admin add u_xxxxxxxx',
      },
      {
        cmd: '/admin forget 短id',
        alias: '/admin 清除 短id',
        desc: '清除指定群友的画像（短 id = openid 前几位）',
      },
      {
        cmd: '/admin sayno list',
        alias: '',
        desc: '列出反驳不的全部词表（13 张表）',
      },
      {
        cmd: '/admin sayno 表名 add|remove 词',
        alias: '',
        desc: '增删反驳不词表词条，写回 sayno.json 即时生效',
        example: '/admin sayno saynowords add 才不{0}',
      },
    ],
  },
  {
    title: '📟 状态查询',
    desc: '',
    tone: 'gray',
    items: [
      {
        cmd: '/status',
        alias: '',
        desc: '查看本群运行状态：消息量、缓存命中率、管理员数、静默/降级状态（仅管理员）',
      },
    ],
  },
];

/**
 * 指令大全：分组卡片 + 一键复制（复制到剪贴板，回 QQ 粘贴即用）。
 */
export default function CommandsPage() {
  const [copied, setCopied] = useState(null);

  const copy = async (text) => {
    try {
      await navigator.clipboard.writeText(text);
      setCopied(text);
      toast(`已复制 ${text}`, 'success');
      setTimeout(() => setCopied(null), 2000);
    } catch {
      toast('复制失败，请手动长按复制', 'error');
    }
  };

  return (
    <div className="qq-scroll h-full min-w-0 flex-1 overflow-y-auto">
      <div className="mx-auto max-w-3xl space-y-5 px-4 py-6 md:px-6">
        <div>
          <h1 className="text-lg font-semibold">指令大全 📖</h1>
          <p className="mt-0.5 text-xs text-qq-sub">
            指令在群里<b>直接发送即可（无需 @）</b>，@ 发送同样有效；管理员指令需要管理员权限。点指令右侧复制按钮，回 QQ 粘贴即用。参数名与中文别名通用。
          </p>
        </div>

        {COMMAND_GROUPS.map((group) => (
          <div key={group.title} className="overflow-hidden rounded-xl border border-qq-border bg-white">
            <div className="flex items-center gap-2 border-b border-qq-border/70 bg-qq-bg/40 px-4 py-2.5">
              <span className="text-sm font-medium">{group.title}</span>
              {group.tone !== 'gray' && <Badge tone={group.tone}>{group.items.length} 条</Badge>}
              {group.desc && <span className="hidden text-xs text-qq-sub sm:inline">{group.desc}</span>}
            </div>
            <div>
              {group.items.map((item, idx) => (
                <div
                  key={item.cmd}
                  className={`flex flex-col gap-1.5 px-4 py-3 sm:flex-row sm:items-start sm:gap-3 ${
                    idx === group.items.length - 1 ? '' : 'border-b border-qq-border/60'
                  }`}
                >
                  <div className="min-w-0 flex-1">
                    <button
                      onClick={() => copy(item.cmd)}
                      title="点击复制"
                      className={`group inline-flex max-w-full items-center gap-1.5 rounded-md px-2 py-1 text-left font-mono text-[13px] transition-colors ${
                        copied === item.cmd ? 'bg-[#e8ffe9] text-qq-green' : 'bg-qq-bg hover:bg-qq-hover'
                      }`}
                    >
                      <span className="truncate">{item.cmd}</span>
                      <span className="shrink-0 text-qq-sub opacity-60 group-hover:opacity-100">
                        <IconCopy />
                      </span>
                    </button>
                    {item.alias && (
                      <div className="mt-0.5 text-[11px] text-qq-sub">
                        别名：<span className="font-mono">{item.alias}</span>
                      </div>
                    )}
                    <div className="mt-1 text-xs leading-5 text-qq-sub">{item.desc}</div>
                  </div>
                  {item.example && (
                    <button
                      onClick={() => copy(item.example)}
                      title="点击复制示例"
                      className={`group inline-flex shrink-0 items-center gap-1.5 self-start rounded-md border px-2 py-1 text-left font-mono text-[11.5px] transition-colors ${
                        copied === item.example
                          ? 'border-qq-green bg-[#e8ffe9] text-qq-green'
                          : 'border-qq-border bg-white text-qq-text hover:border-qq-blue'
                      }`}
                    >
                      <span className="max-w-[280px] truncate">{item.example}</span>
                      <span className="shrink-0 text-qq-sub opacity-60 group-hover:opacity-100">
                        <IconCopy />
                      </span>
                    </button>
                  )}
                </div>
              ))}
            </div>
          </div>
        ))}

        <div className="rounded-xl border border-[#ffe1b3] bg-[#fffaf0] p-4 text-xs leading-5 text-[#8c5a00]">
          💡 小贴士：
          <ul className="mt-1.5 list-inside list-disc space-y-1">
            <li><b>/admin 系列指令需要管理员权限</b>：先把你的 OpenID（聊天页群友昵称旁）加到「设置 → 管理员」，否则机器人会回复"这个指令只有管理员能用哦"。</li>
            <li>不记得参数名？去「配置」页点参数名下方的键名即可复制（如 <code className="rounded bg-white/80 px-1 font-mono">Trigger.PassiveCooldownSeconds</code>）。</li>
            <li>想试试指令效果，可以在「消息 → WebUI 试聊群」以管理员身份直接发送。</li>
            <li>SayNo 词表共有 13 张，表名大小写不敏感（saynowords / trigger / triggerbeforeno …），完整列表见「设置 → SayNo 反驳词表」。</li>
          </ul>
        </div>
      </div>
    </div>
  );
}
