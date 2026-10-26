import { get, post } from '../api.js';
import { icon } from '../icons.js';
import { barChart, shareBars } from '../charts.js';
import { esc, money, moneyShort, num, int, fdate, fdatetime, ftime, relTime, statusChip, progress, toast, toastError, errorState, unit, MOVEMENT_TYPES, PROD_STATUS, fday, $ } from '../ui.js';

const REFRESH_MS = 60000;

export default async function dashboard(root, { ctx }) {
  root.innerHTML = `
    <div class="page-head">
      <div><h2>Panel de control</h2><p id="dash-updated" class="small">Cargando indicadores…</p></div>
      <div class="page-actions">
        <button class="btn" type="button" id="dash-refresh">${icon('refresh')} Actualizar</button>
        ${ctx.canWrite ? `<button class="btn btn-primary" type="button" id="dash-check">${icon('shield')} Ejecutar revisión de inventario</button>` : ''}
      </div>
    </div>
    <div id="check-result"></div>
    <div id="dash-body">${skeleton()}</div>`;

  let disposeChart = null;
  let timer = null;
  let loading = false;

  async function refresh(manual = false) {
    if (loading) return;
    loading = true;
    const btn = $('#dash-refresh', root);
    if (btn) { btn.disabled = true; btn.innerHTML = `<span class="spinner" aria-hidden="true"></span> Actualizando`; }
    try {
      const d = await get('/api/dashboard');
      if (!root.isConnected) return;
      disposeChart?.();
      $('#dash-body', root).innerHTML = render(d);
      disposeChart = barChart($('#sales-chart', root), d.salesByMonth.map((m) => ({
        label: m.label, value: m.total, line: m.orders,
        tip: `${m.label}\nVentas: ${money(m.total)}\nPedidos: ${m.orders}`
      })), { format: money, axisFormat: moneyShort, ariaLabel: 'Ventas facturadas de los últimos 12 meses', height: 250 });
      $('#dash-updated', root).textContent = `Actualizado a las ${ftime(d.generatedAt)} · se actualiza automáticamente cada 60 s mientras la página está visible`;
      if (manual) toast('Indicadores actualizados.', 'info');
      ctx.refreshNotifications();
    } catch (err) {
      if (err.status === 401) return;
      if ($('#dash-body', root).querySelector('.kpis')) toastError(err, 'No se pudo actualizar');
      else {
        $('#dash-body', root).innerHTML = `<div class="card">${errorState(err, 'dash-retry')}</div>`;
        $('#dash-retry', root)?.addEventListener('click', () => refresh());
      }
    } finally {
      loading = false;
      if (btn?.isConnected) { btn.disabled = false; btn.innerHTML = `${icon('refresh')} Actualizar`; }
    }
  }

  $('#dash-refresh', root).addEventListener('click', () => refresh(true));
  $('#dash-check', root)?.addEventListener('click', async (e) => {
    const b = e.currentTarget;
    b.disabled = true; b.innerHTML = `<span class="spinner" aria-hidden="true"></span> Revisando inventario…`;
    try {
      const r = await post('/api/inventory/check');
      $('#check-result', root).innerHTML = `
        <div class="note ${r.lowStockItems ? 'warn' : 'ok'}" style="margin-bottom:16px;display:flex;gap:12px;align-items:flex-start;justify-content:space-between;flex-wrap:wrap" role="status">
          <div><strong>Revisión de inventario ejecutada ${esc(fdatetime(r.ranAt))}.</strong>
          ${int(r.itemsChecked)} partidas revisadas · ${int(r.lowStockItems)} bajo mínimo · ${int(r.notificationsCreated)} alertas nuevas · ${int(r.notificationsResolved)} alertas resueltas.</div>
          <button class="btn btn-ghost btn-sm" type="button" data-dismiss aria-label="Ocultar resultado">${icon('x')}</button>
        </div>`;
      $('#check-result [data-dismiss]', root).addEventListener('click', () => { $('#check-result', root).innerHTML = ''; });
      toast(`Revisión completada: ${r.lowStockItems} partidas bajo mínimo, ${r.notificationsCreated} alertas nuevas.`);
      refresh();
    } catch (err) { toastError(err); }
    finally { if (b.isConnected) { b.disabled = false; b.innerHTML = `${icon('shield')} Ejecutar revisión de inventario`; } }
  });

  // Dashboard refresh automation: every 60 s while the tab is visible, and right away when it becomes visible again.
  timer = setInterval(() => { if (document.visibilityState === 'visible') refresh(); }, REFRESH_MS);
  const onVisible = () => { if (document.visibilityState === 'visible') refresh(); };
  document.addEventListener('visibilitychange', onVisible);

  await refresh();
  return () => { clearInterval(timer); document.removeEventListener('visibilitychange', onVisible); disposeChart?.(); };
}

