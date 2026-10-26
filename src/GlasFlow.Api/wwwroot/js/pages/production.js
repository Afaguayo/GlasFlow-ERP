import { get, post, ref, invalidate } from '../api.js';
import { icon } from '../icons.js';
import { replaceQuery } from '../app.js';
import {
  esc, num, fday, fdatetime, isoDay, statusChip, progress, field, formValues, modal, confirmDialog, validate, toast, load, pager,
  emptyState, debounce, options, unit, PROD_STATUS, $, $$
} from '../ui.js';

export default async function production(root, { query, ctx }) {
  const s = { page: 1, status: query.status || '', search: '' };
  root.innerHTML = `
    <div class="page-head">
      <div><h2>Órdenes de producción</h2><p>Corte, templado y laminado. Las unidades registradas entran de inmediato al almacén de la orden.</p></div>
      <div class="page-actions">${ctx.canWrite ? `<button class="btn btn-primary" type="button" id="new-po">${icon('plus')} Nueva orden</button>` : ''}</div>
    </div>
    <section class="card">
      <form class="toolbar" role="search" aria-label="Filtrar órdenes" id="po-filters">
        <div class="field"><span class="field-label" id="pst-lbl">Estado</span>
          <div class="seg" role="group" aria-labelledby="pst-lbl">${[['', 'Todas'], ...Object.entries(PROD_STATUS)].map(([k, l]) =>
            `<button type="button" data-status="${k}" aria-pressed="${s.status === k}">${l}</button>`).join('')}</div></div>
        <div class="field grow"><label for="po-search">Buscar</label><div class="input-icon">${icon('search')}<input class="input" id="po-search" name="search" placeholder="Número de orden o nombre del producto"></div></div>
      </form>
      <div id="po-table"></div>
    </section>`;
  const f = $('#po-filters', root);
  const box = $('#po-table', root);
  f.addEventListener('submit', (e) => e.preventDefault());
  f.search.addEventListener('input', debounce(() => { s.search = f.search.value.trim(); s.page = 1; reload(); }, 300));
  $$('[data-status]', f).forEach((b) => b.addEventListener('click', () => {
    s.status = b.dataset.status; s.page = 1;
    $$('[data-status]', f).forEach((x) => x.setAttribute('aria-pressed', String(x === b)));
    replaceQuery({ status: s.status });
    reload();
  }));
  $('#new-po', root)?.addEventListener('click', createModal);

  let rows = [];
  function reload() {
    load(box, () => get('/api/production-orders', { page: s.page, pageSize: 25, status: s.status, search: s.search }), (r) => {
      rows = r.items;
      if (!r.items.length) return emptyState('Sin órdenes de producción', 'No hay órdenes con estos filtros.', 'factory');
      return `<div class="table-wrap"><table class="data"><thead><tr>
        <th>Orden</th><th style="min-width:200px">Producto</th><th>Almacén</th><th style="min-width:170px">Avance</th><th>Estado</th><th>Compromiso</th>${ctx.canWrite ? '<th><span class="sr-only">Acciones</span></th>' : ''}
      </tr></thead><tbody>${r.items.map((p) => {
        const late = p.dueDate && (p.status === 'Planned' || p.status === 'InProgress') && p.dueDate.slice(0, 10) < isoDay();
        return `<tr>
          <td><span class="mono cell-main">${esc(p.productionNumber)}</span><div class="cell-sub nowrap">Creada ${esc(fday(p.createdAt))}</div></td>
          <td><span class="mono">${esc(p.productCode)}</span><div class="cell-sub">${esc(p.productName)}</div></td>
          <td class="mono">${esc(p.warehouseCode)}</td>
          <td>${progress(p.progressPercent, p.status === 'Completed')}<div class="cell-sub nowrap">${num(p.quantityCompleted)} de ${num(p.quantityPlanned)} ${esc(unit(p.unit))}</div></td>
          <td>${statusChip(p.status, 'prod')}</td>
          <td class="nowrap">${esc(fday(p.dueDate))}${late ? ' <span class="chip chip-danger plain">Vencida</span>' : ''}
            ${p.startDate ? `<div class="cell-sub">Inicio ${esc(fday(p.startDate))}</div>` : ''}${p.completionDate ? `<div class="cell-sub">Fin ${esc(fday(p.completionDate))}</div>` : ''}</td>
          ${ctx.canWrite ? `<td><div class="row-actions">${actionsFor(p)}</div></td>` : ''}
        </tr>`;
      }).join('')}</tbody></table></div>${pager(r, 'órdenes')}`;
    }).then((r) => {
      if (!r) return;
      $$('[data-page]', box).forEach((b) => b.addEventListener('click', () => { s.page = Number(b.dataset.page); reload(); }));
      $$('[data-act]', box).forEach((b) => b.addEventListener('click', () => act(b.dataset.act, rows.find((x) => x.id == b.dataset.id))));
    });
  }

  function actionsFor(p) {
    const b = (a, ic, label, full, cls = '') => `<button class="btn btn-sm ${cls}" type="button" data-act="${a}" data-id="${p.id}" title="${full}" aria-label="${full} ${esc(p.productionNumber)}">${icon(ic)}${label ? ' ' + label : ''}</button>`;
    const cancel = b('cancel', 'ban', '', 'Cancelar orden', 'btn-danger btn-icon');
    if (p.status === 'Planned') return b('start', 'play', 'Iniciar', 'Iniciar producción') + cancel;
    if (p.status === 'InProgress') return b('register', 'plus', 'Registrar', 'Registrar unidades') + b('complete', 'flag', 'Completar', 'Completar orden') + cancel;
    return '<span class="muted small">—</span>';
  }

  const done = (msg) => { toast(msg); invalidate('products'); ctx.refreshNotifications(); reload(); };

  function act(a, p) {
    const remaining = Math.max(0, p.quantityPlanned - p.quantityCompleted);
    if (a === 'start') {
      confirmDialog({
        title: 'Iniciar producción', confirmLabel: 'Iniciar',
        message: `¿Iniciar la orden <strong>${esc(p.productionNumber)}</strong>: ${num(p.quantityPlanned)} ${esc(unit(p.unit))} de ${esc(p.productName)} en ${esc(p.warehouseCode)}?`,
        action: async () => { const r = await post(`/api/production-orders/${p.id}/start`); done(`Orden ${r.productionNumber} en proceso.`); }
      });
    } else if (a === 'cancel') {
      confirmDialog({
        title: 'Cancelar orden', confirmLabel: 'Cancelar orden', danger: true,
        message: `¿Cancelar <strong>${esc(p.productionNumber)}</strong>?${p.quantityCompleted ? ` Las ${num(p.quantityCompleted)} unidades ya registradas permanecen en inventario.` : ''} Esta acción no se puede deshacer.`,
        action: async () => { const r = await post(`/api/production-orders/${p.id}/cancel`); done(`Orden ${r.productionNumber} cancelada.`); }
      });
    } else if (a === 'register') {
      modal({
        title: `Registrar unidades · ${p.productionNumber}`, size: 'narrow', submitLabel: 'Registrar',
        body: `<p class="muted small">${esc(p.productName)} · avance ${num(p.quantityCompleted)} de ${num(p.quantityPlanned)} ${esc(unit(p.unit))}. Las unidades entran al almacén ${esc(p.warehouseCode)} como <strong>Entrada de producción</strong>.</p>
          ${field({ name: 'quantity', label: `Unidades terminadas (${unit(p.unit)})`, type: 'number', required: true, value: '', attrs: 'step="any" min="0.001" inputmode="decimal"', hint: `Pendiente por producir: ${num(remaining)}.` })}`,
        onSubmit: async (form) => {
          const q = Number(formValues(form).quantity);
          validate({ quantity: [[q > 0, 'La cantidad debe ser mayor que cero.'], [q <= remaining, `Excede lo pendiente por producir (${num(remaining)}).`]] });
          const r = await post(`/api/production-orders/${p.id}/register`, { quantity: q });
          done(`Se registraron ${num(q)} ${unit(p.unit)} en ${r.productionNumber} (${num(r.progressPercent)}%).`);
        }
      });
    } else if (a === 'complete') {
      modal({
        title: `Completar orden · ${p.productionNumber}`, size: 'narrow', submitLabel: 'Completar orden',
        body: `<p class="muted small">Cierra la orden. Si terminó unidades que aún no registró, indíquelas para recibirlas en la misma operación.</p>
          ${field({ name: 'quantity', label: `Cantidad final adicional (${unit(p.unit)})`, type: 'number', value: '', attrs: 'step="any" min="0" inputmode="decimal"', hint: `Opcional. Registrado: ${num(p.quantityCompleted)} de ${num(p.quantityPlanned)}; pendiente ${num(remaining)}.` })}`,
        onSubmit: async (form) => {
          const raw = formValues(form).quantity;
          const q = raw === '' ? null : Number(raw);
          validate({ quantity: [[q === null || (q >= 0 && q <= remaining), `Indique una cantidad entre 0 y ${num(remaining)} o deje el campo vacío.`],
            [p.quantityCompleted > 0 || q > 0, 'Registre al menos una unidad terminada: indique la cantidad final.']] });
          const r = await post(`/api/production-orders/${p.id}/complete`, q ? { quantity: q } : null);
          done(`Orden ${r.productionNumber} completada con ${num(r.quantityCompleted)} ${unit(p.unit)}.`);
        }
      });
    }
  }

  async function createModal() {
    const [products, warehouses] = await Promise.all([ref.products(), ref.warehouses()]);
    const producible = products.filter((p) => p.active && p.category !== 'Accesorios');
    modal({
      title: 'Nueva orden de producción', submitLabel: 'Crear orden',
      body: `<p class="muted small">Solo productos de vidrio; los accesorios se compran, no se producen.</p>
        <div class="form-grid">
          ${field({ name: 'productId', label: 'Producto', type: 'select', required: true, span: true, options: options(producible, 'id', (p) => `${p.productCode} · ${p.productName} (${p.category})`, 'Seleccione un producto') })}
          ${field({ name: 'warehouseId', label: 'Almacén de recepción', type: 'select', required: true, options: options(warehouses, 'id', (w) => `${w.code} · ${w.name}`, 'Seleccione un almacén') })}
          ${field({ name: 'quantityPlanned', label: 'Cantidad planeada', type: 'number', required: true, attrs: 'step="any" min="0.001" inputmode="decimal"' })}
          ${field({ name: 'dueDate', label: 'Fecha compromiso', type: 'date', attrs: `min="${isoDay()}"` })}
          ${field({ name: 'notes', label: 'Notas', type: 'textarea', span: true, attrs: 'maxlength="500" rows="2"' })}
        </div>`,
      onSubmit: async (form) => {
        const v = formValues(form);
        const q = Number(v.quantityPlanned);
        validate({
          productId: [[v.productId, 'Seleccione un producto.']],
          warehouseId: [[v.warehouseId, 'Seleccione un almacén.']],
          quantityPlanned: [[q > 0, 'La cantidad debe ser mayor que cero.'], [q <= 1000000, 'Máximo 1,000,000.']],
          notes: [[v.notes.length <= 500, 'Máximo 500 caracteres.']]
        });
        const r = await post('/api/production-orders', {
          productId: Number(v.productId), warehouseId: Number(v.warehouseId), quantityPlanned: q,
          dueDate: v.dueDate || null, notes: v.notes || null
        });
        toast(`Orden ${r.productionNumber} creada (planeada).`);
        reload();
      }
    });
  }

  reload();
}
