import type { SVGProps } from "react";

const base = (p: SVGProps<SVGSVGElement>) => ({
  width: 16, height: 16, viewBox: "0 0 24 24", fill: "none", stroke: "currentColor", strokeWidth: 2,
  strokeLinecap: "round" as const, strokeLinejoin: "round" as const, "aria-hidden": true, ...p,
});

export const CatLogo = (p: SVGProps<SVGSVGElement>) => (
  <svg {...base(p)}><path d="M5 9 L5 3 L10 7 H14 L19 3 L19 9 C19 15 16 19 12 19 C8 19 5 15 5 9 Z" /><circle cx="9.5" cy="11.5" r="0.6" fill="currentColor" /><circle cx="14.5" cy="11.5" r="0.6" fill="currentColor" /></svg>
);
export const IconGrid = (p: SVGProps<SVGSVGElement>) => (<svg {...base(p)}><rect x="4" y="4" width="7" height="7" rx="2" /><rect x="13" y="4" width="7" height="7" rx="2" /><rect x="4" y="13" width="7" height="7" rx="2" /><rect x="13" y="13" width="7" height="7" rx="2" /></svg>);
export const IconJobs = (p: SVGProps<SVGSVGElement>) => (<svg {...base(p)}><circle cx="12" cy="12" r="8" /><path d="M12 7v5l3 2" /></svg>);
export const IconFleet = (p: SVGProps<SVGSVGElement>) => (<svg {...base(p)}><rect x="3" y="4" width="18" height="6" rx="2" /><rect x="3" y="14" width="18" height="6" rx="2" /><path d="M7 7h.01M7 17h.01" /></svg>);
export const IconSearch = (p: SVGProps<SVGSVGElement>) => (<svg {...base(p)}><circle cx="11" cy="11" r="7" /><path d="M20 20l-4-4" /></svg>);
export const IconBack = (p: SVGProps<SVGSVGElement>) => (<svg {...base(p)}><path d="M15 6l-6 6 6 6" /></svg>);
export const IconEdit = (p: SVGProps<SVGSVGElement>) => (<svg {...base(p)}><path d="M4 20h4L19 9l-4-4L4 16z" /></svg>);
export const IconTrash = (p: SVGProps<SVGSVGElement>) => (<svg {...base(p)}><path d="M5 7h14M10 7V4h4v3M7 7l1 13h8l1-13" /></svg>);
export const IconCopy = (p: SVGProps<SVGSVGElement>) => (<svg {...base(p)}><rect x="8" y="8" width="12" height="12" rx="2" /><path d="M16 8V5a1 1 0 0 0-1-1H5a1 1 0 0 0-1 1v10a1 1 0 0 0 1 1h3" /></svg>);
export const IconRules = (p: SVGProps<SVGSVGElement>) => (<svg {...base(p)}><path d="M4 6h16M4 12h10M4 18h6" /></svg>);
export const IconClose = (p: SVGProps<SVGSVGElement>) => (<svg {...base(p)}><path d="M6 6l12 12M18 6L6 18" /></svg>);
export const IconChevron = (p: SVGProps<SVGSVGElement>) => (<svg {...base(p)}><path d="M9 6l6 6-6 6" /></svg>);
export const IconUp = (p: SVGProps<SVGSVGElement>) => (<svg {...base(p)}><path d="M6 15l6-6 6 6" /></svg>);
export const IconDown = (p: SVGProps<SVGSVGElement>) => (<svg {...base(p)}><path d="M6 9l6 6 6-6" /></svg>);
export const IconPlus = (p: SVGProps<SVGSVGElement>) => (<svg {...base(p)}><path d="M12 5v14M5 12h14" /></svg>);
export const IconPanel = (p: SVGProps<SVGSVGElement>) => (<svg {...base(p)}><rect x="3" y="4" width="18" height="16" rx="2" /><path d="M9 4v16" /></svg>);