function skeleton() {
  return `<div class="kpis">${Array.from({ length: 8 }, () => `<div class="card kpi"><div class="skeleton sk-line" style="width:50%"></div><div class="skeleton sk-line" style="height:24px;width:70%"></div><div class="skeleton sk-line" style="width:40%"></div></div>`).join('')}</div>
    <div class="grid grid-main"><div class="card"><div class="card-body"><div class="skeleton sk-block"></div></div></div><div class="card"><div class="card-body"><div class="skeleton sk-block"></div></div></div></div>`;
}

function render(d) {
  const k = d.kpis;
  const change = k.salesLastMonth > 0 ? (k.salesThisMonth - k.salesLastMonth) / k.salesLastMonth : null;
  const delta = change === null ? '<span>Sin ventas el mes anterior</span>'
    : `<span class="delta ${change >= 0 ? 'up' : 'down'}">${icon(change >= 0 ? 'arrowUp' : 'arrowDown')}${num(Math.abs(change) * 100)}%</span><span>vs. mes anterior (${esc(moneyShort(k.salesLastMonth))})</span>`;
  const now = new Date();
  const monthName = now.toLocaleDateString('es-MX', { month: 'long' });
  const catTotal = d.inventoryByCategory.reduce((s, c) => s + c.value, 0);

  return `
  <section class="kpis" aria-label="Indicadores clave">
    ${kpi('Ventas del mes', 'money', money(k.salesThisMonth), delta, '#/ventas', `Ventas facturadas en ${monthName}`)}
    ${kpi('Ventas acumuladas del año', 'trend', money(k.salesYearToDate), `<span>Enero a ${esc(monthName)} ${now.getFullYear()}</span>`, '#/reportes')}
    ${kpi('Pedidos en borrador', 'file', int(k.pendingOrders), '<span>Pendientes de confirmar</span>', '#/ventas?status=Draft', '', k.pendingOrders ? 'attn' : '')}
    ${kpi('Confirmados por embarcar', 'truck', int(k.confirmedOrders), '<span>Existencia ya reservada</span>', '#/ventas?status=Confirmed')}
    ${kpi('Valor de inventario', 'layers', money(k.inventoryValue), '<span>A costo, todos los almacenes</span>', '#/inventario')}
    ${kpi('Productos bajo mínimo', 'alert', int(k.lowStockProducts), `<span>${k.lowStockProducts ? 'Requieren reabasto' : 'Todo en nivel'}</span>`, '#/inventario?low=1', '', k.lowStockProducts ? 'alert' : '')}
    ${kpi('Producción activa', 'factory', `${int(k.activeProductionOrders)} <span class="muted" style="font-size:14px;font-weight:500">órdenes</span>`, `<div style="flex:1">${progress(k.productionProgressPercent)}</div>`, '#/produccion')}
    ${kpi('Alertas sin leer', 'bell', int(k.unreadNotifications), '<span>Ver en la campana de notificaciones</span>', '', '', k.unreadNotifications ? 'attn' : '')}
  </section>

  <div class="grid grid-main">
    <section class="card" aria-labelledby="h-sales">
      <div class="card-head"><div><h3 id="h-sales">Ventas de los últimos 12 meses</h3><div class="sub">Pedidos confirmados y embarcados, total con IVA</div></div>
        <div class="legend"><span><i style="background:var(--chart-muted)"></i>Ventas (MXN)</span><span><i style="background:var(--accent)"></i>Mes en curso</span><span><i style="background:var(--warn);height:2px"></i>Pedidos</span></div></div>
      <div class="card-body"><div id="sales-chart"></div></div>
    </section>
    <section class="card" aria-labelledby="h-cat">
      <div class="card-head"><div><h3 id="h-cat">Inventario por categoría</h3><div class="sub">Valor a costo · total ${esc(money(catTotal))}</div></div></div>
      <div class="card-body">${d.inventoryByCategory.length ? shareBars(d.inventoryByCategory.map((c) => ({ label: c.category, value: c.value })), moneyShort) : '<p class="muted">Sin existencias.</p>'}</div>
    </section>
  </div>

  <div class="grid grid-2 mt-16">
    <section class="card" aria-labelledby="h-low">
      <div class="card-head"><div><h3 id="h-low">Alertas de inventario bajo</h3><div class="sub">Existencia libre por debajo del mínimo</div></div><a class="btn btn-sm" href="#/inventario?low=1">Ver todo ${icon('arrowRight')}</a></div>
      ${d.lowStock.length ? `<ul class="list">${d.lowStock.map((s) => {
        const ratio = s.minimumStock > 0 ? Math.min(100, (s.quantityFree / s.minimumStock) * 100) : 0;
        return `<li><div class="grow"><div class="title"><span class="mono">${esc(s.productCode)}</span> · ${esc(s.productName)}</div>
          <div class="meta">${esc(s.warehouseCode)} · libre ${num(s.quantityFree)} de mínimo ${num(s.minimumStock)} ${esc(unit(s.unit))}</div></div>
          <div class="stock-bar ${s.quantityFree <= 0 ? 'out' : ''}"><div class="progress" aria-hidden="true"><span style="width:${ratio}%"></span></div></div>
          ${s.quantityFree <= 0 ? '<span class="chip chip-danger">Agotado</span>' : '<span class="chip chip-warn">Bajo</span>'}</li>`;
      }).join('')}</ul>` : `<div class="state">${icon('check')}<h4>Sin alertas</h4><p>Todas las partidas están sobre su mínimo.</p></div>`}
    </section>
    <section class="card" aria-labelledby="h-prod">
      <div class="card-head"><div><h3 id="h-prod">Producción activa</h3><div class="sub">Órdenes planeadas y en proceso</div></div><a class="btn btn-sm" href="#/produccion">Ver todo ${icon('arrowRight')}</a></div>
      ${d.activeProduction.length ? `<ul class="list">${d.activeProduction.map((p) => `
        <li><div class="grow"><div class="title"><span class="mono">${esc(p.productionNumber)}</span> · ${esc(p.productName)}</div>
          <div class="meta">${esc(p.warehouseCode)} · ${num(p.quantityCompleted)} / ${num(p.quantityPlanned)} ${esc(unit(p.unit))} · compromiso ${esc(fday(p.dueDate))}</div>
          <div style="margin-top:6px">${progress(p.progressPercent)}</div></div>
          <span class="chip chip-${p.status.toLowerCase()}">${esc(PROD_STATUS[p.status])}</span></li>`).join('')}</ul>`
        : `<div class="state">${icon('factory')}<h4>Sin producción activa</h4></div>`}
    </section>
  </div>

  <div class="grid grid-2 mt-16">
    <section class="card" aria-labelledby="h-orders">
      <div class="card-head"><h3 id="h-orders">Pedidos recientes</h3><a class="btn btn-sm" href="#/ventas">Ver pedidos ${icon('arrowRight')}</a></div>
      <div class="table-wrap"><table class="data"><thead><tr><th>Pedido</th><th>Cliente</th><th>Estado</th><th class="num">Total</th></tr></thead><tbody>
        ${d.recentOrders.map((o) => `<tr><td><a href="#/ventas/${o.id}" class="mono">${esc(o.orderNumber)}</a><div class="cell-sub nowrap">${esc(fdate(o.orderDate))}</div></td>
          <td>${esc(o.customerName)}</td><td>${statusChip(o.status)}</td><td class="num">${esc(money(o.total))}</td></tr>`).join('') || '<tr><td colspan="4" class="muted">Sin pedidos.</td></tr>'}
      </tbody></table></div>
    </section>
    <section class="card" aria-labelledby="h-mov">
      <div class="card-head"><h3 id="h-mov">Movimientos de inventario recientes</h3><a class="btn btn-sm" href="#/inventario?tab=movimientos">Ver historial ${icon('arrowRight')}</a></div>
      <div class="table-wrap"><table class="data"><thead><tr><th>Movimiento</th><th>Producto</th><th>Almacén</th><th class="num">Cantidad</th></tr></thead><tbody>
        ${d.recentMovements.map((m) => `<tr><td><span class="tag">${esc(MOVEMENT_TYPES[m.movementType] || m.movementType)}</span><div class="cell-sub nowrap">${esc(relTime(m.timestamp))} · <span class="mono">${esc(m.reference)}</span></div></td>
          <td><span class="mono">${esc(m.productCode)}</span><div class="cell-sub">${esc(m.productName)}</div></td><td>${esc(m.warehouseCode)}</td>
          <td class="num ${m.quantity >= 0 ? 'qty-pos' : 'qty-neg'}">${m.quantity > 0 ? '+' : ''}${esc(num(m.quantity))}</td></tr>`).join('') || '<tr><td colspan="4" class="muted">Sin movimientos.</td></tr>'}
      </tbody></table></div>
    </section>
  </div>`;
}

function kpi(label, iconName, value, foot, href, title = '', tone = '') {
  return `<article class="card kpi ${tone}" ${title ? `title="${esc(title)}"` : ''}>
    <div class="kpi-label">${icon(iconName)}${esc(label)}</div>
    <div class="kpi-value">${value}</div>
    <div class="kpi-foot">${foot}</div>
    ${href ? `<a class="kpi-link" href="${href}" aria-label="${esc(label)}: ver detalle"></a>` : ''}
  </article>`;
}
