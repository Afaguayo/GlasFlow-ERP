import { get, post, put, invalidate } from '../api.js';
import { icon } from '../icons.js';
import { navigate, setTitle } from '../app.js';
import {
  esc, money, int, fdate, fdatetime, statusChip, field, formValues, modal, validate, toast, load, pager, emptyState, debounce,
  errorState, TAX_ZONES, $, $$
} from '../ui.js';

const RFC_RE = /^[A-ZÑ&]{3,4}\d{6}[A-Z0-9]{3}$/;
const EMAIL_RE = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

export default async function customers(root, { params, ctx }) {
  if (params[0]) return detail(root, Number(params[0]), ctx);
  const s = { page: 1, search: '', active: '' };
  root.innerHTML = `
    <div class="page-head">
      <div><h2>Clientes</h2><p>Constructoras, vidrierías e instaladores. La zona fiscal define la tasa de IVA de sus pedidos.</p></div>
      <div class="page-actions">${ctx.canWrite ? `<button class="btn btn-primary" type="button" id="new-cust">${icon('plus')} Nuevo cliente</button>` : ''}</div>
    </div>
    <section class="card">
      <form class="toolbar" role="search" aria-label="Filtrar clientes" id="c-filters">
        <div class="field grow"><label for="c-search">Buscar</label><div class="input-icon">${icon('search')}<input class="input" id="c-search" name="search" placeholder="Nombre, código, RFC o ciudad"></div></div>
        ${field({ name: 'active', label: 'Estado', type: 'select', options: [['', 'Todos'], ['true', 'Activos'], ['false', 'Inactivos']] })}
      </form>
      <div id="c-table"></div>
    </section>`;
  const f = $('#c-filters', root);
  const box = $('#c-table', root);
  f.addEventListener('submit', (e) => e.preventDefault());
  const apply = () => { Object.assign(s, formValues(f), { page: 1 }); reload(); };
  f.search.addEventListener('input', debounce(apply, 300));
  f.active.addEventListener('change', apply);
  $('#new-cust', root)?.addEventListener('click', () => customerModal(null, (c) => navigate(`#/clientes/${c.id}`)));

  function reload() {
    load(box, () => get('/api/customers', { page: s.page, pageSize: 25, search: s.search, active: s.active }), (r) => {
      if (!r.items.length) return emptyState('Sin clientes', 'No hay clientes que coincidan con la búsqueda.', 'users');
      return `<div class="table-wrap"><table class="data"><thead><tr>
        <th>Cliente</th><th>RFC</th><th>Ciudad</th><th>Zona fiscal</th><th class="num">Límite de crédito</th><th>Contacto</th><th>Estado</th>
      </tr></thead><tbody>${r.items.map((c) => `
        <tr class="clickable" data-id="${c.id}">
          <td><a href="#/clientes/${c.id}" class="cell-main">${esc(c.name)}</a><div class="cell-sub mono">${esc(c.customerCode)}${c.externalId ? ` · ${esc(c.externalId)}` : ''}</div></td>
          <td class="mono">${esc(c.rfc || '—')}</td>
          <td>${esc(c.city)}<div class="cell-sub">${esc(c.state)}</div></td>
          <td>${taxChip(c.taxZone)}</td>
          <td class="num">${money(c.creditLimit)}</td>
          <td>${esc(c.contactName || '—')}<div class="cell-sub">${esc(c.email || '')}</div></td>
          <td>${c.active ? '<span class="chip chip-ok">Activo</span>' : '<span class="chip chip-cancelled">Inactivo</span>'}</td>
        </tr>`).join('')}</tbody></table></div>${pager(r, 'clientes')}`;
    }).then((r) => {
      if (!r) return;
      $$('[data-page]', box).forEach((b) => b.addEventListener('click', () => { s.page = Number(b.dataset.page); reload(); }));
      $$('tr[data-id]', box).forEach((tr) => tr.addEventListener('click', (e) => { if (!e.target.closest('a,button')) navigate(`#/clientes/${tr.dataset.id}`); }));
    });
  }
  reload();
}

function taxChip(zone) {
  return zone === 'FronteraNorte' ? '<span class="chip chip-accent plain">Frontera Norte · 8%</span>' : '<span class="chip plain">General · 16%</span>';
}

