import { get, post, ref, invalidate } from '../api.js';
import { icon } from '../icons.js';
import { navigate, setTitle, replaceQuery } from '../app.js';
import {
  esc, money, num, int, fdate, fdatetime, statusChip, field, formValues, confirmDialog, validate, toast, toastError, load, pager,
  emptyState, errorState, debounce, options, unit, isoDay, SALES_STATUS, TAX_RATES, TAX_ZONES, ValidationError, showErrors, clearErrors, $, $$
} from '../ui.js';

export default async function sales(root, { params, query, ctx }) {
  if (params[0] === 'nuevo') {
    if (!ctx.canWrite) { root.innerHTML = `<div class="card">${emptyState('Acceso de solo lectura', 'Su rol no puede crear pedidos.', 'shield')}</div>`; return; }
    return newOrder(root, query);
  }
  if (params[0]) return orderDetail(root, Number(params[0]), ctx);
  return orderList(root, query, ctx);
}

// ---------------- List ----------------
async function orderList(root, query, ctx) {
  const s = { page: 1, status: query.status || '', search: '', from: '', to: '', customerId: query.customerId || '' };
  let customerName = '';
  if (s.customerId) {
    const list = await ref.customers().catch(() => []);
    customerName = list.find((c) => c.id == s.customerId)?.name || `Cliente ${s.customerId}`;
  }
  root.innerHTML = `
    <div class="page-head">
      <div><h2>Pedidos de venta</h2><p>Flujo: Borrador → Confirmado (reserva existencia) → Embarcado (descuenta existencia). IVA según la zona fiscal del cliente.</p></div>
      <div class="page-actions">${ctx.canWrite ? `<a class="btn btn-primary" href="#/ventas/nuevo">${icon('plus')} Nuevo pedido</a>` : ''}</div>
    </div>
    <section class="card">
      <form class="toolbar" role="search" aria-label="Filtrar pedidos" id="so-filters">
        <div class="field"><span class="field-label" id="st-lbl">Estado</span>
          <div class="seg" role="group" aria-labelledby="st-lbl">${[['', 'Todos'], ...Object.entries(SALES_STATUS)].map(([k, l]) =>
            `<button type="button" data-status="${k}" aria-pressed="${s.status === k}">${l}</button>`).join('')}</div></div>
        <div class="field grow"><label for="so-search">Buscar</label><div class="input-icon">${icon('search')}<input class="input" id="so-search" name="search" placeholder="Número de pedido o nombre del cliente"></div></div>
        ${field({ name: 'from', label: 'Desde', type: 'date' })}
        ${field({ name: 'to', label: 'Hasta', type: 'date' })}
      </form>
      ${s.customerId ? `<div class="toolbar" style="padding-top:8px;padding-bottom:8px"><span class="chip chip-accent plain">Cliente: ${esc(customerName)}</span><button class="link-btn small" type="button" id="clear-cust">Quitar filtro de cliente</button></div>` : ''}
      <div id="so-table"></div>
    </section>`;
  const f = $('#so-filters', root);
  const box = $('#so-table', root);
  f.addEventListener('submit', (e) => e.preventDefault());
  const apply = () => { const v = formValues(f); Object.assign(s, { search: v.search, from: v.from, to: v.to, page: 1 }); reload(); };
  f.search.addEventListener('input', debounce(apply, 300));
  f.from.addEventListener('change', apply);
  f.to.addEventListener('change', apply);
  $$('[data-status]', f).forEach((b) => b.addEventListener('click', () => {
    s.status = b.dataset.status; s.page = 1;
    $$('[data-status]', f).forEach((x) => x.setAttribute('aria-pressed', String(x === b)));
    replaceQuery({ status: s.status, customerId: s.customerId });
    reload();
  }));
  $('#clear-cust', root)?.addEventListener('click', () => navigate(s.status ? `#/ventas?status=${s.status}` : '#/ventas'));

  function reload() {
    if (s.from && s.to && s.from > s.to) { box.innerHTML = `<div class="card-body"><div class="note warn">La fecha inicial es posterior a la final.</div></div>`; return; }
    load(box, () => get('/api/sales-orders', { page: s.page, pageSize: 25, status: s.status, search: s.search, from: s.from, to: s.to, customerId: s.customerId }), (r) => {
      if (!r.items.length) return emptyState('Sin pedidos', 'No hay pedidos con estos filtros.', 'cart');
      return `<div class="table-wrap"><table class="data"><thead><tr>
        <th>Pedido</th><th>Fecha</th><th>Cliente</th><th>Almacén</th><th>Estado</th><th class="num">Partidas</th><th class="num">Subtotal</th><th class="num">IVA</th><th class="num">Total</th>
      </tr></thead><tbody>${r.items.map((o) => `
        <tr class="clickable" data-id="${o.id}">
          <td><a class="mono cell-main" href="#/ventas/${o.id}">${esc(o.orderNumber)}</a></td>
          <td class="nowrap">${esc(fdate(o.orderDate))}</td>
          <td>${esc(o.customerName)}</td>
          <td class="mono">${esc(o.warehouseCode)}</td>
          <td>${statusChip(o.status)}</td>
          <td class="num">${o.lineCount}</td>
          <td class="num">${money(o.subtotal)}</td>
          <td class="num">${money(o.tax)}</td>
          <td class="num"><strong>${money(o.total)}</strong></td>
        </tr>`).join('')}</tbody></table></div>${pager(r, 'pedidos')}`;
    }).then((r) => {
      if (!r) return;
      $$('[data-page]', box).forEach((b) => b.addEventListener('click', () => { s.page = Number(b.dataset.page); reload(); }));
      $$('tr[data-id]', box).forEach((tr) => tr.addEventListener('click', (e) => { if (!e.target.closest('a')) navigate(`#/ventas/${tr.dataset.id}`); }));
    });
  }
  reload();
}

