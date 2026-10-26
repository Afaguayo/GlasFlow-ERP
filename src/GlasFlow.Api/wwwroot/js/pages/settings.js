import { get } from '../api.js';
import { icon } from '../icons.js';
import { setTheme } from '../app.js';
import { esc, int, fdatetime, load, errorState, ROLES, $ } from '../ui.js';

const ENV_VARS = [
  ['ConnectionStrings__GlasFlow', 'Cadena de conexión de SQL Server.', 'Server=localhost\\SQLEXPRESS;Database=GlasFlow;Trusted_Connection=True;TrustServerCertificate=True'],
  ['ConnectionStrings__GlasFlowSqlite', 'Archivo de SQLite cuando Database__Provider=Sqlite.', 'Data Source=glasflow.db'],
  ['Database__Provider', 'Use Sqlite para una demo sin SQL Server (crea el archivo y datos de ejemplo al iniciar).', 'Sqlite'],
  ['Auth__JwtKey', 'Clave para firmar los tokens JWT (mínimo 32 caracteres). Obligatoria fuera de Development.', '<cadena aleatoria de 32+ caracteres>'],
  ['Auth__DemoPassword', 'Contraseña de las cuentas demo admin, operador y consulta.', '<contraseña demo>'],
  ['Ai__Provider', 'Proveedor del asistente: anthropic, openai o none (motor local determinista).', 'anthropic'],
  ['Ai__ApiKey / GLASFLOW_AI_API_KEY', 'Clave API del proveedor de IA. Nunca se expone al navegador.', '<clave del proveedor>'],
  ['Ai__Model', 'Modelo a utilizar con el proveedor configurado.', '<id del modelo>'],
  ['Ai__BaseUrl', 'URL base para proveedores compatibles con OpenAI (o un gateway local).', 'https://api.openai.com/v1'],
  ['Automation__InventoryCheckMinutes', 'Cada cuántos minutos corre la revisión automática de inventario bajo.', '15'],
  ['Inventory__AllowNegativeStock', 'Permite que los ajustes dejen existencia negativa (por defecto false).', 'false']
];