async function detail(root, id, ctx) {
  root.innerHTML = `<div id="cd"></div>`;
  const box = $('#cd', root);
  const render = async () => {
    try {
      box.innerHTML = `<div class="state"><span class="spinner"></span></div>`;
      const d = await get(`/api/customers/${id}`);
      const c = d.customer;
      setTitle(c.name, 'Operación · Clientes');
      box.innerHTML = `
      <div class="page-head">
        <div><a href="#/clientes" class="link-btn small">${icon('arrowLeft')} Clientes</a>
          <h2 style="margin-top:6px">${esc(c.name)}</h2>
          <p><span class="mono">${esc(c.customerCode)}</span> · ${esc(c.city)}, ${esc(c.state)} · ${c.active ? 'Activo' : 'Inactivo'}</p></div>
        <div class="page-actions">
          ${ctx.canWrite ? `<button class="btn" type="button" id="edit-c">${icon('edit')} Editar</button>
          <a class="btn btn-primary" href="#/ventas/nuevo?customerId=${c.id}">${icon('plus')} Nuevo pedido</a>` : ''}
        </div>
      </div>
      <div class="stat-row" style="margin-bottom:16px">
        <div class="stat"><div class="s-label">Ventas totales</div><div class="s-value">${money(d.totalSales)}</div></div>
        <div class="stat"><div class="s-label">Pedidos</div><div class="s-value">${int(d.orderCount)}</div></div>
        <div class="stat"><div class="s-label">Límite de crédito</div><div class="s-value">${money(c.creditLimit)}</div></div>
        <div class="stat"><div class="s-label">Tasa de IVA</div><div class="s-value">${c.taxZone === 'FronteraNorte' ? '8%' : '16%'}</div></div>
      </div>
      <div class="grid grid-main">
        <section class="card"><div class="card-head"><div><h3>Historial de pedidos</h3><div class="sub">Pedidos más recientes del cliente</div></div>
          <a class="btn btn-sm" href="#/ventas?customerId=${c.id}">Ver todos ${icon('arrowRight')}</a></div>
          ${d.recentOrders.length ? `<div class="table-wrap"><table class="data"><thead><tr><th>Pedido</th><th>Fecha</th><th>Almacén</th><th>Estado</th><th class="num">Partidas</th><th class="num">Subtotal</th><th class="num">IVA</th><th class="num">Total</th></tr></thead><tbody>
            ${d.recentOrders.map((o) => `<tr><td><a class="mono" href="#/ventas/${o.id}">${esc(o.orderNumber)}</a></td><td class="nowrap">${esc(fdate(o.orderDate))}</td><td class="mono">${esc(o.warehouseCode)}</td>
              <td>${statusChip(o.status)}</td><td class="num">${o.lineCount}</td><td class="num">${money(o.subtotal)}</td><td class="num">${money(o.tax)}</td><td class="num"><strong>${money(o.total)}</strong></td></tr>`).join('')}
          </tbody></table></div>` : emptyState('Sin pedidos', 'Este cliente aún no tiene pedidos.', 'cart')}
        </section>
        <section class="card"><div class="card-head"><h3>Datos fiscales y de contacto</h3></div><div class="card-body">
          <dl class="dl">
            <dt>RFC</dt><dd class="mono">${esc(c.rfc || '—')}</dd>
            <dt>Zona fiscal</dt><dd>${esc(TAX_ZONES[c.taxZone] || c.taxZone)}</dd>
            <dt>ID externo</dt><dd class="mono">${esc(c.externalId || '—')}</dd>
            <dt>Contacto</dt><dd>${esc(c.contactName || '—')}</dd>
            <dt>Correo</dt><dd>${c.email ? `<a href="mailto:${esc(c.email)}">${esc(c.email)}</a>` : '—'}</dd>
            <dt>Teléfono</dt><dd>${esc(c.phone || '—')}</dd>
            <dt>Dirección</dt><dd>${esc(c.address || '—')}${c.postalCode ? `, C.P. ${esc(c.postalCode)}` : ''}</dd>
            <dt>Ciudad</dt><dd>${esc(c.city)}, ${esc(c.state)}</dd>
            <dt>Alta</dt><dd>${esc(fdatetime(c.createdAt))}</dd>
            <dt>Actualizado</dt><dd>${esc(fdatetime(c.updatedAt))}</dd>
          </dl></div></section>
      </div>`;
      $('#edit-c', box)?.addEventListener('click', () => customerModal(c, render));
    } catch (err) {
      if (err.status === 401) return;
      box.innerHTML = `<div class="page-head"><div><a href="#/clientes" class="link-btn small">${icon('arrowLeft')} Clientes</a></div></div><div class="card">${errorState(err, 'cd-retry')}</div>`;
      $('#cd-retry', box)?.addEventListener('click', render);
    }
  };
  await render();
}

