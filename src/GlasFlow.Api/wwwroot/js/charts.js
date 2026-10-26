// Minimal SVG charts: vertical bars with optional line overlay, horizontal share bars.
import { esc, num } from './ui.js';

function niceMax(v) {
  if (v <= 0) return 1;
  const exp = Math.pow(10, Math.floor(Math.log10(v)));
  const f = v / exp;
  const nf = f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10;
  return nf * exp;
}

/**
 * Bar chart. data: [{ label, value, line?, tip? }]. Highlights the last bar (current period).
 * opts: { height, format(v), lineLabel, ariaLabel }
 * Re-renders on container resize; returns a disposer.
 */
export function barChart(container, data, opts = {}) {
  const height = opts.height ?? 240;
  const fmt = opts.format ?? num;
  const hasLine = data.some((d) => d.line !== undefined);
  container.classList.add('chart');

  const draw = () => {
    const width = Math.max(280, container.clientWidth || 600);
    const pad = { top: 12, right: hasLine ? (width < 420 ? 24 : 36) : 8, bottom: 26, left: width < 420 ? 52 : 64 };
    const w = width - pad.left - pad.right;
    const h = height - pad.top - pad.bottom;
    const max = niceMax(Math.max(...data.map((d) => d.value), 0));
    const lineMax = hasLine ? niceMax(Math.max(...data.map((d) => d.line || 0), 1)) : 1;
    const step = w / Math.max(data.length, 1);
    const bw = Math.max(6, Math.min(44, step * 0.62));
    const ticks = 4;
    const showEvery = width < 420 ? 3 : width < 560 ? 2 : 1;

    let svg = `<svg viewBox="0 0 ${width} ${height}" width="${width}" height="${height}" role="img" aria-label="${esc(opts.ariaLabel || 'Gráfica')}">`;
    for (let i = 0; i <= ticks; i++) {
      const y = pad.top + h - (h * i) / ticks;
      svg += `<line class="gridline" x1="${pad.left}" x2="${pad.left + w}" y1="${y}" y2="${y}"/>`;
      svg += `<text x="${pad.left - 8}" y="${y + 4}" text-anchor="end">${esc(opts.axisFormat ? opts.axisFormat((max * i) / ticks) : fmt((max * i) / ticks))}</text>`;
      if (hasLine) svg += `<text x="${pad.left + w + 8}" y="${y + 4}" text-anchor="start">${num((lineMax * i) / ticks)}</text>`;
    }
    data.forEach((d, i) => {
      const x = pad.left + step * i + (step - bw) / 2;
      const bh = max ? (h * d.value) / max : 0;
      const y = pad.top + h - bh;
      const current = i === data.length - 1;
      svg += `<rect class="bar-hit" data-i="${i}" x="${pad.left + step * i}" y="${pad.top}" width="${step}" height="${h}" fill="transparent"/>`;
      svg += `<rect class="bar${current ? ' current' : ''}" data-i="${i}" x="${x}" y="${y}" width="${bw}" height="${Math.max(bh, 0)}" rx="2"><title>${esc(d.tip || `${d.label}: ${fmt(d.value)}`)}</title></rect>`;
      if (i % showEvery === 0 || current) svg += `<text x="${x + bw / 2}" y="${height - 8}" text-anchor="middle">${esc(d.label)}</text>`;
    });
    if (hasLine) {
      const pts = data.map((d, i) => [pad.left + step * i + step / 2, pad.top + h - (h * (d.line || 0)) / lineMax]);
      svg += `<polyline class="line" points="${pts.map((p) => p.join(',')).join(' ')}"/>`;
      svg += pts.map(([x, y]) => `<circle class="dot" cx="${x}" cy="${y}" r="3"/>`).join('');
    }
    svg += '</svg>';
    container.innerHTML = svg + '<div class="chart-tip" hidden></div>';

    const tip = container.querySelector('.chart-tip');
    const svgEl = container.querySelector('svg');
    svgEl.addEventListener('mousemove', (e) => {
      const t = e.target.closest('[data-i]');
      if (!t) { tip.hidden = true; return; }
      const d = data[Number(t.dataset.i)];
      const box = container.getBoundingClientRect();
      tip.innerHTML = d.tip ? esc(d.tip).replace(/\n/g, '<br>') : `${esc(d.label)}: ${esc(fmt(d.value))}`;
      tip.style.left = `${Math.min(Math.max(e.clientX - box.left, 60), box.width - 60)}px`;
      tip.style.top = `${e.clientY - box.top - 6}px`;
      tip.hidden = false;
    });
    svgEl.addEventListener('mouseleave', () => { tip.hidden = true; });
  };

  draw();
  let last = container.clientWidth;
  const ro = new ResizeObserver(() => {
    if (Math.abs(container.clientWidth - last) > 4) { last = container.clientWidth; draw(); }
  });
  ro.observe(container);
  return () => ro.disconnect();
}

/** Horizontal share bars. data: [{ label, value }] */
export function shareBars(data, fmt = num) {
  const total = data.reduce((s, d) => s + d.value, 0) || 1;
  const max = Math.max(...data.map((d) => d.value), 1);
  return `<div class="hbars">${data.map((d) => `
    <div class="hbar-row">
      <span class="lbl">${esc(d.label)}</span>
      <span class="val">${esc(fmt(d.value))}<span class="share">${num((d.value / total) * 100)}%</span></span>
      <div class="track" aria-hidden="true"><span style="width:${(d.value / max) * 100}%"></span></div>
    </div>`).join('')}</div>`;
}