export default async function settings(root, { ctx }) {
  root.innerHTML = `
    <div class="page-head">
      <div><h2>Configuración</h2><p>Estado del sistema, base de datos, proveedor de IA y automatizaciones. Los secretos nunca se muestran.</p></div>
      <div class="page-actions"><button class="btn" type="button" id="st-refresh">${icon('refresh')} Actualizar</button></div>
    </div>
    <div id="st-body"><div class="state"><span class="spinner"></span></div></div>`;
  $('#st-refresh', root).addEventListener('click', render);

  async function render() {
    const body = $('#st-body', root);
    body.innerHTML = '<div class="state" aria-busy="true"><span class="spinner"></span></div>';
    let status, health;
    try {
      [status, health] = await Promise.all([
        get('/api/system/status'),
        fetch('/health', { headers: { Accept: 'application/json' } }).then(async (r) => ({ httpStatus: r.status, ...(await r.json().catch(() => ({ status: r.ok ? 'Healthy' : 'Unhealthy' }))) }))
          .catch(() => ({ status: 'Unreachable', checks: [] }))
      ]);
    } catch (err) {
      if (err.status === 401) return;
      body.innerHTML = `<div class="card">${errorState(err, 'st-retry')}</div>`;
      $('#st-retry', body)?.addEventListener('click', render);
      return;
    }
    const a = status.application, db = status.database, ai = status.ai, au = status.automation;
    const healthy = health.status === 'Healthy';
    const counts = db.counts ? [
      ['Clientes', db.counts.customers], ['Productos', db.counts.products], ['Pedidos de venta', db.counts.salesOrders],
      ['Órdenes de producción', db.counts.productionOrders], ['Movimientos', db.counts.movements], ['Bitácora', db.counts.auditLogs]
    ] : [];
    const theme = document.documentElement.getAttribute('data-theme') === 'dark' ? 'dark' : 'light';

    body.innerHTML = `
      <div class="grid grid-3">
        <section class="card"><div class="card-head"><h3>${icon('gear')} Aplicación</h3><span class="chip chip-ok">En línea</span></div><div class="card-body">
          <dl class="dl"><dt>Nombre</dt><dd>${esc(a.name)}</dd><dt>Versión</dt><dd class="mono">${esc(a.version)}</dd><dt>Entorno</dt><dd>${esc(a.environment)}</dd>
            <dt>Runtime</dt><dd>${esc(a.runtime)}</dd><dt>Hora del servidor</dt><dd>${esc(fdatetime(a.serverTimeUtc))}</dd></dl></div></section>
        <section class="card"><div class="card-head"><h3>${icon('heart')} Health check</h3>${healthy ? '<span class="chip chip-ok">Healthy</span>' : `<span class="chip chip-danger">${esc(health.status)}</span>`}</div><div class="card-body">
          <dl class="dl"><dt>Endpoint</dt><dd><code>GET /health</code></dd><dt>Estado HTTP</dt><dd class="mono">${esc(health.httpStatus ?? '—')}</dd>
            ${(health.checks || []).map((c) => `<dt>${esc(c.name)}</dt><dd>${esc(c.status)}${c.description ? `<div class="cell-sub">${esc(c.description)}</div>` : ''}</dd>`).join('')}</dl></div></section>
        <section class="card"><div class="card-head"><h3>${icon('shield')} Sesión y apariencia</h3></div><div class="card-body stack">
          <dl class="dl"><dt>Usuario</dt><dd>${esc(ctx.user.displayName)} <span class="muted mono">(${esc(ctx.user.username)})</span></dd><dt>Rol</dt><dd>${esc(ROLES[ctx.user.role] || ctx.user.role)}</dd>
            <dt>Token expira</dt><dd>${esc(fdatetime(ctx.user.expiresAt))}</dd></dl>
          <div class="field"><label for="theme-sel">Tema de la interfaz</label><select class="input" id="theme-sel"><option value="light" ${theme === 'light' ? 'selected' : ''}>Claro</option><option value="dark" ${theme === 'dark' ? 'selected' : ''}>Oscuro</option></select></div>
        </div></section>
      </div>

      <div class="grid grid-main mt-16">
        <section class="card"><div class="card-head"><div><h3>${icon('database')} Base de datos</h3><div class="sub">${esc(db.provider)}</div></div>${db.connected ? '<span class="chip chip-ok">Conectada</span>' : '<span class="chip chip-danger">Sin conexión</span>'}</div>
          <div class="card-body stack">
            <dl class="dl"><dt>Proveedor</dt><dd>${esc(db.provider)}</dd><dt>Servidor / archivo</dt><dd class="mono">${esc(db.server || '—')}</dd><dt>Base de datos</dt><dd class="mono">${esc(db.databaseName || '—')}</dd></dl>
            ${counts.length ? `<div class="stat-row">${counts.map(([l, v]) => `<div class="stat"><div class="s-label">${esc(l)}</div><div class="s-value">${int(v)}</div></div>`).join('')}</div>` : ''}
            <div><h4 style="font-size:13px;margin-bottom:8px">Objetos SQL (procedimientos, vistas, triggers)</h4>
              ${db.sqlObjects.length ? `<div class="table-wrap" style="border:1px solid var(--border);border-radius:var(--radius)"><table class="data"><thead><tr><th>Objeto</th><th>Tipo</th></tr></thead><tbody>
                ${db.sqlObjects.map((o) => `<tr><td class="mono">${esc(o.name)}</td><td>${esc(sqlType(o.type))}</td></tr>`).join('')}</tbody></table></div>`
                : `<div class="note">${db.provider === 'SQLite' ? 'Con SQLite no se instalan procedimientos almacenados, vistas ni triggers; esos objetos existen al ejecutar con SQL Server.' : 'No se encontraron objetos SQL instalados.'}</div>`}</div>
          </div></section>
        <div class="stack">
          <section class="card"><div class="card-head"><h3>${icon('assistant')} Asistente IA</h3>${ai.hasApiKey ? '<span class="chip chip-ok">Clave configurada</span>' : '<span class="chip plain">Sin clave</span>'}</div><div class="card-body">
            <dl class="dl"><dt>Proveedor configurado</dt><dd>${esc(ai.configuredProvider || 'none')}</dd><dt>Motor activo</dt><dd>${esc(ai.active)}</dd>
              <dt>Clave API</dt><dd>${ai.hasApiKey ? 'Configurada (oculta)' : 'No configurada'}</dd></dl>
            ${ai.hasApiKey ? '' : '<p class="small muted" style="margin-top:12px">Sin clave, el asistente responde con el motor local determinista a partir de los mismos resúmenes.</p>'}</div></section>
          <section class="card"><div class="card-head"><h3>${icon('clock')} Automatización</h3></div><div class="card-body">
            <dl class="dl"><dt>Revisión de inventario</dt><dd>Cada ${int(au.inventoryCheckMinutes)} min</dd>
              <dt>Última ejecución programada</dt><dd>${au.lastScheduledRun ? esc(fdatetime(au.lastScheduledRun)) : 'Aún no se ejecuta en esta sesión del servidor'}</dd>
              <dt>Existencia negativa</dt><dd>${au.allowNegativeStock ? 'Permitida' : 'No permitida'}</dd>
              <dt>Panel de control</dt><dd>Se actualiza cada 60 s mientras está visible</dd></dl></div></section>
        </div>
      </div>

      <section class="card mt-16"><div class="card-head"><div><h3>${icon('key')} Variables de entorno</h3><div class="sub">Configure el servidor con variables de entorno o <code>appsettings.json</code> (el doble guion bajo equivale a «:»).</div></div></div>
        <div class="table-wrap"><table class="data"><thead><tr><th>Variable</th><th>Descripción</th><th>Ejemplo</th></tr></thead><tbody>
          ${ENV_VARS.map(([k, d, e]) => `<tr><td class="code-cell"><code>${esc(k)}</code></td><td>${esc(d)}</td><td><code>${esc(e)}</code></td></tr>`).join('')}
        </tbody></table></div>
        <div class="card-body"><pre class="block">$env:Database__Provider = "Sqlite"
$env:Auth__DemoPassword = "&lt;contraseña demo&gt;"
$env:Ai__Provider = "anthropic"; $env:GLASFLOW_AI_API_KEY = "&lt;clave&gt;"
dotnet run --project src/GlasFlow.Api</pre></div>
      </section>

      ${ctx.isAdmin ? `<section class="card mt-16"><div class="card-head"><div><h3>${icon('file')} Bitácora de auditoría</h3><div class="sub">Últimos 50 eventos (aplicación y triggers de base de datos)</div></div></div><div id="audit"></div></section>` : ''}`;

    $('#theme-sel', body).addEventListener('change', (e) => setTheme(e.target.value));
    if (ctx.isAdmin) {
      load($('#audit', body), () => get('/api/system/audit', { take: 50 }), (rows) => rows.length ? `<div class="table-wrap"><table class="data"><thead><tr><th>Fecha</th><th>Usuario</th><th>Acción</th><th>Entidad</th><th>Detalle</th><th>Origen</th></tr></thead><tbody>
        ${rows.map((r) => `<tr><td class="nowrap">${esc(fdatetime(r.timestamp))}</td><td>${esc(r.userName)}</td><td><span class="tag">${esc(r.action)}</span></td>
          <td>${esc(r.entityType)}${r.entityId ? ` <span class="mono muted">#${esc(r.entityId)}</span>` : ''}</td><td>${esc(r.details || '—')}</td>
          <td>${r.source === 'DB_TRIGGER' ? '<span class="chip chip-info plain">Trigger SQL</span>' : '<span class="chip plain">Aplicación</span>'}</td></tr>`).join('')}</tbody></table></div>`
        : '<div class="state"><h4>Sin eventos</h4></div>');
    }
  }
  await render();
}

function sqlType(t) {
  return { SQL_STORED_PROCEDURE: 'Procedimiento almacenado', VIEW: 'Vista', SQL_TRIGGER: 'Trigger' }[t] || t;
}
