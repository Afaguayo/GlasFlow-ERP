import { get, post, put, del, ref, invalidate } from '../api.js';
import { icon } from '../icons.js';
import { replaceQuery } from '../app.js';
import {
  esc, money, num, fdatetime, isoDay, stockChip, unit, field, formValues, modal, confirmDialog, validate, toast, toastError,
  load, pager, emptyState, debounce, options, MOVEMENT_TYPES, UNITS, $, $$
} from '../ui.js';

const TABS = [['existencias', 'Existencias'], ['productos', 'Productos'], ['movimientos', 'Movimientos']];

export default async function inventory(root, { query, ctx }) {
  let tab = TABS.some(([k]) => k === query.tab) ? query.tab : 'existencias';
  const [warehouses, categories] = await Promise.all([ref.warehouses(), ref.categories()]);
  const state = {
    stock: { page: 1, search: query.search || '', category: '', warehouseId: '', lowStockOnly: query.low === '1' },
    products: { page: 1, search: '', category: '', active: 'true', lowStockOnly: false },
    moves: { page: 1, productId: query.productId || '', warehouseId: '', movementType: '', from: '', to: '' }
  };

  root.innerHTML = `
    <div class="page-head">
      <div><h2>Inventario</h2><p>Existencias por producto y almacén, catálogo de productos e historial de movimientos.</p></div>
      <div class="page-actions" id="inv-actions"></div>
    </div>
    <div class="tabs" role="tablist" aria-label="Secciones de inventario">
      ${TABS.map(([k, l]) => `<button class="tab" role="tab" type="button" id="tab-${k}" aria-controls="panel" data-tab="${k}">${l}</button>`).join('')}
    </div>
    <div id="panel" role="tabpanel"></div>`;

  const panel = $('#panel', root);
  const actions = $('#inv-actions', root);

  function selectTab(k) {
    tab = k;
    $$('[data-tab]', root).forEach((b) => {
      b.setAttribute('aria-selected', String(b.dataset.tab === k));
      b.tabIndex = b.dataset.tab === k ? 0 : -1;
    });
    panel.setAttribute('aria-labelledby', `tab-${k}`);
    replaceQuery(k === 'existencias' ? {} : { tab: k });
    actions.innerHTML = '';
    if (k === 'existencias') renderStockTab();
    else if (k === 'productos') renderProductsTab();
    else renderMovesTab();
  }
  $$('[data-tab]', root).forEach((b) => b.addEventListener('click', () => selectTab(b.dataset.tab)));
  root.querySelector('[role=tablist]').addEventListener('keydown', (e) => {
    if (e.key !== 'ArrowRight' && e.key !== 'ArrowLeft') return;
    const i = TABS.findIndex(([k]) => k === tab);
    const next = TABS[(i + (e.key === 'ArrowRight' ? 1 : TABS.length - 1)) % TABS.length][0];
    selectTab(next); $(`#tab-${next}`, root).focus();
  });

  const whOptions = (empty) => options(warehouses, 'id', (w) => `${w.code} · ${w.name}`, empty);
  const catOptions = (empty) => [['', empty], ...categories.map((c) => [c, c])];

  // ---------------- Existencias ----------------
  function renderStockTab() {
    const s = state.stock;
    if (ctx.canWrite) {
      actions.innerHTML = `<button class="btn" type="button" data-act="transfer">${icon('transfer')} Transferencia</button>
        <button class="btn btn-primary" type="button" data-act="adjust">${icon('adjust')} Ajuste de inventario</button>`;
      $('[data-act=adjust]', actions).addEventListener('click', () => adjustModal(null, reloadStock));
      $('[data-act=transfer]', actions).addEventListener('click', () => transferModal(null, reloadStock));
    }
    panel.innerHTML = `<section class="card">
      <form class="toolbar" role="search" aria-label="Filtrar existencias" id="stock-filters">
        <div class="field grow"><label for="st-search">Buscar</label><div class="input-icon">${icon('search')}<input class="input" id="st-search" name="search" placeholder="Código o nombre de producto" value="${esc(s.search)}"></div></div>
        ${field({ name: 'category', label: 'Categoría', type: 'select', value: s.category, options: catOptions('Todas las categorías') })}
        ${field({ name: 'warehouseId', label: 'Almacén', type: 'select', value: s.warehouseId, options: whOptions('Todos los almacenes') })}
        <label class="switch"><input type="checkbox" name="lowStockOnly" ${s.lowStockOnly ? 'checked' : ''}> Solo inventario bajo</label>
      </form>
      <div id="stock-table"></div></section>`;
    const f = $('#stock-filters', panel);
    const apply = () => { Object.assign(s, formValues(f), { page: 1 }); reloadStock(); };
    f.addEventListener('submit', (e) => e.preventDefault());
    f.search.addEventListener('input', debounce(apply, 300));
    [f.category, f.warehouseId, f.lowStockOnly].forEach((el) => el.addEventListener('change', apply));
    reloadStock();
  }

  function reloadStock() {
    const s = state.stock;
    const box = $('#stock-table', panel);
    if (!box) return;
    load(box, () => get('/api/inventory', { page: s.page, pageSize: 25, search: s.search, category: s.category, warehouseId: s.warehouseId, lowStockOnly: s.lowStockOnly || undefined }), (r) => {
      if (!r.items.length) return emptyState('Sin existencias para estos filtros', 'Pruebe con otra búsqueda o quite el filtro de inventario bajo.', 'box');
      return `<div class="table-wrap"><table class="data"><thead><tr>
        <th style="min-width:220px">Producto</th><th>Categoría</th><th>Almacén</th><th class="num">Disponible</th><th class="num">Reservado</th><th class="num">Libre</th><th class="num">Mínimo</th><th>Estado</th><th class="num" title="Valor a costo">Valor</th>${ctx.canWrite ? '<th><span class="sr-only">Acciones</span></th>' : ''}
      </tr></thead><tbody>${r.items.map((x) => `
        <tr class="${x.isLowStock ? (x.quantityFree <= 0 ? 'row-out' : 'row-low') : ''}">
          <td><span class="mono cell-main">${esc(x.productCode)}</span><div class="cell-sub">${esc(x.productName)}</div></td>
          <td>${esc(x.category)}</td>
          <td><span class="mono">${esc(x.warehouseCode)}</span></td>
          <td class="num">${num(x.quantityAvailable)} <span class="muted">${esc(unit(x.unit))}</span></td>
          <td class="num">${num(x.quantityReserved)}</td>
          <td class="num"><strong>${num(x.quantityFree)}</strong></td>
          <td class="num">${num(x.minimumStock)}</td>
          <td>${stockChip(x)}</td>
          <td class="num">${money(x.stockValue)}</td>
          ${ctx.canWrite ? `<td><div class="row-actions">
            <button class="btn btn-sm" type="button" data-adjust="${x.inventoryId}" aria-label="Ajustar ${esc(x.productCode)} en ${esc(x.warehouseCode)}">${icon('adjust')} Ajustar</button>
            <button class="btn btn-sm" type="button" data-transfer="${x.inventoryId}" aria-label="Transferir ${esc(x.productCode)} desde ${esc(x.warehouseCode)}">${icon('transfer')} Transferir</button></div></td>` : ''}
        </tr>`).join('')}</tbody></table></div>${pager(r, 'partidas')}`;
    }).then((r) => {
      if (!r) return;
      $$('[data-page]', box).forEach((b) => b.addEventListener('click', () => { s.page = Number(b.dataset.page); reloadStock(); }));
      $$('[data-adjust]', box).forEach((b) => b.addEventListener('click', () => adjustModal(r.items.find((i) => i.inventoryId == b.dataset.adjust), reloadStock)));
      $$('[data-transfer]', box).forEach((b) => b.addEventListener('click', () => transferModal(r.items.find((i) => i.inventoryId == b.dataset.transfer), reloadStock)));
    });
  }

  async function productOptions(empty) {
    const products = await ref.products();
    return options(products.filter((p) => p.active), 'id', (p) => `${p.productCode} · ${p.productName}`, empty);
  }

  async function stockFor(productId, warehouseId) {
    if (!productId || !warehouseId) return null;
    const products = await ref.products();
    const p = products.find((x) => x.id == productId);
    const r = await get('/api/inventory', { search: p?.productCode, warehouseId, pageSize: 50 });
    return r.items.find((i) => i.productId == productId) || { quantityAvailable: 0, quantityReserved: 0, quantityFree: 0, unit: p?.unit, minimumStock: p?.minimumStock };
  }

  async function adjustModal(row, done) {
    const prodOpts = await productOptions('Seleccione un producto');
    modal({
      title: 'Ajuste de inventario',
      submitLabel: 'Registrar ajuste',
      body: `<p class="muted small">Registra un movimiento de tipo <strong>Ajuste</strong> (conteo cíclico, merma, rotura). Use una cantidad positiva para sumar existencia y negativa para restarla.</p>
        <div class="form-grid">
          ${field({ name: 'productId', label: 'Producto', type: 'select', required: true, value: row?.productId ?? '', options: prodOpts, span: true })}
          ${field({ name: 'warehouseId', label: 'Almacén', type: 'select', required: true, value: row?.warehouseId ?? '', options: whOptions('Seleccione un almacén') })}
          ${field({ name: 'quantity', label: 'Cantidad (con signo)', type: 'number', required: true, attrs: 'step="any" inputmode="decimal"', placeholder: 'p. ej. -3 o 12', hint: 'Negativa resta, positiva suma.' })}
          ${field({ name: 'reason', label: 'Motivo', type: 'textarea', required: true, span: true, placeholder: 'p. ej. Conteo cíclico semanal: diferencia por rotura en maniobra', attrs: 'maxlength="200"' })}
        </div>
        <div class="note" id="adj-stock">Seleccione producto y almacén para ver la existencia actual.</div>`,
      onOpen: (form) => {
        const show = async () => {
          const box = $('#adj-stock', form);
          if (!form.productId.value || !form.warehouseId.value) return;
          box.textContent = 'Consultando existencia…';
          try {
            const st = await stockFor(form.productId.value, form.warehouseId.value);
            form.dataset.available = st.quantityAvailable; form.dataset.reserved = st.quantityReserved;
            box.innerHTML = `Existencia actual: <strong>${num(st.quantityAvailable)} ${esc(unit(st.unit))}</strong> disponibles · ${num(st.quantityReserved)} reservadas · <strong>${num(st.quantityFree)}</strong> libres (mínimo ${num(st.minimumStock)}).`;
          } catch (err) { box.textContent = err.message; }
        };
        const reset = () => { delete form.dataset.available; delete form.dataset.reserved; show(); };
        form.productId.addEventListener('change', reset);
        form.warehouseId.addEventListener('change', reset);
        show();
      },
      onSubmit: async (form) => {
        const v = formValues(form);
        const q = Number(v.quantity);
        validate({
          productId: [[v.productId, 'Seleccione un producto.']],
          warehouseId: [[v.warehouseId, 'Seleccione un almacén.']],
          quantity: [[v.quantity !== '' && !isNaN(q), 'Escriba una cantidad.'], [q !== 0, 'La cantidad no puede ser cero.'], [Math.abs(q) <= 1000000, 'Máximo ±1,000,000.'],
            [form.dataset.available === undefined || Number(form.dataset.available) + q >= Number(form.dataset.reserved), `El ajuste dejaría menos existencia que la reservada (${num(Number(form.dataset.reserved || 0))}).`]],
          reason: [[v.reason.length >= 3, 'Describa el motivo (mínimo 3 caracteres).'], [v.reason.length <= 200, 'Máximo 200 caracteres.']]
        });
        const st = await post('/api/inventory/adjustments', { productId: Number(v.productId), warehouseId: Number(v.warehouseId), quantity: q, reason: v.reason });
        invalidate('products');
        toast(`Ajuste registrado. ${st.productCode} en ${st.warehouseCode}: ${num(st.quantityAvailable)} disponibles.`);
        done?.();
      }
    });
  }

  async function transferModal(row, done) {
    const prodOpts = await productOptions('Seleccione un producto');
    modal({
      title: 'Transferencia entre almacenes',
      submitLabel: 'Transferir',
      body: `<p class="muted small">Genera una salida en el almacén de origen y una entrada en el destino. Solo se puede transferir existencia libre (no reservada).</p>
        <div class="form-grid">
          ${field({ name: 'productId', label: 'Producto', type: 'select', required: true, value: row?.productId ?? '', options: prodOpts, span: true })}
          ${field({ name: 'fromWarehouseId', label: 'Almacén de origen', type: 'select', required: true, value: row?.warehouseId ?? '', options: whOptions('Seleccione origen') })}
          ${field({ name: 'toWarehouseId', label: 'Almacén de destino', type: 'select', required: true, value: row ? (warehouses.find((w) => w.id !== row.warehouseId)?.id ?? '') : '', options: whOptions('Seleccione destino') })}
          ${field({ name: 'quantity', label: 'Cantidad', type: 'number', required: true, attrs: 'step="any" min="0.001" inputmode="decimal"' })}
          ${field({ name: 'notes', label: 'Notas', placeholder: 'Opcional', attrs: 'maxlength="200"' })}
        </div>
        <div class="note" id="tr-stock">Seleccione producto y almacén de origen para ver la existencia libre.</div>`,
      onOpen: (form) => {
        const show = async () => {
          const box = $('#tr-stock', form);
          if (!form.productId.value || !form.fromWarehouseId.value) return;
          box.textContent = 'Consultando existencia…';
          try {
            const st = await stockFor(form.productId.value, form.fromWarehouseId.value);
            form.dataset.free = st.quantityFree;
            box.innerHTML = `Existencia libre en origen: <strong>${num(st.quantityFree)} ${esc(unit(st.unit))}</strong> (${num(st.quantityReserved)} reservadas).`;
          } catch (err) { box.textContent = err.message; }
        };
        const reset = () => { delete form.dataset.free; show(); };
        form.productId.addEventListener('change', reset);
        form.fromWarehouseId.addEventListener('change', reset);
        show();
      },
      onSubmit: async (form) => {
        const v = formValues(form);
        const q = Number(v.quantity);
        const free = form.dataset.free !== undefined ? Number(form.dataset.free) : Infinity;
        validate({
          productId: [[v.productId, 'Seleccione un producto.']],
          fromWarehouseId: [[v.fromWarehouseId, 'Seleccione el almacén de origen.']],
          toWarehouseId: [[v.toWarehouseId, 'Seleccione el almacén de destino.'], [v.toWarehouseId !== v.fromWarehouseId, 'El destino debe ser distinto del origen.']],
          quantity: [[q > 0, 'La cantidad debe ser mayor que cero.'], [q <= 1000000, 'Máximo 1,000,000.'], [q <= free, `Solo hay ${num(free)} libres en el origen.`]]
        });
        await post('/api/inventory/transfers', { productId: Number(v.productId), fromWarehouseId: Number(v.fromWarehouseId), toWarehouseId: Number(v.toWarehouseId), quantity: q, notes: v.notes || null });
        toast('Transferencia registrada en ambos almacenes.');
        done?.();
      }
    });
  }

  // ---------------- Productos ----------------
  function renderProductsTab() {
    const s = state.products;
    if (ctx.canWrite) {
      actions.innerHTML = `<button class="btn btn-primary" type="button" data-act="new">${icon('plus')} Nuevo producto</button>`;
      $('[data-act=new]', actions).addEventListener('click', () => productModal(null));
    }
    panel.innerHTML = `<section class="card">
      <form class="toolbar" role="search" aria-label="Filtrar productos" id="prod-filters">
        <div class="field grow"><label for="pr-search">Buscar</label><div class="input-icon">${icon('search')}<input class="input" id="pr-search" name="search" placeholder="Código, nombre o ID externo" value="${esc(s.search)}"></div></div>
        ${field({ name: 'category', label: 'Categoría', type: 'select', value: s.category, options: catOptions('Todas las categorías') })}
        ${field({ name: 'active', label: 'Estado', type: 'select', value: s.active, options: [['true', 'Activos'], ['false', 'Inactivos'], ['', 'Todos']] })}
        <label class="switch"><input type="checkbox" name="lowStockOnly" ${s.lowStockOnly ? 'checked' : ''}> Solo inventario bajo</label>
      </form><div id="prod-table"></div></section>`;
    const f = $('#prod-filters', panel);
    const apply = () => { Object.assign(s, formValues(f), { page: 1 }); reloadProducts(); };
    f.addEventListener('submit', (e) => e.preventDefault());
    f.search.addEventListener('input', debounce(apply, 300));
    [f.category, f.active, f.lowStockOnly].forEach((el) => el.addEventListener('change', apply));
    reloadProducts();
  }

  let productRows = [];
  function reloadProducts() {
    const s = state.products;
    const box = $('#prod-table', panel);
    if (!box) return;
    load(box, () => get('/api/products', { page: s.page, pageSize: 25, search: s.search, category: s.category, active: s.active, lowStockOnly: s.lowStockOnly || undefined }), (r) => {
      productRows = r.items;
      if (!r.items.length) return emptyState('No hay productos con estos filtros', ctx.canWrite ? 'Cree un producto o importe un catálogo desde Integración ERP.' : '', 'box');
      return `<div class="table-wrap"><table class="data"><thead><tr>
        <th class="w-prod">Producto</th><th>Categoría</th><th class="num">Costo</th><th class="num">Precio</th><th class="num">Margen</th><th class="num">Mínimo</th><th class="num">Existencia</th><th>Proveedor</th><th>Estado</th>${ctx.canWrite ? '<th><span class="sr-only">Acciones</span></th>' : ''}
      </tr></thead><tbody>${r.items.map((p) => {
        const margin = p.sellingPrice > 0 ? (p.sellingPrice - p.cost) / p.sellingPrice : 0;
        return `<tr class="${p.isLowStock ? 'row-low' : ''}">
          <td><span class="mono cell-main">${esc(p.productCode)}</span><div class="cell-sub">${esc(p.productName)}${p.externalId ? ` · <span class="mono">${esc(p.externalId)}</span>` : ''}</div></td>
          <td>${esc(p.category)}</td>
          <td class="num">${money(p.cost)}</td><td class="num">${money(p.sellingPrice)}</td><td class="num">${num(margin * 100)}%</td>
          <td class="num">${num(p.minimumStock)}</td>
          <td class="num">${num(p.totalAvailable)} <span class="muted">${esc(unit(p.unit))}</span>${p.totalReserved ? `<div class="cell-sub">${num(p.totalReserved)} reservado</div>` : ''}</td>
          <td class="truncate" title="${esc(p.supplierName || '')}">${esc(p.supplierName || '—')}</td>
          <td>${p.active ? (p.isLowStock ? '<span class="chip chip-warn">Bajo mínimo</span>' : '<span class="chip chip-ok">Activo</span>') : '<span class="chip chip-cancelled">Inactivo</span>'}</td>
          ${ctx.canWrite ? `<td><div class="row-actions">
            <button class="btn btn-sm" type="button" data-edit="${p.id}" aria-label="Editar ${esc(p.productCode)}">${icon('edit')} Editar</button>
            ${ctx.isAdmin && p.active ? `<button class="btn btn-sm btn-icon btn-danger" type="button" data-deact="${p.id}" title="Desactivar producto" aria-label="Desactivar ${esc(p.productCode)}">${icon('ban')}</button>` : ''}
          </div></td>` : ''}</tr>`;
      }).join('')}</tbody></table></div>${pager(r, 'productos')}`;
    }).then((r) => {
      if (!r) return;
      $$('[data-page]', box).forEach((b) => b.addEventListener('click', () => { s.page = Number(b.dataset.page); reloadProducts(); }));
      $$('[data-edit]', box).forEach((b) => b.addEventListener('click', () => productModal(productRows.find((p) => p.id == b.dataset.edit))));
      $$('[data-deact]', box).forEach((b) => b.addEventListener('click', () => {
        const p = productRows.find((x) => x.id == b.dataset.deact);
        confirmDialog({
          title: 'Desactivar producto', danger: true, confirmLabel: 'Desactivar',
          message: `¿Desactivar <strong>${esc(p.productCode)} · ${esc(p.productName)}</strong>? El producto conserva su historial (pedidos y movimientos) pero ya no podrá venderse ni producirse. Puede reactivarlo editándolo.`,
          action: async () => { await del(`/api/products/${p.id}`); invalidate('products'); toast(`${p.productCode} desactivado.`); reloadProducts(); }
        });
      }));
    });
  }

  async function productModal(p) {
    const suppliers = await ref.suppliers();
    const isEdit = !!p;
    modal({
      title: isEdit ? `Editar producto ${p.productCode}` : 'Nuevo producto',
      size: 'wide',
      submitLabel: isEdit ? 'Guardar cambios' : 'Crear producto',
      body: `<div class="form-grid">
        ${field({ name: 'productCode', label: 'Código', required: true, value: p?.productCode ?? '', attrs: 'maxlength="30" autocomplete="off"', hint: '2 a 30 caracteres, único.' })}
        ${field({ name: 'externalId', label: 'ID externo (ERP)', value: p?.externalId ?? '', attrs: 'maxlength="50"', hint: 'Clave en el ERP de origen, p. ej. SKU-20112.' })}
        ${field({ name: 'productName', label: 'Nombre', required: true, value: p?.productName ?? '', span: true, attrs: 'maxlength="150"' })}
        ${field({ name: 'category', label: 'Categoría', type: 'select', required: true, value: p?.category ?? '', options: catOptions('Seleccione una categoría') })}
        ${field({ name: 'unit', label: 'Unidad de medida', type: 'select', required: true, value: p?.unit ?? 'm2', options: Object.entries(UNITS).map(([k, l]) => [k, `${l} (${k})`]) })}
        ${field({ name: 'cost', label: 'Costo unitario (MXN)', type: 'number', required: true, value: p?.cost ?? 0, attrs: 'step="0.01" min="0" inputmode="decimal"' })}
        ${field({ name: 'sellingPrice', label: 'Precio de venta (MXN, sin IVA)', type: 'number', required: true, value: p?.sellingPrice ?? '', attrs: 'step="0.01" min="0.01" inputmode="decimal"' })}
        ${field({ name: 'minimumStock', label: 'Existencia mínima', type: 'number', required: true, value: p?.minimumStock ?? 0, attrs: 'step="any" min="0" inputmode="decimal"', hint: 'Por almacén; dispara alertas de inventario bajo.' })}
        ${field({ name: 'supplierId', label: 'Proveedor', type: 'select', value: p?.supplierId ?? '', options: options(suppliers.filter((s) => s.active || s.id === p?.supplierId), 'id', (s) => `${s.supplierCode} · ${s.name}`, 'Sin proveedor') })}
        ${field({ name: 'description', label: 'Descripción', type: 'textarea', value: p?.description ?? '', span: true, attrs: 'maxlength="500"' })}
        ${field({ name: 'active', label: 'Producto activo', type: 'checkbox', value: p ? p.active : true, span: true })}
      </div>`,
      onSubmit: async (form) => {
        const v = formValues(form);
        const cost = Number(v.cost), price = Number(v.sellingPrice), min = Number(v.minimumStock);
        validate({
          productCode: [[v.productCode.length >= 2 && v.productCode.length <= 30, 'El código debe tener de 2 a 30 caracteres.']],
          externalId: [[v.externalId.length <= 50, 'Máximo 50 caracteres.']],
          productName: [[v.productName.length >= 2 && v.productName.length <= 150, 'El nombre debe tener de 2 a 150 caracteres.']],
          category: [[categories.includes(v.category), 'Seleccione una categoría.']],
          unit: [[/^(m2|pza|ml|kg)$/.test(v.unit), 'Unidad no válida.']],
          cost: [[v.cost !== '' && cost >= 0 && cost <= 10000000, 'El costo debe estar entre 0 y 10,000,000.']],
          sellingPrice: [[price >= 0.01 && price <= 10000000, 'El precio debe estar entre 0.01 y 10,000,000.'], [price >= cost, 'El precio de venta no puede ser menor que el costo.']],
          minimumStock: [[v.minimumStock !== '' && min >= 0 && min <= 10000000, 'El mínimo debe estar entre 0 y 10,000,000.']],
          description: [[v.description.length <= 500, 'Máximo 500 caracteres.']]
        });
        const body = {
          productCode: v.productCode, externalId: v.externalId || null, productName: v.productName, category: v.category,
          description: v.description || null, unit: v.unit, cost, sellingPrice: price, minimumStock: min, active: v.active,
          supplierId: v.supplierId ? Number(v.supplierId) : null
        };
        const saved = isEdit ? await put(`/api/products/${p.id}`, body) : await post('/api/products', body);
        invalidate('products');
        toast(isEdit ? `Producto ${saved.productCode} actualizado.` : `Producto ${saved.productCode} creado.`);
        if (tab === 'productos') reloadProducts();
      }
    });
  }

  // ---------------- Movimientos ----------------
  async function renderMovesTab() {
    const s = state.moves;
    const prodOpts = await productOptions('Todos los productos').catch(() => [['', 'Todos los productos']]);
    panel.innerHTML = `<section class="card">
      <form class="toolbar" aria-label="Filtrar movimientos" id="mv-filters">
        ${field({ name: 'productId', label: 'Producto', type: 'select', value: s.productId, options: prodOpts })}
        ${field({ name: 'warehouseId', label: 'Almacén', type: 'select', value: s.warehouseId, options: whOptions('Todos') })}
        ${field({ name: 'movementType', label: 'Tipo', type: 'select', value: s.movementType, options: [['', 'Todos los tipos'], ...Object.entries(MOVEMENT_TYPES)] })}
        ${field({ name: 'from', label: 'Desde', type: 'date', value: s.from, attrs: `max="${isoDay()}"` })}
        ${field({ name: 'to', label: 'Hasta', type: 'date', value: s.to })}
        <button class="btn btn-ghost" type="button" id="mv-clear">Limpiar filtros</button>
      </form><div id="mv-table"></div></section>`;
    const f = $('#mv-filters', panel);
    const apply = () => { Object.assign(s, formValues(f), { page: 1 }); reloadMoves(); };
    f.addEventListener('submit', (e) => e.preventDefault());
    $$('select, input', f).forEach((el) => el.addEventListener('change', apply));
    $('#mv-clear', f).addEventListener('click', () => { f.reset(); $$('select', f).forEach((x) => { x.value = ''; }); $$('input', f).forEach((x) => { x.value = ''; }); apply(); });
    reloadMoves();
  }

  function reloadMoves() {
    const s = state.moves;
    const box = $('#mv-table', panel);
    if (!box) return;
    if (s.from && s.to && s.from > s.to) { box.innerHTML = `<div class="card-body"><div class="note warn">La fecha inicial es posterior a la final.</div></div>`; return; }
    load(box, () => get('/api/inventory/movements', { page: s.page, pageSize: 25, productId: s.productId, warehouseId: s.warehouseId, movementType: s.movementType, from: s.from, to: s.to }), (r) => {
      if (!r.items.length) return emptyState('Sin movimientos', 'No hay movimientos que coincidan con los filtros.', 'layers');
      return `<div class="table-wrap"><table class="data"><thead><tr><th>Fecha</th><th>Tipo</th><th class="w-prod">Producto</th><th>Almacén</th><th class="num">Cantidad</th><th>Referencia</th><th>Notas</th><th>Usuario</th></tr></thead><tbody>
        ${r.items.map((m) => `<tr>
          <td class="nowrap">${esc(fdatetime(m.timestamp))}</td>
          <td><span class="tag">${esc(MOVEMENT_TYPES[m.movementType] || m.movementType)}</span></td>
          <td><span class="mono">${esc(m.productCode)}</span><div class="cell-sub">${esc(m.productName)}</div></td>
          <td class="mono">${esc(m.warehouseCode)}</td>
          <td class="num ${m.quantity >= 0 ? 'qty-pos' : 'qty-neg'}">${m.quantity > 0 ? '+' : ''}${num(m.quantity)}</td>
          <td class="mono">${esc(m.reference)}</td>
          <td>${esc(m.notes || '—')}</td>
          <td>${esc(m.userName)}</td></tr>`).join('')}
      </tbody></table></div>${pager(r, 'movimientos')}`;
    }).then((r) => {
      if (!r) return;
      $$('[data-page]', box).forEach((b) => b.addEventListener('click', () => { s.page = Number(b.dataset.page); reloadMoves(); }));
    });
  }

  selectTab(tab);
}