// ---------------- New order ----------------
async function newOrder(root, query) {
  setTitle('Nuevo pedido', 'Operación · Ventas');
  root.innerHTML = `<div class="state"><span class="spinner"></span></div>`;
  let customers, warehouses, products;
  try {
    [customers, warehouses, products] = await Promise.all([ref.customers(), ref.warehouses(), ref.products()]);
  } catch (err) {
    root.innerHTML = `<div class="card">${errorState(err)}</div>`;
    return;
  }
  customers = customers.filter((c) => c.active);
  products = products.filter((p) => p.active);
  const freeByProduct = new Map();
  let lines = [{ productId: '', quantity: '' }];

  root.innerHTML = `
    <div class="page-head">
      <div><a href="#/ventas" class="link-btn small">${icon('arrowLeft')} Pedidos</a><h2 style="margin-top:6px">Nuevo pedido de venta</h2>
        <p>El pedido se crea en <strong>Borrador</strong>. La existencia se reserva al confirmarlo. Los importes finales los calcula el servidor.</p></div>
    </div>
    <form id="so-form" novalidate>
      <div class="form-alert" role="alert" style="margin-bottom:16px"></div>
      <div class="grid grid-main">
        <div class="stack">
          <section class="card"><div class="card-head"><h3>Cliente y almacén</h3></div>
            <div class="card-body form-grid">
              ${field({ name: 'customerId', label: 'Cliente', type: 'select', required: true, value: query.customerId || '', options: options(customers, 'id', (c) => `${c.customerCode} · ${c.name}`, 'Seleccione un cliente'), span: true })}
              ${field({ name: 'warehouseId', label: 'Almacén de surtido', type: 'select', required: true, value: '', options: options(warehouses, 'id', (w) => `${w.code} · ${w.name}`, 'Seleccione un almacén') })}
              ${field({ name: 'externalReference', label: 'Referencia externa', attrs: 'maxlength="60" autocomplete="off"', placeholder: 'p. ej. OC-55821', hint: 'Opcional. Evita pedidos duplicados: no se aceptan dos pedidos con la misma referencia.' })}
              ${field({ name: 'notes', label: 'Notas', type: 'textarea', span: true, attrs: 'maxlength="500" rows="2"' })}
              <div class="span-2" id="cust-info"></div>
            </div>
          </section>
          <section class="card"><div class="card-head"><div><h3>Partidas</h3><div class="sub" id="lines-sub">Seleccione un almacén para ver la existencia libre</div></div>
              <button class="btn btn-sm" type="button" id="add-line">${icon('plus')} Agregar partida</button></div>
            <div class="table-wrap"><table class="data line-editor"><thead><tr><th style="min-width:240px">Producto</th><th class="num">Cantidad</th><th class="num">Precio unitario</th><th class="num">Libre en almacén</th><th class="num">Importe</th><th><span class="sr-only">Quitar</span></th></tr></thead>
              <tbody id="lines"></tbody></table></div>
            <p class="field-error" data-error-for="lines" style="padding:0 16px 12px"></p>
          </section>
        </div>
        <aside class="card" style="align-self:start;position:sticky;top:calc(var(--top-h) + 16px)">
          <div class="card-head"><div><h3>Resumen</h3><div class="sub">Vista previa; el servidor recalcula</div></div></div>
          <div class="card-body stack">
            <dl class="totals" id="totals"></dl>
            <div id="stock-warn"></div>
            <button class="btn btn-primary" type="submit" style="width:100%">${icon('check')} Crear pedido en borrador</button>
            <a class="btn" href="#/ventas" style="width:100%">Cancelar</a>
          </div>
        </aside>
      </div>
    </form>`;

  const form = $('#so-form', root);
  const tbody = $('#lines', root);

  const product = (id) => products.find((p) => p.id == id);
  const customer = () => customers.find((c) => c.id == form.customerId.value);
  const rate = () => TAX_RATES[customer()?.taxZone] ?? 0.16;

  function renderLines() {
    const prodOpts = options(products, 'id', (p) => `${p.productCode} · ${p.productName}`, 'Seleccione un producto');
    tbody.innerHTML = lines.map((l, i) => {
      const p = product(l.productId);
      return `<tr data-i="${i}">
        <td><label class="sr-only" for="ln-p-${i}">Producto de la partida ${i + 1}</label>
          <select class="input" id="ln-p-${i}" data-field="productId">${prodOpts.map(([v, t]) => `<option value="${esc(v)}" ${String(v) === String(l.productId) ? 'selected' : ''}>${esc(t)}</option>`).join('')}</select></td>
        <td class="num"><label class="sr-only" for="ln-q-${i}">Cantidad de la partida ${i + 1}</label>
          <input class="input num q" id="ln-q-${i}" data-field="quantity" type="number" min="0.001" step="any" inputmode="decimal" value="${esc(l.quantity)}"> <span class="muted small">${p ? esc(unit(p.unit)) : ''}</span></td>
        <td class="num" data-cell="price">${p ? money(p.sellingPrice) : '—'}</td>
        <td class="num" data-cell="free">—</td>
        <td class="num" data-cell="total">—</td>
        <td><button class="btn btn-ghost btn-icon btn-sm" type="button" data-remove="${i}" aria-label="Quitar partida ${i + 1}" ${lines.length === 1 ? 'disabled' : ''}>${icon('x')}</button></td>
      </tr>`;
    }).join('');
    recalc();
  }

  function recalc() {
    let subtotal = 0;
    const over = [];
    $$('tr[data-i]', tbody).forEach((tr) => {
      const l = lines[Number(tr.dataset.i)];
      const p = product(l.productId);
      const q = Number(l.quantity) || 0;
      const total = p ? Math.round(p.sellingPrice * q * 100) / 100 : 0;
      subtotal += total;
      $('[data-cell=price]', tr).textContent = p ? money(p.sellingPrice) : '—';
      $('[data-cell=total]', tr).textContent = p && q ? money(total) : '—';
      const freeCell = $('[data-cell=free]', tr);
      if (p && form.warehouseId.value) {
        const free = freeByProduct.get(p.id) ?? 0;
        // Sum quantity of this product across lines to compare with free stock.
        const requested = lines.filter((x) => x.productId == p.id).reduce((s, x) => s + (Number(x.quantity) || 0), 0);
        const bad = requested > free;
        if (bad && !over.includes(p.productCode)) over.push(p.productCode);
        freeCell.innerHTML = `<span class="${bad ? 'free-bad' : 'free-ok'}">${num(free)}</span>`;
      } else freeCell.textContent = '—';
    });
    const r = rate();
    const tax = Math.round(subtotal * r * 100) / 100;
    const c = customer();
    $('#totals', root).innerHTML = `
      <dt>Subtotal</dt><dd>${money(subtotal)}</dd>
      <dt>IVA ${num(r * 100)}% ${c ? `<span class="small">(${esc(c.taxZone === 'FronteraNorte' ? 'Frontera Norte' : 'General')})</span>` : ''}</dt><dd>${money(tax)}</dd>
      <dt class="grand">Total</dt><dd class="grand">${money(subtotal + tax)}</dd>`;
    $('#stock-warn', root).innerHTML = over.length
      ? `<div class="note warn"><strong>Existencia insuficiente</strong> para ${esc(over.join(', '))}. Puede guardar el borrador, pero la confirmación fallará hasta que haya existencia libre.</div>` : '';
  }

  async function loadStock() {
    freeByProduct.clear();
    const wid = form.warehouseId.value;
    const sub = $('#lines-sub', root);
    if (!wid) { sub.textContent = 'Seleccione un almacén para ver la existencia libre'; recalc(); return; }
    sub.textContent = 'Consultando existencia…';
    try {
      const r = await get('/api/inventory', { warehouseId: wid, pageSize: 200 });
      r.items.forEach((i) => freeByProduct.set(i.productId, i.quantityFree));
      const w = warehouses.find((x) => x.id == wid);
      sub.textContent = `Existencia libre en ${w.code} · ${w.name}`;
    } catch (err) { sub.textContent = err.message; }
    recalc();
  }

  function showCustomer() {
    const c = customer();
    $('#cust-info', root).innerHTML = c ? `<div class="note ${c.taxZone === 'FronteraNorte' ? 'info' : ''}">
      <strong>${esc(c.name)}</strong> · RFC ${esc(c.rfc || 'no registrado')} · ${esc(c.city)}, ${esc(c.state)} · ${esc(TAX_ZONES[c.taxZone])} · Crédito ${money(c.creditLimit)}</div>` : '';
    // Suggest the warehouse in the customer's city when none is chosen yet.
    if (c && !form.warehouseId.value) {
      const w = warehouses.find((x) => x.city === c.city) || warehouses[0];
      if (w) { form.warehouseId.value = w.id; loadStock(); }
    }
    recalc();
  }

  tbody.addEventListener('change', (e) => {
    const tr = e.target.closest('tr[data-i]');
    if (!tr || !e.target.dataset.field) return;
    lines[Number(tr.dataset.i)][e.target.dataset.field] = e.target.value;
    if (e.target.dataset.field === 'productId') renderLines(); else recalc();
  });
  tbody.addEventListener('input', (e) => {
    const tr = e.target.closest('tr[data-i]');
    if (!tr || e.target.dataset.field !== 'quantity') return;
    lines[Number(tr.dataset.i)].quantity = e.target.value;
    recalc();
  });
  tbody.addEventListener('click', (e) => {
    const b = e.target.closest('[data-remove]');
    if (!b) return;
    lines.splice(Number(b.dataset.remove), 1);
    renderLines();
  });
  $('#add-line', root).addEventListener('click', () => {
    lines.push({ productId: '', quantity: '' });
    renderLines();
    $(`#ln-p-${lines.length - 1}`, root)?.focus();
  });
  form.customerId.addEventListener('change', showCustomer);
  form.warehouseId.addEventListener('change', loadStock);

  form.addEventListener('submit', async (e) => {
    e.preventDefault();
    clearErrors(form);
    const btn = form.querySelector('button[type=submit]');
    try {
      const v = formValues(form);
      const filled = lines.filter((l) => l.productId || l.quantity);
      const lineErr = filled.length === 0 ? 'Agregue al menos una partida.'
        : filled.some((l) => !l.productId) ? 'Seleccione el producto de cada partida.'
        : filled.some((l) => !(Number(l.quantity) > 0)) ? 'Cada partida necesita una cantidad mayor que cero.'
        : filled.some((l) => Number(l.quantity) > 1000000) ? 'La cantidad máxima por partida es 1,000,000.'
        : new Set(filled.map((l) => String(l.productId))).size !== filled.length ? 'Cada producto solo puede aparecer una vez por pedido; combine las cantidades en una sola partida.' : '';
      validate({
        customerId: [[v.customerId, 'Seleccione un cliente.']],
        warehouseId: [[v.warehouseId, 'Seleccione un almacén.']],
        externalReference: [[v.externalReference.length <= 60, 'Máximo 60 caracteres.']],
        notes: [[v.notes.length <= 500, 'Máximo 500 caracteres.']],
        lines: [[!lineErr, lineErr]]
      });
      btn.disabled = true;
      btn.innerHTML = '<span class="spinner" aria-hidden="true"></span> Creando pedido…';
      const order = await post('/api/sales-orders', {
        customerId: Number(v.customerId), warehouseId: Number(v.warehouseId), notes: v.notes || null,
        externalReference: v.externalReference || null,
        lines: filled.map((l) => ({ productId: Number(l.productId), quantity: Number(l.quantity) }))
      });
      invalidate('products');
      toast(`Pedido ${order.orderNumber} creado en borrador por ${money(order.total)}.`);
      navigate(`#/ventas/${order.id}`);
    } catch (err) {
      if (err.fieldErrors && !(err instanceof ValidationError)) {
        // Server line errors look like "Lines[0].Quantity"; show them under the lines table.
        const lineMsgs = Object.entries(err.fieldErrors).filter(([k]) => /^lines/i.test(k)).flatMap(([, m]) => m);
        if (lineMsgs.length) err.fieldErrors = { ...Object.fromEntries(Object.entries(err.fieldErrors).filter(([k]) => !/^lines/i.test(k))), lines: lineMsgs };
      }
      showErrors(form, err);
      if (!(err instanceof ValidationError)) form.querySelector('.form-alert').scrollIntoView({ block: 'center', behavior: 'smooth' });
      btn.disabled = false;
      btn.innerHTML = `${icon('check')} Crear pedido en borrador`;
    }
  });

  renderLines();
  if (form.customerId.value) showCustomer();
}

