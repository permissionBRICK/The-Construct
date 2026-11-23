import type { Scheme } from '../backend/types';

/* Design tokens. Every colour in the CSS comes from these. Each scheme has a light and a dark set; the
   sidebar stays dark in the light theme. Type and radii are shared. */

export type Mode = 'light' | 'dark';
export interface Tokens {
  bg: string; pn: string; pn2: string; sel: string; ln: string; fg: string; dim: string;
  pri: string; prifg: string; soft: string;
  ok: string; warn: string; warnbg: string; bad: string; badbg: string; off: string;
  side: string; sidefg: string; sidedim: string; sidesel: string; sidepri: string; sideprifg: string;
}

export const TYPE = {
  font: '"IBM Plex Sans", system-ui, -apple-system, "Segoe UI", sans-serif',
  mono: '"IBM Plex Mono", ui-monospace, Consolas, monospace',
  fs: '13.5px',
  rad: '6px',
  radc: '8px',
};

export const SCHEMES: Record<Scheme, { label: string; light: Tokens; dark: Tokens }> = {
  green: {
    label: 'Green',
    light: {
      bg: '#F3F6F2', pn: '#FFFFFF', pn2: '#F5F9F5', sel: '#D6E4DA', ln: '#D2DED5', fg: '#0E2117', dim: '#5B7164', pri: '#0A8748', prifg: '#FFFFFF', soft: '#DCEFE3',
      ok: '#0A8748', warn: '#8F5B00', warnbg: '#FBF1DA', bad: '#B4281F', badbg: '#FBE9E6', off: '#9BAAA1',
      side: '#0B1610', sidefg: '#D8F3E3', sidedim: '#6F9882', sidesel: '#12281C', sidepri: '#1FDD7C', sideprifg: '#03130A',
    },
    dark: {
      bg: '#060D09', pn: '#0B1610', pn2: '#0E1C14', sel: '#12281C', ln: '#1D3728', fg: '#D8F3E3', dim: '#6F9882', pri: '#1FDD7C', prifg: '#03130A', soft: '#10301F',
      ok: '#1FDD7C', warn: '#F2B950', warnbg: '#241C0A', bad: '#FF7A6B', badbg: '#29120F', off: '#47604F',
      side: '#040906', sidefg: '#D8F3E3', sidedim: '#6F9882', sidesel: '#12281C', sidepri: '#1FDD7C', sideprifg: '#03130A',
    },
  },
  blue: {
    label: 'Blue',
    light: {
      bg: '#F3F3F3', pn: '#FFFFFF', pn2: '#F9F9F9', sel: '#EAEAEA', ln: '#E2E2E2', fg: '#1B1B1B', dim: '#616161', pri: '#005FB8', prifg: '#FFFFFF', soft: '#E0EEF9',
      ok: '#0F7B0F', warn: '#9D5D00', warnbg: '#FFF4CE', bad: '#C42B1C', badbg: '#FDE7E9', off: '#8A8A8A',
      side: '#1C2430', sidefg: '#E6ECF3', sidedim: '#8C9AAB', sidesel: '#2A3646', sidepri: '#60CDFF', sideprifg: '#00131F',
    },
    dark: {
      bg: '#202020', pn: '#2B2B2B', pn2: '#272727', sel: '#2D2D2D', ln: '#383838', fg: '#FFFFFF', dim: '#C5C5C5', pri: '#60CDFF', prifg: '#000000', soft: '#1C3346',
      ok: '#6CCB5F', warn: '#FCE100', warnbg: '#433519', bad: '#FF99A4', badbg: '#442726', off: '#8A8A8A',
      side: '#171717', sidefg: '#F2F2F2', sidedim: '#9A9A9A', sidesel: '#2D2D2D', sidepri: '#60CDFF', sideprifg: '#000000',
    },
  },
  copper: {
    label: 'Copper',
    light: {
      bg: '#F1F2F3', pn: '#FFFFFF', pn2: '#F7F8F9', sel: '#E4E8EC', ln: '#DCE0E4', fg: '#171B1F', dim: '#5D6872', pri: '#B5581E', prifg: '#FFFFFF', soft: '#F8E8DC',
      ok: '#23864A', warn: '#7F5C00', warnbg: '#FBF2D4', bad: '#BE3426', badbg: '#FBE8E5', off: '#A3ACB4',
      side: '#1F252B', sidefg: '#E4E8EC', sidedim: '#8A96A1', sidesel: '#2E363F', sidepri: '#EE9358', sideprifg: '#1A0E06',
    },
    dark: {
      bg: '#171B1F', pn: '#1E2329', pn2: '#22282E', sel: '#262D34', ln: '#2D343B', fg: '#E6E9EC', dim: '#98A3AD', pri: '#EE9358', prifg: '#1A0E06', soft: '#3A2617',
      ok: '#5CD38A', warn: '#E7C04B', warnbg: '#322810', bad: '#FF8270', badbg: '#3A1B17', off: '#56616B',
      side: '#111417', sidefg: '#E4E8EC', sidedim: '#7F8B96', sidesel: '#262D34', sidepri: '#EE9358', sideprifg: '#1A0E06',
    },
  },
  mono: {
    label: 'Black and white',
    light: {
      bg: '#FFFFFF', pn: '#FFFFFF', pn2: '#F8F8F8', sel: '#EDEDED', ln: '#E4E4E4', fg: '#111111', dim: '#707070', pri: '#111111', prifg: '#FFFFFF', soft: '#F0F0F0',
      ok: '#12893F', warn: '#8F5600', warnbg: '#FFF6E5', bad: '#C8261A', badbg: '#FDEDEB', off: '#BDBDBD',
      side: '#111111', sidefg: '#F2F2F2', sidedim: '#8E8E93', sidesel: '#262626', sidepri: '#FFFFFF', sideprifg: '#111111',
    },
    dark: {
      bg: '#0E0E0F', pn: '#161618', pn2: '#1B1B1D', sel: '#222225', ln: '#2A2A2D', fg: '#F2F2F2', dim: '#8E8E93', pri: '#F2F2F2', prifg: '#0E0E0F', soft: '#222225',
      ok: '#3DDC84', warn: '#F5B94A', warnbg: '#2A2010', bad: '#FF6B5C', badbg: '#2D1412', off: '#4A4A4E',
      side: '#080809', sidefg: '#F2F2F2', sidedim: '#8E8E93', sidesel: '#222225', sidepri: '#F2F2F2', sideprifg: '#0E0E0F',
    },
  },
};

export const SCHEME_ORDER: Scheme[] = ['green', 'blue', 'copper', 'mono'];

/** CSS custom properties for one scheme and mode. */
export function cssVars(scheme: Scheme, mode: Mode): Record<string, string> {
  const t = SCHEMES[scheme][mode];
  const out: Record<string, string> = {};
  for (const [k, v] of Object.entries({ ...t, ...TYPE })) out['--' + k] = v;
  return out;
}

/** Relative luminance contrast (WCAG) of two #RRGGBB colours. */
export function contrast(a: string, b: string): number {
  const lum = (hex: string) => {
    const c = [1, 3, 5].map(i => parseInt(hex.slice(i, i + 2), 16) / 255).map(v => (v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4));
    return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2];
  };
  const [x, y] = [lum(a), lum(b)].sort((p, q) => q - p);
  return (x + 0.05) / (y + 0.05);
}