export function customerModal(c, done) {
  const isEdit = !!c;
  modal({
    title: isEdit ? `Editar cliente ${c.customerCode}` : 'Nuevo cliente',
    size: 'wide',
    submitLabel: isEdit ? 'Guardar cambios' : 'Crear cliente',
    body: `<div class="form-grid">
      ${field({ name: 'customerCode', label: 'Código de cliente', required: true, value: c?.customerCode ?? '', attrs: 'maxlength="20" autocomplete="off"', placeholder: 'CLI-013' })}
      ${field({ name: 'externalId', label: 'ID externo (ERP)', value: c?.externalId ?? '', attrs: 'maxlength="50"' })}
      ${field({ name: 'name', label: 'Razón social', required: true, value: c?.name ?? '', span: true, attrs: 'maxlength="150"' })}
      ${field({ name: 'rfc', label: 'RFC', value: c?.rfc ?? '', attrs: 'maxlength="13" autocomplete="off" style="text-transform:uppercase"', hint: 'Formato SAT: 3–4 letras, fecha AAMMDD y homoclave. Ej. VCJ850101AB1.' })}
      ${field({ name: 'taxZone', label: 'Zona fiscal', type: 'select', required: true, value: c?.taxZone ?? 'General', options: [['General', 'General · IVA 16%'], ['FronteraNorte', 'Frontera Norte · IVA 8%']],
        hint: 'Frontera Norte aplica el estímulo fiscal de IVA 8% (p. ej. Ciudad Juárez); el resto del país usa IVA general 16%.' })}
      ${field({ name: 'contactName', label: 'Contacto', value: c?.contactName ?? '', attrs: 'maxlength="100"' })}
      ${field({ name: 'email', label: 'Correo electrónico', type: 'email', value: c?.email ?? '', attrs: 'maxlength="150"' })}
      ${field({ name: 'phone', label: 'Teléfono', value: c?.phone ?? '', attrs: 'maxlength="30" inputmode="tel"' })}
      ${field({ name: 'creditLimit', label: 'Límite de crédito (MXN)', type: 'number', value: c?.creditLimit ?? 0, attrs: 'step="0.01" min="0" inputmode="decimal"' })}
      ${field({ name: 'address', label: 'Dirección', value: c?.address ?? '', span: true, attrs: 'maxlength="250"' })}
      ${field({ name: 'city', label: 'Ciudad', required: true, value: c?.city ?? '', attrs: 'maxlength="80"' })}
      ${field({ name: 'state', label: 'Estado', required: true, value: c?.state ?? 'Chihuahua', attrs: 'maxlength="80"' })}
      ${field({ name: 'postalCode', label: 'Código postal', value: c?.postalCode ?? '', attrs: 'maxlength="5" inputmode="numeric"' })}
      ${field({ name: 'active', label: 'Cliente activo', type: 'checkbox', value: c ? c.active : true })}
    </div>`,
    onSubmit: async (form) => {
      const v = formValues(form);
      v.rfc = v.rfc.toUpperCase();
      const credit = Number(v.creditLimit || 0);
      validate({
        customerCode: [[v.customerCode.length >= 2 && v.customerCode.length <= 20, 'El código debe tener de 2 a 20 caracteres.']],
        externalId: [[v.externalId.length <= 50, 'Máximo 50 caracteres.']],
        name: [[v.name.length >= 2 && v.name.length <= 150, 'La razón social debe tener de 2 a 150 caracteres.']],
        rfc: [[!v.rfc || RFC_RE.test(v.rfc), 'El RFC no tiene el formato del SAT (ej. VCJ850101AB1).']],
        email: [[!v.email || EMAIL_RE.test(v.email), 'Correo electrónico no válido.']],
        city: [[v.city.length > 0, 'La ciudad es obligatoria.']],
        state: [[v.state.length > 0, 'El estado es obligatorio.']],
        postalCode: [[!v.postalCode || /^\d{5}$/.test(v.postalCode), 'El código postal debe tener 5 dígitos.']],
        creditLimit: [[credit >= 0 && credit <= 100000000, 'El límite debe estar entre 0 y 100,000,000.']]
      });
      const body = {
        customerCode: v.customerCode, externalId: v.externalId || null, name: v.name, rfc: v.rfc || null, contactName: v.contactName || null,
        email: v.email || null, phone: v.phone || null, address: v.address || null, city: v.city, state: v.state,
        postalCode: v.postalCode || null, taxZone: v.taxZone, creditLimit: credit, active: v.active
      };
      const saved = isEdit ? await put(`/api/customers/${c.id}`, body) : await post('/api/customers', body);
      invalidate('customers');
      toast(isEdit ? `Cliente ${saved.customerCode} actualizado.` : `Cliente ${saved.customerCode} creado.`);
      done?.(saved);
    }
  });
}
