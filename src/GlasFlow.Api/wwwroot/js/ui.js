// Shared UI helpers: formatting, escaping, toasts, modals, forms, states, pagination.
import { icon } from './icons.js';

// ---------- Escaping & formatting ----------
export function esc(value) {
  return String(value ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

const moneyFmt = new Intl.NumberFormat('es-MX', { style: 'currency', currency: 'MXN', minimumFractionDigits: 2, maximumFractionDigits: 2 });
const moneyCompact = new Intl.NumberFormat('es-MX', { style: 'currency', currency: 'MXN', notation: 'compact', maximumFractionDigits: 1 });
const numFmt = new Intl.NumberFormat('es-MX', { maximumFractionDigits: 2 });
const intFmt = new Intl.NumberFormat('es-MX', { maximumFractionDigits: 0 });
const pctFmt = new Intl.NumberFormat('es-MX', { style: 'percent', maximumFractionDigits: 1 });
const dateFmt = new Intl.DateTimeFormat('es-MX', { day: '2-digit', month: 'short', year: 'numeric' });
const dateTimeFmt = new Intl.DateTimeFormat('es-MX', { day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' });
const timeFmt = new Intl.DateTimeFormat('es-MX', { hour: '2-digit', minute: '2-digit', second: '2-digit' });

export const money = (v) => moneyFmt.format(Number(v) || 0);
export const moneyShort = (v) => moneyCompact.format(Number(v) || 0);
export const num = (v) => numFmt.format(Number(v) || 0);
export const int = (v) => intFmt.format(Number(v) || 0);
export const pct = (v) => pctFmt.format(Number(v) || 0);

/** API timestamps are UTC; some come without an offset, so treat those as UTC too. */
export function parseDate(value) {
  if (!value) return null;
  if (value instanceof Date) return value;
  const s = String(value);
  const d = new Date(/[zZ]|[+-]\d\d:?\d\d$/.test(s) || s.length <= 10 ? s : s + 'Z');
  return isNaN(d) ? null : d;
}
export const fdate = (v) => { const d = parseDate(v); return d ? dateFmt.format(d) : '—'; };
/** Calendar dates (e.g. due dates) are stored as midnight; format the date part without a timezone shift. */
export const fday = (v) => {
  if (!v) return '—';
  const [y, m, d] = String(v).slice(0, 10).split('-').map(Number);
  return y ? dateFmt.format(new Date(y, m - 1, d)) : '—';
};
export const fdatetime =(v) => { const d = parseDate(v); return d ? dateTimeFmt.format(d) : '—'; };
export const ftime = (v) => { const d = parseDate(v); return d ? timeFmt.format(d) : '—'; };
export function relTime(v) {
  const d = parseDate(v);
  if (!d) return '—';
  const sec = Math.round((Date.now() - d.getTime()) / 1000);
  const rtf = new Intl.RelativeTimeFormat('es-MX', { numeric: 'auto' });
  if (Math.abs(sec) < 60) return 'hace un momento';
  const min = Math.round(sec / 60); if (Math.abs(min) < 60) return rtf.format(-min, 'minute');
  const h = Math.round(min / 60); if (Math.abs(h) < 24) return rtf.format(-h, 'hour');
  const days = Math.round(h / 24); if (Math.abs(days) < 30) return rtf.format(-days, 'day');
  return fdate(d);
}
/** yyyy-mm-dd in local time, for <input type="date">. */
export function isoDay(d = new Date()) {
  const z = new Date(d.getTime() - d.getTimezoneOffset() * 60000);
  return z.toISOString().slice(0, 10);
}

// ---------- Labels ----------
export const SALES_STATUS = { Draft: 'Borrador', Confirmed: 'Confirmado', Shipped: 'Embarcado', Cancelled: 'Cancelado' };
export const PROD_STATUS = { Planned: 'Planeada', InProgress: 'En proceso', Completed: 'Completada', Cancelled: 'Cancelada' };
export const MOVEMENT_TYPES = {
  Adjustment: 'Ajuste', Reservation: 'Reserva', ReservationRelease: 'Liberación de reserva', SaleShipment: 'Embarque de venta',
  ProductionReceipt: 'Entrada de producción', TransferOut: 'Transferencia (salida)', TransferIn: 'Transferencia (entrada)', Import: 'Importación'
};
export const TAX_ZONES = { General: 'General · IVA 16%', FronteraNorte: 'Frontera Norte · IVA 8%' };
export const TAX_RATES = { General: 0.16, FronteraNorte: 0.08 };
export const ROLES = { Admin: 'Administrador', Operator: 'Operador', Viewer: 'Consulta (solo lectura)' };
export const UNITS = { m2: 'm²', pza: 'pza', ml: 'm lineal', kg: 'kg' };

export function statusChip(status, kind = 'sales') {
  const labels = kind === 'sales' ? SALES_STATUS : PROD_STATUS;
  return `<span class="chip chip-${esc(String(status).toLowerCase())}">${esc(labels[status] || status)}</span>`;
}
export function stockChip(row) {
  const free = Number(row.quantityFree ?? row.totalAvailable ?? 0);
  if (!row.isLowStock) return '<span class="chip chip-ok">En nivel</span>';
  if (free <= 0) return '<span class="chip chip-danger">Agotado</span>';
  return '<span class="chip chip-warn">Bajo mínimo</span>';
}
export const unit = (u) => UNITS[u] || u;

// ---------- DOM ----------
export const $ = (sel, root = document) => root.querySelector(sel);
export const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];
export function html(strings, ...values) {
  return strings.reduce((out, s, i) => out + s + (i < values.length ? values[i] ?? '' : ''), '');
}

// ---------- Toasts ----------
export function toast(message, type = 'success', title) {
  const host = document.getElementById('toasts');
  if (!host) return;
  const titles = { success: 'Listo', error: 'No se pudo completar', warn: 'Atención', info: 'Información' };
  const el = document.createElement('div');
  el.className = `toast ${type}`;
  el.setAttribute('role', type === 'error' ? 'alert' : 'status');
  el.innerHTML = `<div class="t-body"><div class="t-title">${esc(title || titles[type])}</div><div class="t-msg">${esc(message)}</div></div>
    <button class="btn btn-ghost btn-icon btn-sm" type="button" aria-label="Cerrar notificación">${icon('x')}</button>`;
  el.querySelector('button').addEventListener('click', () => el.remove());
  host.appendChild(el);
  setTimeout(() => el.remove(), type === 'error' ? 8000 : 4500);
}
export function toastError(err, title) {
  toast(err?.message || String(err), 'error', title || err?.title);
}

// ---------- Modal ----------
let openModals = 0;
/**
 * Opens a modal. `onSubmit(form, ctx)` may throw an ApiError; field errors are rendered automatically.
 * Returns { el, close }.
 */
export function modal({ title, body, submitLabel = 'Guardar', cancelLabel = 'Cancelar', size = '', onSubmit, onOpen, danger = false, footer }) {
  const prevFocus = document.activeElement;
  const overlay = document.createElement('div');
  overlay.className = 'overlay';
  const id = 'm' + Math.random().toString(36).slice(2, 8);
  overlay.innerHTML = `<form class="modal ${size}" role="dialog" aria-modal="true" aria-labelledby="${id}" novalidate>
      <div class="modal-head"><h3 id="${id}">${esc(title)}</h3>
        <button type="button" class="btn btn-ghost btn-icon btn-sm" data-close aria-label="Cerrar">${icon('x')}</button></div>
      <div class="modal-body"><div class="form-alert" role="alert"></div>${body}</div>
      <div class="modal-foot">${footer ?? `<button type="button" class="btn" data-close>${esc(cancelLabel)}</button>
        ${onSubmit ? `<button type="submit" class="btn ${danger ? 'btn-danger-solid' : 'btn-primary'}">${esc(submitLabel)}</button>` : ''}`}</div>
    </form>`;
  const form = overlay.querySelector('form');
  document.body.appendChild(overlay);
  openModals++;
  document.body.style.overflow = 'hidden';

  let closed = false;
  const close = () => {
    if (closed) return;
    closed = true;
    overlay.remove();
    openModals = Math.max(0, openModals - 1);
    if (!openModals) document.body.style.overflow = '';
    document.removeEventListener('keydown', onKey, true);
    if (prevFocus && prevFocus.focus) prevFocus.focus();
  };
  const onKey = (e) => {
    if (e.key === 'Escape') { e.stopPropagation(); close(); }
    if (e.key === 'Tab') trapFocus(e, form);
  };
  document.addEventListener('keydown', onKey, true);
  overlay.addEventListener('mousedown', (e) => { if (e.target === overlay) close(); });
  $$('[data-close]', form).forEach((b) => b.addEventListener('click', close));

  form.addEventListener('submit', async (e) => {
    e.preventDefault();
    if (!onSubmit) return;
    clearErrors(form);
    const btn = form.querySelector('button[type=submit]');
    const label = btn.innerHTML;
    btn.disabled = true;
    btn.innerHTML = `<span class="spinner" aria-hidden="true"></span> ${esc(btn.textContent.trim())}`;
    try {
      const keepOpen = await onSubmit(form, { close });
      if (keepOpen !== true) close();
    } catch (err) {
      showErrors(form, err);
    } finally {
      if (!closed) { btn.disabled = false; btn.innerHTML = label; }
    }
  });
  const ctx = { el: form, close };
  onOpen?.(form, ctx);
  const first = form.querySelector('.modal-body input:not([type=hidden]):not([disabled]), .modal-body select, .modal-body textarea') || form.querySelector('button[type=submit]');
  setTimeout(() => first?.focus(), 30);
  return ctx;
}

function trapFocus(e, root) {
  const items = $$('a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])', root)
    .filter((x) => x.offsetParent !== null);
  if (!items.length) return;
  const first = items[0], last = items[items.length - 1];
  if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
  else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
}

/** Confirmation dialog. Resolves true when confirmed. If `action` is given it runs inside the dialog and errors are shown there. */
export function confirmDialog({ title, message, confirmLabel = 'Confirmar', danger = false, action, body = '' }) {
  return new Promise((resolve) => {
    let done = false;
    const m = modal({
      title, size: 'narrow', submitLabel: confirmLabel, danger,
      body: `<p class="confirm-msg">${message}</p>${body}`,
      onSubmit: async (form) => {
        if (action) await action(form);
        done = true;
        resolve(true);
      }
    });
    const observer = new MutationObserver(() => {
      if (!document.body.contains(m.el)) { observer.disconnect(); if (!done) resolve(false); }
    });
    observer.observe(document.body, { childList: true });
  });
}

// ---------- Forms ----------
/** Renders a labelled form field. type: text|number|email|date|select|textarea|checkbox */
export function field({ name, label, type = 'text', value = '', required = false, options = [], hint = '', span = false, attrs = '', placeholder = '' }) {
  const id = `f-${name}-${Math.random().toString(36).slice(2, 6)}`;
  const req = required ? ' required aria-required="true"' : '';
  const errId = `${id}-err`;
  const described = `aria-describedby="${errId}${hint ? ` ${id}-hint` : ''}"`;
  let control;
  if (type === 'select') {
    control = `<select class="input" id="${id}" name="${name}" ${req} ${described} ${attrs}>${options.map((o) => {
      const [v, l] = Array.isArray(o) ? o : [o, o];
      return `<option value="${esc(v)}"${String(v) === String(value ?? '') ? ' selected' : ''}>${esc(l)}</option>`;
    }).join('')}</select>`;
  } else if (type === 'textarea') {
    control = `<textarea class="input" id="${id}" name="${name}" ${req} ${described} placeholder="${esc(placeholder)}" ${attrs}>${esc(value)}</textarea>`;
  } else if (type === 'checkbox') {
    return `<div class="field ${span ? 'span-2' : ''}"><label class="check"><input type="checkbox" id="${id}" name="${name}" ${value ? 'checked' : ''} ${attrs}> ${esc(label)}</label>
      ${hint ? `<span class="hint" id="${id}-hint">${hint}</span>` : ''}<p class="field-error" id="${errId}" data-error-for="${name}"></p></div>`;
  } else {
    control = `<input class="input${type === 'number' ? ' num' : ''}" id="${id}" name="${name}" type="${type}" value="${esc(value)}" placeholder="${esc(placeholder)}" ${req} ${described} ${attrs}>`;
  }
  return `<div class="field ${span ? 'span-2' : ''}"><label for="${id}">${esc(label)}${required ? ' <span aria-hidden="true" class="muted">*</span>' : ''}</label>${control}
    ${hint ? `<span class="hint" id="${id}-hint">${hint}</span>` : ''}<p class="field-error" id="${errId}" data-error-for="${name}"></p></div>`;
}

export function formValues(form) {
  const out = {};
  for (const el of form.elements) {
    if (!el.name || el.disabled) continue;
    if (el.type === 'checkbox') out[el.name] = el.checked;
    else out[el.name] = el.value.trim();
  }
  return out;
}

export function clearErrors(form) {
  $$('[data-error-for]', form).forEach((e) => { e.textContent = ''; });
  $$('[aria-invalid]', form).forEach((e) => e.removeAttribute('aria-invalid'));
  const alert = $('.form-alert', form);
  if (alert) alert.textContent = '';
}

/** Client-side validation errors use the same shape as the server: { field: [msg] }. */
export class ValidationError extends Error {
  constructor(errors) { super('Revise los campos marcados.'); this.fieldErrors = errors; this.title = 'Datos no válidos'; }
}

/** Shows ApiError/ValidationError field errors next to inputs (server keys are PascalCase, inputs camelCase). */
export function showErrors(form, err) {
  const alert = $('.form-alert', form);
  const unmatched = [];
  let firstInvalid = null;
  if (err?.fieldErrors) {
    for (const [key, msgs] of Object.entries(err.fieldErrors)) {
      const k = key.replace(/^\$\./, '');
      const target = $$('[data-error-for]', form).find((e) => e.dataset.errorFor.toLowerCase() === k.toLowerCase());
      const text = (Array.isArray(msgs) ? msgs : [msgs]).join(' ');
      if (target) {
        target.textContent = text;
        const input = form.elements[target.dataset.errorFor];
        if (input && input.setAttribute) { input.setAttribute('aria-invalid', 'true'); firstInvalid ??= input; }
      } else unmatched.push(text);
    }
  }
  if (alert) {
    const parts = [err?.message || 'No se pudo guardar.'];
    if (unmatched.length) parts.push(unmatched.join(' '));
    alert.textContent = parts.join(' ');
  }
  (firstInvalid || alert)?.focus?.();
  if (!err?.fieldErrors && !(err instanceof ValidationError)) toastError(err);
}

export function validate(rules) {
  const errors = {};
  for (const [name, checks] of Object.entries(rules)) {
    for (const [ok, msg] of checks) if (!ok) { (errors[name] ||= []).push(msg); break; }
  }
  if (Object.keys(errors).length) throw new ValidationError(errors);
}

// ---------- States ----------
export function skeletonRows(cols, rows = 6) {
  return `<div class="card-body" aria-busy="true" aria-label="Cargando">${Array.from({ length: rows }, (_, i) =>
    `<div class="skeleton sk-line" style="width:${92 - (i % 3) * 14}%"></div>`).join('')}<span class="sr-only">Cargando…</span></div>`;
}
export function emptyState(title, message = '', iconName = 'inbox') {
  return `<div class="state">${icon(iconName)}<h4>${esc(title)}</h4>${message ? `<p>${message}</p>` : ''}</div>`;
}
export function errorState(err, retryId = '') {
  return `<div class="state error" role="alert">${icon('alert')}<h4>${esc(err?.title || 'Error al cargar')}</h4><p>${esc(err?.message || err)}</p>
    ${retryId ? `<button class="btn btn-sm" type="button" id="${retryId}">${icon('refresh')} Reintentar</button>` : ''}</div>`;
}

/** Loads data into a container with skeleton → content | error-with-retry. */
export async function load(container, loader, renderFn, { skeleton } = {}) {
  container.innerHTML = skeleton ?? skeletonRows(6);
  try {
    const data = await loader();
    if (!container.isConnected) return;
    container.innerHTML = renderFn(data);
    return data;
  } catch (err) {
    if (err?.status === 401) return;
    const rid = 'retry-' + Math.random().toString(36).slice(2, 7);
    container.innerHTML = errorState(err, rid);
    container.querySelector('#' + rid)?.addEventListener('click', () => load(container, loader, renderFn, { skeleton }));
  }
}

// ---------- Pagination ----------
export function pager(result, label = 'registros') {
  const { page, totalPages, totalCount, pageSize } = result;
  if (!totalCount) return '';
  const from = (page - 1) * pageSize + 1;
  const to = Math.min(page * pageSize, totalCount);
  return `<nav class="pager" aria-label="Paginación"><span>${int(from)}–${int(to)} de ${int(totalCount)} ${esc(label)}</span>
    <div class="pager-ctrls">
      <button class="btn btn-sm" type="button" data-page="${page - 1}" ${page <= 1 ? 'disabled' : ''} aria-label="Página anterior">${icon('chevronLeft')} Anterior</button>
      <span class="pg">Página ${int(page)} de ${int(Math.max(totalPages, 1))}</span>
      <button class="btn btn-sm" type="button" data-page="${page + 1}" ${page >= totalPages ? 'disabled' : ''} aria-label="Página siguiente">Siguiente ${icon('chevronRight')}</button>
    </div></nav>`;
}

export function progress(percent, done = false) {
  const p = Math.max(0, Math.min(100, Number(percent) || 0));
  return `<div class="progress-row"><div class="progress ${done || p >= 100 ? 'done' : ''}" role="progressbar" aria-valuemin="0" aria-valuemax="100" aria-valuenow="${p.toFixed(0)}" aria-label="Avance"><span style="width:${p}%"></span></div><span class="pct">${num(p)}%</span></div>`;
}

export function debounce(fn, ms = 300) {
  let t;
  return (...args) => { clearTimeout(t); t = setTimeout(() => fn(...args), ms); };
}

export function options(list, valueKey, labelFn, emptyLabel) {
  const opts = list.map((x) => [x[valueKey], labelFn(x)]);
  return emptyLabel !== undefined ? [['', emptyLabel], ...opts] : opts;
}