// ---------------- Detail ----------------
async function orderDetail(root, id, ctx) {
  root.innerHTML = '<div id="od"></div>';
  const box = $('#od', root);
  let lastError = null;

  async function render() {
    try {
      const o = await get(`/api/sales-orders/${id}`);
      setTitle(`Pedido ${o.orderNumber}`, 'Operación · Ventas');
      const steps = [
        ['Borrador', o.orderDate, true],
        ['Confirmado', o.confirmedAt, !!o.confirmedAt],
        ['Embarcado', o.shippedAt, !!o.shippedAt]
      ];
      const acts = [];
      if (ctx.canWrite && o.status === 'Draft') acts.push(`<button class="btn btn-primary" type="button" data-act="confirm">${icon('check')} Confirmar</button>`);
      if (ctx.canWrite && o.status === 'Confirmed') acts.push(`<button class="btn btn-primary" type="button" data-act="ship">${icon('truck')} Embarcar</button>`);
      if (ctx.canWrite && (o.status === 'Draft' || o.status === 'Confirmed')) acts.push(`<button class="btn btn-danger" type="button" data-act="cancel">${icon('ban')} Cancelar pedido</button>`);
      const hint = {
        Draft: 'Al confirmar se reserva la existencia de cada partida en el almacén. Si falta existencia en alguna, no se aplica ningún cambio.',
        Confirmed: 'Existencia reservada. Al embarcar se descuenta del almacén.',
        Shipped: 'Pedido embarcado; la existencia ya se descontó.',
        Cancelled: 'Pedido cancelado; cualquier reserva fue liberada.'
      }[o.status];

      box.innerHTML = `
        <div class="page-head">
          <div><a href="#/ventas" class="link-btn small">${icon('arrowLeft')} Pedidos</a>
            <h2 style="margin-top:6px;display:flex;gap:10px;align-items:center;flex-wrap:wrap"><span class="mono">${esc(o.orderNumber)}</span> ${statusChip(o.status)}</h2>
            <p>${esc(hint)}</p></div>
          <div class="page-actions">${acts.join('')}</div>
        </div>
        <div id="od-error">${lastError ? `<div class="note danger" role="alert" style="margin-bottom:16px"><strong>${esc(lastError.title)}:</strong> ${esc(lastError.message)}</div>` : ''}</div>
        <div class="grid grid-main">
          <div class="stack">
            <section class="card"><div class="card-body">
              <ol class="steps" aria-label="Avance del pedido">${steps.map(([n, d, done]) => `<li class="${done ? 'done' : ''}"><div class="s-name">${n}</div><div class="s-date">${done ? esc(fdatetime(d)) : 'Pendiente'}</div></li>`).join('')}
                ${o.status === 'Cancelled' ? '<li class="cancel"><div class="s-name">Cancelado</div><div class="s-date">Sin efecto en existencia</div></li>' : ''}</ol>
            </div></section>
            <section class="card"><div class="card-head"><h3>Partidas</h3><span class="muted small">${o.lines.length} partidas · surtido desde ${esc(o.warehouseName)}</span></div>
              <div class="table-wrap"><table class="data"><thead><tr><th>#</th><th>Producto</th><th class="num">Cantidad</th><th class="num">Precio unitario</th><th class="num">Importe</th></tr></thead><tbody>
                ${o.lines.map((l, i) => `<tr><td class="muted">${i + 1}</td><td><span class="mono">${esc(l.productCode)}</span><div class="cell-sub">${esc(l.productName)}</div></td>
                  <td class="num">${num(l.quantity)} <span class="muted">${esc(unit(l.unit))}</span></td><td class="num">${money(l.unitPrice)}</td><td class="num">${money(l.lineTotal)}</td></tr>`).join('')}
              </tbody><tfoot><tr><td colspan="4">Subtotal</td><td class="num">${money(o.subtotal)}</td></tr></tfoot></table></div>
            </section>
          </div>
          <div class="stack">
            <section class="card"><div class="card-head"><h3>Importes</h3></div><div class="card-body">
              <dl class="totals"><dt>Subtotal</dt><dd>${money(o.subtotal)}</dd><dt>IVA ${num(o.taxRate * 100)}%</dt><dd>${money(o.tax)}</dd>
                <dt class="grand">Total</dt><dd class="grand">${money(o.total)}</dd></dl></div></section>
            <section class="card"><div class="card-head"><h3>Datos del pedido</h3></div><div class="card-body">
              <dl class="dl">
                <dt>Cliente</dt><dd><a href="#/clientes/${o.customerId}">${esc(o.customerName)}</a></dd>
                <dt>RFC</dt><dd class="mono">${esc(o.customerRfc || '—')}</dd>
                <dt>Almacén</dt><dd>${esc(o.warehouseName)}</dd>
                <dt>Fecha</dt><dd>${esc(fdatetime(o.orderDate))}</dd>
                <dt>Ref. externa</dt><dd class="mono">${esc(o.externalReference || '—')}</dd>
                <dt>Notas</dt><dd>${esc(o.notes || '—')}</dd>
              </dl></div></section>
          </div>
        </div>`;

      const run = (path, title, message, okMsg, opts = {}) => confirmDialog({
        title, message, confirmLabel: opts.label || 'Confirmar', danger: opts.danger,
        action: async () => {
          try {
            const r = await post(`/api/sales-orders/${o.id}/${path}`);
            lastError = null;
            invalidate('products');
            toast(okMsg(r));
            ctx.refreshNotifications();
          } catch (err) {
            // Show the server's reason on the page (e.g. insufficient stock 422) and close the dialog.
            lastError = err;
            toastError(err);
          }
          render();
        }
      });
      $('[data-act=confirm]', box)?.addEventListener('click', () => run('confirm', 'Confirmar pedido',
        `Se reservará la existencia de las ${o.lines.length} partidas en <strong>${esc(o.warehouseName)}</strong>. ¿Confirmar el pedido <strong>${esc(o.orderNumber)}</strong> por ${money(o.total)}?`,
        (r) => `Pedido ${r.orderNumber} confirmado; existencia reservada.`, { label: 'Confirmar y reservar' }));
      $('[data-act=ship]', box)?.addEventListener('click', () => run('ship', 'Embarcar pedido',
        `Se descontará del almacén la existencia reservada. ¿Registrar el embarque de <strong>${esc(o.orderNumber)}</strong>?`,
        (r) => `Pedido ${r.orderNumber} embarcado.`, { label: 'Registrar embarque' }));
      $('[data-act=cancel]', box)?.addEventListener('click', () => run('cancel', 'Cancelar pedido',
        `¿Cancelar el pedido <strong>${esc(o.orderNumber)}</strong>?${o.status === 'Confirmed' ? ' Se liberará la existencia reservada.' : ''} Esta acción no se puede deshacer.`,
        (r) => `Pedido ${r.orderNumber} cancelado.`, { label: 'Cancelar pedido', danger: true }));
    } catch (err) {
      if (err.status === 401) return;
      box.innerHTML = `<div class="page-head"><div><a href="#/ventas" class="link-btn small">${icon('arrowLeft')} Pedidos</a></div></div><div class="card">${errorState(err, 'od-retry')}</div>`;
      $('#od-retry', box)?.addEventListener('click', render);
    }
  }
  box.innerHTML = `<div class="state"><span class="spinner"></span></div>`;
  await render();
}
