import { get, download, ref } from '../api.js';
import { icon } from '../icons.js';
import { replaceQuery } from '../app.js';
import { esc, money, num, int, fdate, fdatetime, fday, field, formValues, toast, toastError, load, emptyState, statusChip, SALES_STATUS, PROD_STATUS, unit, $, $$ } from '../ui.js';

const REPORTS = {
  ventas: {
    label: 'Ventas', path: '/api/reports/sales', filters: ['from', 'to', 'salesStatus'],
    cols: [
      ['Pedido', (r) => `<span class="mono">${esc(r.orderNumber)}</span>`],
      ['Fecha', (r) => esc(fdate(r.orderDate)), 'nowrap'],
      ['Cliente', (r) => `${esc(r.customerName)}<div class="cell-sub mono">${esc(r.customerCode)}</div>`],
      ['Estado', (r) => statusChip(r.status)],
      ['Subtotal', (r) => money(r.subtotal), 'num'],
      ['IVA', (r) => money(r.tax), 'num'],
      ['Total', (r) => money(r.total), 'num']
    ],
    totals: (t) => [['Pedidos', int(t.Orders)], ['Subtotal facturado', money(t.BilledSubtotal)], ['IVA facturado', money(t.BilledTax)], ['Total facturado', money(t.BilledTotal)]],
    footer: (t) => ['Total facturado (confirmados y embarcados)', '', '', '', money(t.BilledSubtotal), money(t.BilledTax), money(t.BilledTotal)]
  },
  inventario: {
    label: 'Inventario', path: '/api/reports/inventory', filters: ['warehouseId', 'category'],
    cols: invCols(),
    totals: (t) => [['Partidas', int(t.Lines)], ['Bajo mínimo', int(t.LowStockLines)], ['Valor a costo', money(t.InventoryValue)]],
    footer: (t) => ['Total', '', '', '', '', '', '', '', money(t.InventoryValue), '']
  },
  bajo: {
    label: 'Inventario bajo', path: '/api/reports/low-stock', filters: ['warehouseId'],
    cols: invCols(),
    totals: (t) => [['Partidas bajo mínimo', int(t.Lines)], ['Valor a costo', money(t.InventoryValue)]],
    footer: (t) => ['Total', '', '', '', '', '', '', '', money(t.InventoryValue), '']
  },
  produccion: {
    label: 'Producción', path: '/api/reports/production', filters: ['from', 'to', 'prodStatus'],
    cols: [
      ['Orden', (r) => `<span class="mono">${esc(r.productionNumber)}</span>`],
      ['Producto', (r) => `<span class="mono">${esc(r.productCode)}</span><div class="cell-sub">${esc(r.productName)}</div>`],
      ['Estado', (r) => statusChip(r.status, 'prod')],
      ['Planeado', (r) => num(r.quantityPlanned), 'num'],
      ['Completado', (r) => num(r.quantityCompleted), 'num'],
      ['Avance', (r) => `${num(r.progressPercent)}%`, 'num'],
      ['Inicio', (r) => esc(fday(r.startDate)), 'nowrap'],
      ['Fin', (r) => esc(fday(r.completionDate)), 'nowrap'],
      ['Compromiso', (r) => esc(fday(r.dueDate)), 'nowrap']
    ],
    totals: (t) => [['Órdenes', int(t.Orders)], ['Cantidad planeada', num(t.Planned)], ['Cantidad completada', num(t.Completed)], ['Avance global', t.Planned ? `${num((t.Completed / t.Planned) * 100)}%` : '—']],
    footer: (t) => ['Total', '', '', num(t.Planned), num(t.Completed), t.Planned ? `${num((t.Completed / t.Planned) * 100)}%` : '', '', '', '']
  }
};

