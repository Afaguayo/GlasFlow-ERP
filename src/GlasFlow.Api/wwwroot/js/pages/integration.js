import { api, get, download, invalidate, translate } from '../api.js';
import { icon } from '../icons.js';
import { esc, int, toast, toastError, load, $, $$ } from '../ui.js';

const FIELD_LABELS = {
  ExternalId: 'ID externo (clave del ERP)', ProductCode: 'Código de producto', ProductName: 'Nombre', Category: 'Categoría',
  Description: 'Descripción', Unit: 'Unidad', Cost: 'Costo', SellingPrice: 'Precio de venta', MinimumStock: 'Existencia mínima', SupplierCode: 'Código de proveedor'
};
const REQUIRED = ['ExternalId', 'ProductName', 'Category', 'SellingPrice'];

export default async function integration(root, { ctx }) {
  root.innerHTML = `
    <div class="page-head">
      <div><h2>Integración con ERP externo</h2><p>Intercambio de catálogos por CSV con el ERP corporativo. Los productos se emparejan por <strong>ID externo</strong>: si existe se actualiza, si no se crea.</p></div>
    </div>
    <div class="grid grid-main">
      <div class="stack">
        <section class="card" aria-labelledby="h-imp">
          <div class="card-head"><div><h3 id="h-imp">Importar catálogo de productos</h3><div class="sub">CSV con encabezados; máximo 5 MB</div></div></div>
          <div class="card-body stack">
            ${ctx.canWrite ? `
            <div class="drop" id="drop">
              ${icon('upload')}
              <div><label for="csv-file" class="link-btn">Seleccione un archivo .csv</label> o arrástrelo aquí</div>
              <input type="file" id="csv-file" accept=".csv,text/csv" class="sr-only">
              <div class="file-name" id="file-name">Ningún archivo seleccionado</div>
            </div>
            <div style="display:flex;gap:8px;flex-wrap:wrap">
              <button class="btn" type="button" id="btn-dry" disabled>${icon('check')} Validar (simulación)</button>
              <button class="btn btn-primary" type="button" id="btn-import" disabled>${icon('upload')} Importar y guardar</button>
              <button class="btn btn-ghost" type="button" id="btn-template">${icon('download')} Descargar plantilla</button>
            </div>
            <p class="small muted">Recomendado: valide primero. La simulación revisa cada fila con las mismas reglas pero no guarda cambios.</p>`
            : `<div class="note">Su rol es de solo lectura: puede descargar la plantilla y exportar, pero no importar.</div>
               <div><button class="btn" type="button" id="btn-template">${icon('download')} Descargar plantilla</button></div>`}
            <div id="imp-result" aria-live="polite"></div>
          </div>
        </section>
        <section class="card" aria-labelledby="h-map">
          <div class="card-head"><div><h3 id="h-map">Mapeo de columnas y categorías</h3><div class="sub">Encabezados aceptados del ERP externo (sin distinguir mayúsculas)</div></div></div>
          <div id="mapping"></div>
        </section>
      </div>
      <div class="stack">
        <section class="card" aria-labelledby="h-exp">
          <div class="card-head"><h3 id="h-exp">Exportar a ERP</h3></div>
          <div class="card-body stack">
            <p class="small muted">Archivos CSV UTF-8 listos para cargar en el ERP corporativo o abrir en Excel.</p>
            <button class="btn" type="button" data-export="/api/integration/products/export" data-name="glasflow-productos.csv">${icon('download')} Exportar productos</button>
            <button class="btn" type="button" data-export="/api/integration/customers/export" data-name="glasflow-clientes.csv">${icon('download')} Exportar clientes</button>
          </div>
        </section>
        <section class="card" aria-labelledby="h-how">
          <div class="card-head"><h3 id="h-how">Cómo funciona</h3></div>
          <div class="card-body"><ol class="small" style="margin:0;padding-left:18px;display:flex;flex-direction:column;gap:8px">
            <li>Descargue la plantilla con el formato del ERP de ejemplo.</li>
            <li>Columnas obligatorias: ID externo, nombre, categoría y precio.</li>
            <li>Las familias del ERP (<code>TEMPERED</code>, <code>HARDWARE</code>…) se traducen a las categorías de GlasFlow.</li>
            <li>Las unidades <code>sqm</code> y <code>ea</code> se convierten a m² y pza.</li>
            <li>Cada fila con error se reporta con su número de línea; las demás continúan.</li>
          </ol></div>
        </section>
      </div>
    </div>`;

  let file = null;
  const input = $('#csv-file', root);
  const setFile = (f) => {
    file = f || null;
    $('#file-name', root).textContent = file ? `${file.name} · ${int(Math.ceil(file.size / 1024))} KB` : 'Ningún archivo seleccionado';
    const okType = file && /\.csv$/i.test(file.name);
    $('#btn-dry', root).disabled = !okType;
    $('#btn-import', root).disabled = !okType;
    if (file && !okType) toast('Solo se aceptan archivos .csv.', 'warn');
    if (file && file.size > 5_000_000) toast('El archivo supera 5 MB; el servidor lo rechazará.', 'warn');
  };
  if (input) {
    input.addEventListener('change', () => setFile(input.files[0]));
    const drop = $('#drop', root);
    drop.addEventListener('dragover', (e) => { e.preventDefault(); drop.classList.add('over'); });
    drop.addEventListener('dragleave', () => drop.classList.remove('over'));
    drop.addEventListener('drop', (e) => { e.preventDefault(); drop.classList.remove('over'); setFile(e.dataTransfer.files[0]); });
    $('#btn-dry', root).addEventListener('click', (e) => runImport(true, e.currentTarget));
    $('#btn-import', root).addEventListener('click', (e) => runImport(false, e.currentTarget));
  }

  async function runImport(dryRun, btn) {
    if (!file) return;
    const label = btn.innerHTML;
    btn.disabled = true;
    btn.innerHTML = `<span class="spinner" aria-hidden="true"></span> ${dryRun ? 'Validando…' : 'Importando…'}`;
    const out = $('#imp-result', root);
    try {
      const fd = new FormData();
      fd.append('file', file, file.name);
      const r = await api('/api/integration/products/import', { method: 'POST', form: fd, query: { dryRun } });
      if (!dryRun) { invalidate('products'); ctx.refreshNotifications(); }
      const ok = r.errors.length === 0;
      out.innerHTML = `
        <div class="note ${ok ? 'ok' : 'warn'}" style="margin-bottom:12px"><strong>${r.dryRun ? 'Simulación completada' : 'Importación completada'}:</strong>
          ${r.dryRun ? 'no se guardó ningún cambio.' : 'los cambios se guardaron.'} ${ok ? 'Todas las filas son válidas.' : `${r.errors.length} filas con error.`}</div>
        <div class="stat-row">
          <div class="stat"><div class="s-label">Filas leídas</div><div class="s-value">${int(r.totalRows)}</div></div>
          <div class="stat"><div class="s-label">${r.dryRun ? 'Se crearían' : 'Creados'}</div><div class="s-value">${int(r.created)}</div></div>
          <div class="stat"><div class="s-label">${r.dryRun ? 'Se actualizarían' : 'Actualizados'}</div><div class="s-value">${int(r.updated)}</div></div>
          <div class="stat"><div class="s-label">Omitidos</div><div class="s-value">${int(r.skipped)}</div></div>
        </div>
        ${r.errors.length ? `<h4 style="margin:16px 0 8px;font-size:13px">Errores por fila</h4>
          <div class="table-wrap" style="border:1px solid var(--border);border-radius:var(--radius)"><table class="data"><thead><tr><th class="num">Línea</th><th>ID externo</th><th>Error</th></tr></thead><tbody>
          ${r.errors.map((e) => `<tr><td class="num">${e.lineNumber}</td><td class="mono">${esc(e.externalId || '—')}</td><td>${esc(translate(e.message))}</td></tr>`).join('')}</tbody></table></div>` : ''}
        ${r.dryRun && ok && ctx.canWrite ? '<p class="small muted" style="margin-top:12px">El archivo es válido. Pulse <strong>Importar y guardar</strong> para aplicar los cambios.</p>' : ''}`;
      toast(r.dryRun ? `Validación: ${r.created} nuevos, ${r.updated} a actualizar, ${r.errors.length} errores.` : `Importación: ${r.created} creados, ${r.updated} actualizados, ${r.errors.length} errores.`, r.errors.length ? 'warn' : 'success');
    } catch (err) {
      out.innerHTML = `<div class="note danger" role="alert"><strong>${esc(err.title)}:</strong> ${esc(err.message)}</div>`;
      toastError(err);
    } finally {
      btn.disabled = !file;
      btn.innerHTML = label;
    }
  }

  $('#btn-template', root).addEventListener('click', async (e) => {
    const b = e.currentTarget;
    b.disabled = true;
    try { const n = await download('/api/integration/products/template', null, 'erp-catalogo-ejemplo.csv'); toast(`Descargado ${n}.`); }
    catch (err) { toastError(err); } finally { b.disabled = false; }
  });
  $$('[data-export]', root).forEach((b) => b.addEventListener('click', async () => {
    b.disabled = true;
    try { const n = await download(b.dataset.export, null, b.dataset.name); toast(`Descargado ${n}.`); }
    catch (err) { toastError(err); } finally { b.disabled = false; }
  }));

  load($('#mapping', root), () => get('/api/integration/mapping'), (m) => {
    const cats = Object.entries(m.categories).filter(([k, v]) => k !== v);
    return `<div class="table-wrap"><table class="data"><thead><tr><th>Campo de GlasFlow</th><th>Encabezados aceptados</th><th>Obligatorio</th></tr></thead><tbody>
      ${Object.entries(m.columns).map(([k, heads]) => `<tr><td><strong>${esc(FIELD_LABELS[k] || k)}</strong><div class="cell-sub mono">${esc(k)}</div></td>
        <td>${heads.map((h) => `<code>${esc(h)}</code>`).join(' ')}</td><td>${REQUIRED.includes(k) ? '<span class="chip chip-accent plain">Sí</span>' : '<span class="muted">No</span>'}</td></tr>`).join('')}
    </tbody></table></div>
    <div class="table-wrap" style="border-top:1px solid var(--border)"><table class="data"><thead><tr><th>Familia en el ERP externo</th><th>Categoría en GlasFlow</th></tr></thead><tbody>
      ${cats.map(([k, v]) => `<tr><td><code>${esc(k)}</code></td><td>${esc(v)}</td></tr>`).join('')}
    </tbody></table></div>
    <div class="card-body small muted">También se aceptan los nombres de categoría de GlasFlow tal cual (p. ej. «Vidrio templado»).</div>`;
  });
}
