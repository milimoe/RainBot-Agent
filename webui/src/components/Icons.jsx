// ---------- 内联 SVG 图标（无第三方图标库依赖） ----------

const base = {
  fill: 'none',
  stroke: 'currentColor',
  strokeWidth: 1.8,
  strokeLinecap: 'round',
  strokeLinejoin: 'round',
  viewBox: '0 0 24 24',
};

export function IconChat({ size = 22, ...p }) {
  return (
    <svg width={size} height={size} {...base} {...p}>
      <path d="M21 11.5a8.38 8.38 0 0 1-.9 3.8 8.5 8.5 0 0 1-7.6 4.7 8.38 8.38 0 0 1-3.8-.9L3 21l1.9-5.7a8.38 8.38 0 0 1-.9-3.8 8.5 8.5 0 0 1 4.7-7.6 8.38 8.38 0 0 1 3.8-.9h.5a8.48 8.48 0 0 1 8 8v.5z" />
    </svg>
  );
}

export function IconPerson({ size = 22, ...p }) {
  return (
    <svg width={size} height={size} {...base} {...p}>
      <circle cx="12" cy="8" r="4" />
      <path d="M4 21v-2a8 8 0 0 1 16 0v2" />
    </svg>
  );
}

export function IconSliders({ size = 22, ...p }) {
  return (
    <svg width={size} height={size} {...base} {...p}>
      <path d="M4 21v-7M4 10V3M12 21v-9M12 8V3M20 21v-5M20 12V3M1 14h6M9 8h6M17 16h6" />
    </svg>
  );
}

export function IconGear({ size = 22, ...p }) {
  return (
    <svg width={size} height={size} {...base} {...p}>
      <circle cx="12" cy="12" r="3" />
      <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z" />
    </svg>
  );
}

export function IconPulse({ size = 22, ...p }) {
  return (
    <svg width={size} height={size} {...base} {...p}>
      <path d="M22 12h-4l-3 9L9 3l-3 9H2" />
    </svg>
  );
}

export function IconSearch({ size = 16, ...p }) {
  return (
    <svg width={size} height={size} {...base} {...p}>
      <circle cx="11" cy="11" r="7" />
      <path d="m21 21-4.3-4.3" />
    </svg>
  );
}

export function IconMore({ size = 18, ...p }) {
  return (
    <svg width={size} height={size} {...base} {...p}>
      <circle cx="12" cy="5" r="1" fill="currentColor" stroke="none" />
      <circle cx="12" cy="12" r="1" fill="currentColor" stroke="none" />
      <circle cx="12" cy="19" r="1" fill="currentColor" stroke="none" />
    </svg>
  );
}

export function IconSend({ size = 18, ...p }) {
  return (
    <svg width={size} height={size} {...base} {...p}>
      <path d="m22 2-7 20-4-9-9-4z" />
      <path d="M22 2 11 13" />
    </svg>
  );
}

export function IconSmile({ size = 20, ...p }) {
  return (
    <svg width={size} height={size} {...base} {...p}>
      <circle cx="12" cy="12" r="9" />
      <path d="M8 14s1.5 2 4 2 4-2 4-2" />
      <path d="M9 9h.01M15 9h.01" />
    </svg>
  );
}

export function IconX({ size = 14, ...p }) {
  return (
    <svg width={size} height={size} {...base} {...p}>
      <path d="M18 6 6 18M6 6l12 12" />
    </svg>
  );
}

export function IconPlus({ size = 14, ...p }) {
  return (
    <svg width={size} height={size} {...base} {...p}>
      <path d="M12 5v14M5 12h14" />
    </svg>
  );
}

export function IconLock({ size = 16, ...p }) {
  return (
    <svg width={size} height={size} {...base} {...p}>
      <rect x="4" y="11" width="16" height="10" rx="2" />
      <path d="M8 11V7a4 4 0 0 1 8 0v4" />
    </svg>
  );
}

export function IconRefresh({ size = 15, ...p }) {
  return (
    <svg width={size} height={size} {...base} {...p}>
      <path d="M21 12a9 9 0 1 1-2.6-6.3" />
      <path d="M21 3v6h-6" />
    </svg>
  );
}

export function IconRobot({ size = 20, ...p }) {
  return (
    <svg width={size} height={size} {...base} {...p}>
      <rect x="4" y="8" width="16" height="12" rx="2" />
      <path d="M12 8V4M8 4h8" />
      <circle cx="9" cy="13" r="1" fill="currentColor" stroke="none" />
      <circle cx="15" cy="13" r="1" fill="currentColor" stroke="none" />
      <path d="M9 17h6" />
    </svg>
  );
}

export function IconLog({ size = 22, ...p }) {
  return (
    <svg width={size} height={size} {...base} {...p}>
      <rect x="3" y="4" width="18" height="16" rx="2" />
      <path d="M7 9h6M7 13h6M7 17h6M15 9l2 2 2-2" />
    </svg>
  );
}

export function IconBack({ size = 18, ...p }) {
  return (
    <svg width={size} height={size} {...base} {...p}>
      <path d="M15 18l-6-6 6-6" />
    </svg>
  );
}

export function IconPause({ size = 14, ...p }) {
  return (
    <svg width={size} height={size} {...base} {...p}>
      <path d="M9 5v14M15 5v14" />
    </svg>
  );
}

export function IconPlay({ size = 14, ...p }) {
  return (
    <svg width={size} height={size} {...base} {...p}>
      <path d="M7 5l11 7-11 7z" />
    </svg>
  );
}

export function IconBook({ size = 22, ...p }) {
  return (
    <svg width={size} height={size} {...base} {...p}>
      <path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20" />
      <path d="M6.5 2H20v20H6.5A2.5 2.5 0 0 1 4 19.5v-15A2.5 2.5 0 0 1 6.5 2z" />
    </svg>
  );
}

export function IconCopy({ size = 13, ...p }) {
  return (
    <svg width={size} height={size} {...base} {...p}>
      <rect x="9" y="9" width="12" height="12" rx="2" />
      <path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1" />
    </svg>
  );
}