function invCols() {
  return [
    ['Producto', (r) => `<span class="mono">${esc(r.productCode)}</span><div class="cell-sub">${esc(r.productName)}</div>`],
    ['Categoría', (r) => esc(r.category)],
    ['Almacén', (r) => `<span class="mono">${esc(r.warehouseCode)}</span>`],
    ['Disponible', (r) => `${num(r.quantityAvailable)} <span class="muted">${esc(unit(r.unit))}</span>`, 'num'],
    ['Reservado', (r) => num(r.quantityReserved), 'num'],
    ['Libre', (r) => num(r.quantityFree), 'num'],
    ['Mínimo', (r) => num(r.minimumStock), 'num'],
    ['Costo unit.', (r) => money(r.unitCost), 'num'],
    ['Valor', (r) => money(r.stockValue), 'num'],
    ['Estado', (r) => r.isLowStock ? (r.quantityFree <= 0 ? '<span class="chip chip-danger">Agotado</span>' : '<span class="chip chip-warn">Bajo mínimo</span>') : '<span class="chip chip-ok">En nivel</span>']
  ];
}

export default async function reports(root, { query, ctx }) {
  let tab = REPORTS[query.tab] ? query.tab : 'ventas';
  const [warehouses, categories] = await Promise.all([ref.warehouses(), ref.categories()]);
  const filters = {};
  let lastResult = null;

  root.innerHTML = `
    <div class="page-head">
      <div><h2>Reportes</h2><p>Consultas listas para imprimir o exportar a CSV (Excel). Los mismos datos están disponibles como vistas SQL para Crystal Reports al usar SQL Server.</p></div>
    </div>
    <div class="tabs" role="tablist" aria-label="Tipo de reporte">${Object.entries(REPORTS).map(([k, r]) =>
      `<button class="tab" role="tab" type="button" id="rt-${k}" data-tab="${k}" aria-controls="rep-panel">${r.label}</button>`).join('')}</div>
    <section class="card" id="rep-panel" role="tabpanel">
      <form class="toolbar" id="rep-filters" aria-label="Filtros del reporte"></form>
      <div id="rep-out"></div>
    </section>`;

  const form = $('#rep-filters', root);
  const out = $('#rep-out', root);

  function filterFields(def) {
    const f = filters[tab] ||= {};
    const parts = def.filters.map((k) => {
      if (k === 'from') return field({ name: 'from', label: 'Desde', type: 'date', value: f.from || '' });
      if (k === 'to') return field({ name: 'to', label: 'Hasta', type: 'date', value: f.to || '' });
      if (k === 'salesStatus') return field({ name: 'status', label: 'Estado', type: 'select', value: f.status || '', options: [['', 'Todos'], ...Object.entries(SALES_STATUS)] });
      if (k === 'prodStatus') return field({ name: 'status', label: 'Estado', type: 'select', value: f.status || '', options: [['', 'Todos'], ...Object.entries(PROD_STATUS)] });
      if (k === 'warehouseId') return field({ name: 'warehouseId', label: 'Almacén', type: 'select', value: f.warehouseId || '', options: [['', 'Todos los almacenes'], ...warehouses.map((w) => [w.id, `${w.code} · ${w.name}`])] });
      if (k === 'category') return field({ name: 'category', label: 'Categoría', type: 'select', value: f.category || '', options: [['', 'Todas las categorías'], ...categories.map((c) => [c, c])] });
      return '';
    });
    form.innerHTML = parts.join('') + `<span class="spacer"></span>
      <button class="btn btn-primary" type="submit">${icon('search')} Consultar</button>
      <button class="btn" type="button" id="rep-csv">${icon('download')} Exportar CSV</button>
      <button class="btn" type="button" id="rep-print">${icon('printer')} Imprimir</button>`;
    $('#rep-csv', form).addEventListener('click', exportCsv);
    $('#rep-print', form).addEventListener('click', () => {
      if (!lastResult) { toast('Consulte el reporte antes de imprimir.', 'warn'); return; }
      window.print();
    });
  }

  function currentQuery() {
    const v = formValues(form);
    filters[tab] = v;
    return v;
  }

  function filterSummary(v) {
    const parts = [];
    if (v.from || v.to) parts.push(`Periodo: ${v.from ? fday(v.from) : 'inicio'} – ${v.to ? fday(v.to) : 'hoy'}`);
    if (v.status) parts.push(`Estado: ${(SALES_STATUS[v.status] || PROD_STATUS[v.status])}`);
    if (v.warehouseId) { const w = warehouses.find((x) => x.id == v.warehouseId); parts.push(`Almacén: ${w ? `${w.code} · ${w.name}` : v.warehouseId}`); }
    if (v.category) parts.push(`Categoría: ${v.category}`);
    return parts.length ? parts.join(' · ') : 'Sin filtros (todos los registros)';
  }

  function run() {
    const def = REPORTS[tab];
    const v = currentQuery();
    if (v.from && v.to && v.from > v.to) { out.innerHTML = `<div class="card-body"><div class="note warn">La fecha inicial es posterior a la final.</div></div>`; return; }
    lastResult = null;
    load(out, () => get(def.path, v), (r) => {
      lastResult = r;
      const t = r.totals || {};
      const foot = def.footer(t);
      return `
        <div class="print-only print-header">
          <div><div class="co">GlasFlow ERP — Vidrios del Norte (empresa ficticia)</div><div class="rt">${esc(r.title)}</div><div class="small">${esc(filterSummary(v))}</div></div>
          <div class="pf">Generado: ${esc(fdatetime(r.generatedAt))}<br>Usuario: ${esc(ctx.user.displayName || ctx.user.username)}<br>${int(r.rows.length)} registros</div>
        </div>
        <div class="card-head no-print"><div><h3>${esc(r.title)}</h3><div class="sub">${esc(filterSummary(v))} · generado ${esc(fdatetime(r.generatedAt))}</div></div></div>
        <div class="card-body"><div class="stat-row">${def.totals(t).map(([l, val]) => `<div class="stat"><div class="s-label">${esc(l)}</div><div class="s-value">${val}</div></div>`).join('')}</div></div>
        ${r.rows.length ? `<div class="table-wrap"><table class="data"><thead><tr>${def.cols.map(([h, , cls]) => `<th class="${cls === 'num' ? 'num' : ''}${h === 'Producto' ? ' w-prod' : ''}">${esc(h)}</th>`).join('')}</tr></thead>
          <tbody>${r.rows.map((row) => `<tr>${def.cols.map(([, fn, cls]) => `<td class="${cls || ''}">${fn(row)}</td>`).join('')}</tr>`).join('')}</tbody>
          <tfoot><tr>${foot.map((c, i) => `<td class="${def.cols[i]?.[2] === 'num' ? 'num' : ''}">${c}</td>`).join('')}</tr></tfoot></table></div>`
          : emptyState('Sin registros', 'No hay datos para los filtros seleccionados.', 'report')}`;
    });
  }

  async function exportCsv(e) {
    const b = e.currentTarget;
    const def = REPORTS[tab];
    b.disabled = true;
    try {
      const name = await download(`${def.path}/csv`, currentQuery(), `glasflow-${tab}.csv`);
      toast(`Descargado ${name}.`);
    } catch (err) { toastError(err); }
    finally { b.disabled = false; }
  }

  function selectTab(k) {
    tab = k;
    $$('[data-tab]', root).forEach((b) => { b.setAttribute('aria-selected', String(b.dataset.tab === k)); b.tabIndex = b.dataset.tab === k ? 0 : -1; });
    $('#rep-panel', root).setAttribute('aria-labelledby', `rt-${k}`);
    replaceQuery(k === 'ventas' ? {} : { tab: k });
    filterFields(REPORTS[k]);
    run();
  }

  form.addEventListener('submit', (e) => { e.preventDefault(); run(); });
  $$('[data-tab]', root).forEach((b) => b.addEventListener('click', () => selectTab(b.dataset.tab)));
  selectTab(tab);
}
