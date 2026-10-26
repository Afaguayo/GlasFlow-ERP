// GlasFlow ERP — application shell: hash router, session, top bar, notifications, theme.
import { session, login, get, post, setUnauthorizedHandler, invalidate } from './api.js';
import { icon, brandMark } from './icons.js';
import { esc, toast, toastError, relTime, ROLES, $, $$ } from './ui.js';

const NAV = [
  { group: 'Operación' },
  { path: 'dashboard', label: 'Panel de control', icon: 'dashboard', module: 'dashboard' },
  { path: 'inventario', label: 'Inventario', icon: 'box', module: 'inventory' },
  { path: 'ventas', label: 'Ventas', icon: 'cart', module: 'sales' },
  { path: 'clientes', label: 'Clientes', icon: 'users', module: 'customers' },
  { path: 'produccion', label: 'Producción', icon: 'factory', module: 'production' },
  { group: 'Análisis' },
  { path: 'reportes', label: 'Reportes', icon: 'report', module: 'reports' },
  { path: 'asistente', label: 'Asistente IA', icon: 'assistant', module: 'assistant' },
  { group: 'Sistema' },
  { path: 'integracion', label: 'Integración ERP', icon: 'plug', module: 'integration' },
  { path: 'configuracion', label: 'Configuración', icon: 'gear', module: 'settings' }
];
const ROUTES = Object.fromEntries(NAV.filter((n) => n.path).map((n) => [n.path, n]));

// ---------- Theme ----------
function currentTheme() { return document.documentElement.getAttribute('data-theme') === 'dark' ? 'dark' : 'light'; }
export function setTheme(t) {
  document.documentElement.setAttribute('data-theme', t);
  try { localStorage.setItem('glasflow.theme', t); } catch { /* storage blocked */ }
  $$('[data-theme-toggle]').forEach(updateThemeButton);
  window.dispatchEvent(new CustomEvent('themechange'));
}
function updateThemeButton(btn) {
  const dark = currentTheme() === 'dark';
  btn.innerHTML = icon(dark ? 'sun' : 'moon');
  btn.setAttribute('aria-label', dark ? 'Cambiar a tema claro' : 'Cambiar a tema oscuro');
  btn.title = dark ? 'Tema claro' : 'Tema oscuro';
}
function themeButton() { return `<button class="btn btn-ghost btn-icon" type="button" data-theme-toggle></button>`; }
document.addEventListener('click', (e) => {
  if (e.target.closest('[data-theme-toggle]')) setTheme(currentTheme() === 'dark' ? 'light' : 'dark');
});

// ---------- Routing ----------
function parseHash() {
  const raw = location.hash.replace(/^#\/?/, '');
  const [path, qs = ''] = raw.split('?');
  const segs = path.split('/').filter(Boolean).map(decodeURIComponent);
  return { segs, query: Object.fromEntries(new URLSearchParams(qs)) };
}
export function navigate(hash) { if (location.hash !== hash) location.hash = hash; else route(); }
/** Updates the query string of the current route without re-rendering. */
export function replaceQuery(query) {
  const { segs } = parseHash();
  const qs = new URLSearchParams(Object.entries(query).filter(([, v]) => v !== '' && v != null)).toString();
  history.replaceState(null, '', `#/${segs.join('/')}${qs ? '?' + qs : ''}`);
}

let cleanup = null;
let renderToken = 0;
const app = document.getElementById('app');
let ctx = null;

async function route() {
  const s = session.get();
  const { segs, query } = parseHash();
  if (!s) {
    if (segs[0] !== 'login') {
      const target = segs.length ? location.hash : '';
      try { sessionStorage.setItem('glasflow.returnTo', target); } catch { /* ignore */ }
      history.replaceState(null, '', '#/login');
    }
    teardown();
    renderLogin();
    return;
  }
  if (!segs.length || segs[0] === 'login') { history.replaceState(null, '', '#/dashboard'); return route(); }
  const entry = ROUTES[segs[0]];
  if (!document.querySelector('.shell')) renderShell(s);
  closeNav();
  closeNotifications();
  teardown();
  const main = $('#main');
  $$('.nav a').forEach((a) => {
    if (a.dataset.path === segs[0]) a.setAttribute('aria-current', 'page'); else a.removeAttribute('aria-current');
  });
  if (!entry) {
    setTitle('Página no encontrada');
    main.innerHTML = `<div class="card"><div class="state">${icon('alert')}<h4>La página solicitada no existe</h4><p><a href="#/dashboard">Volver al panel de control</a></p></div></div>`;
    return;
  }
  setTitle(entry.label, crumbFor(entry));
  const token = ++renderToken;
  main.innerHTML = '<div class="state" aria-busy="true"><span class="spinner"></span></div>';
  try {
    const mod = await import(`./pages/${entry.module}.js`);
    if (token !== renderToken) return;
    main.innerHTML = '';
    const result = await mod.default(main, { params: segs.slice(1), query, ctx });
    if (token !== renderToken) { if (typeof result === 'function') result(); return; }
    cleanup = typeof result === 'function' ? result : null;
    main.focus({ preventScroll: true });
    window.scrollTo(0, 0);
  } catch (err) {
    if (err?.status === 401) return;
    console.error(err);
    main.innerHTML = `<div class="card"><div class="state error">${icon('alert')}<h4>No se pudo cargar la página</h4><p>${esc(err.message)}</p></div></div>`;
  }
}
function teardown() { if (cleanup) { try { cleanup(); } catch { /* ignore */ } cleanup = null; } }
function crumbFor(entry) {
  let group = '';
  for (const n of NAV) { if (n.group) group = n.group; if (n === entry) break; }
  return group;
}
export function setTitle(title, crumb) {
  const t = $('#page-title'); if (t) t.textContent = title;
  if (crumb !== undefined) { const c = $('#page-crumb'); if (c) c.textContent = crumb; }
  document.title = `${title} · GlasFlow ERP`;
}

window.addEventListener('hashchange', route);
setUnauthorizedHandler(() => {
  stopPolling();
  toast('Su sesión expiró o no es válida. Inicie sesión de nuevo.', 'warn', 'Sesión finalizada');
  document.querySelector('.shell')?.remove();
  navigate('#/login');
});

// ---------- Login ----------
function renderLogin() {
  document.title = 'Iniciar sesión · GlasFlow ERP';
  app.innerHTML = `
  <main class="login" id="main">
    <section class="login-art" aria-label="GlasFlow ERP">
      <div class="brand">${brandMark()}<span class="brand-text"><span class="brand-name">Glas<b>Flow</b></span><span class="brand-sub">ERP industrial</span></span></div>
      <svg class="panes" viewBox="0 0 520 420" aria-hidden="true">
        <g fill="none" stroke-width="1.5">
          <rect x="150" y="40" width="240" height="320" rx="4" stroke="#2f4652" fill="#17232a"/>
          <rect x="210" y="80" width="240" height="320" rx="4" stroke="#3fb1aa" fill="rgba(63,177,170,.06)"/>
          <rect x="90" y="110" width="240" height="300" rx="4" stroke="#40525e"/>
          <path d="M250 330l150-150M250 270l90-90M290 360l140-140" stroke="#3fb1aa" stroke-opacity=".5"/>
        </g>
      </svg>
      <div class="login-copy">
        <h1>Inventario, ventas y producción de vidrio en un solo flujo.</h1>
        <p>Existencias por almacén, pedidos con IVA por zona fiscal, órdenes de producción, reportes e integración con ERP externo.</p>
        <div class="login-facts">
          <div><b>2 almacenes</b>Chihuahua · Cd. Juárez</div>
          <div><b>IVA 16% / 8%</b>General y Frontera Norte</div>
          <div><b>API REST</b>JWT · roles</div>
        </div>
      </div>
      <p class="login-legal">Vidrios del Norte es una empresa ficticia. Proyecto de portafolio; los datos son de demostración.</p>
    </section>
    <section class="login-form-wrap">
      <div class="login-theme">${themeButton()}</div>
      <form class="login-form" novalidate>
        <div><h2>Iniciar sesión</h2></div>
        <p class="lead">Ingrese con su usuario de GlasFlow.</p>
        <div class="form-alert" role="alert"></div>
        <div class="field"><label for="lg-user">Usuario</label><input class="input" id="lg-user" name="username" autocomplete="username" required></div>
        <div class="field"><label for="lg-pass">Contraseña</label><input class="input" id="lg-pass" name="password" type="password" autocomplete="current-password" required></div>
        <button class="btn btn-primary" type="submit">Entrar</button>
        <div class="demo-users">
          Cuentas de demostración (la contraseña se define con <code>Auth__DemoPassword</code>):
          <div class="row">
            <button class="btn btn-sm" type="button" data-user="admin">admin · Administrador</button>
            <button class="btn btn-sm" type="button" data-user="operador">operador · Operador</button>
            <button class="btn btn-sm" type="button" data-user="consulta">consulta · Consulta</button>
          </div>
        </div>
      </form>
    </section>
  </main>`;
  $$('[data-theme-toggle]').forEach(updateThemeButton);
  const form = $('.login-form');
  $$('[data-user]', form).forEach((b) => b.addEventListener('click', () => {
    form.username.value = b.dataset.user;
    form.password.focus();
  }));
  form.addEventListener('submit', async (e) => {
    e.preventDefault();
    const alert = $('.form-alert', form);
    alert.textContent = '';
    const username = form.username.value.trim();
    const password = form.password.value;
    if (!username || !password) { alert.textContent = 'Escriba su usuario y contraseña.'; (username ? form.password : form.username).focus(); return; }
    const btn = form.querySelector('button[type=submit]');
    btn.disabled = true; btn.innerHTML = '<span class="spinner" aria-hidden="true"></span> Entrando…';
    try {
      const res = await login(username, password);
      session.set(res);
      invalidate('');
      let back = '';
      try { back = sessionStorage.getItem('glasflow.returnTo') || ''; sessionStorage.removeItem('glasflow.returnTo'); } catch { /* ignore */ }
      app.innerHTML = '';
      navigate(back && !back.includes('login') ? back : '#/dashboard');
    } catch (err) {
      alert.textContent = err.message;
      btn.disabled = false; btn.textContent = 'Entrar';
      form.password.select();
    }
  });
  setTimeout(() => form.username.focus(), 0);
}

// ---------- Shell ----------
function renderShell(s) {
  const canWrite = s.role === 'Admin' || s.role === 'Operator';
  ctx = {
    user: s,
    canWrite,
    isAdmin: s.role === 'Admin',
    refreshNotifications: () => refreshBell(),
    setTitle
  };
  const initials = (s.displayName || s.username).split(/\s+/).map((w) => w[0]).slice(0, 2).join('').toUpperCase();
  app.innerHTML = `
  <div class="shell">
    <aside class="sidebar" id="sidebar" aria-label="Navegación principal">
      <a class="brand" href="#/dashboard">${brandMark()}<span class="brand-text"><span class="brand-name">Glas<b>Flow</b></span><span class="brand-sub">Vidrios del Norte</span></span></a>
      <nav class="nav">${NAV.map((n) => n.group
        ? `<div class="nav-label">${esc(n.group)}</div>`
        : `<a href="#/${n.path}" data-path="${n.path}">${icon(n.icon)}<span>${esc(n.label)}</span></a>`).join('')}</nav>
      <div class="side-foot"><strong>Vidrios del Norte</strong>Empresa ficticia · Chihuahua, México</div>
    </aside>
    <div class="backdrop" data-close-nav></div>
    <div class="main-col">
      <header class="topbar">
        <button class="btn btn-ghost btn-icon menu-btn" type="button" aria-label="Abrir menú" aria-controls="sidebar" aria-expanded="false" id="menu-btn">${icon('menu')}</button>
        <div class="topbar-title"><span class="crumb" id="page-crumb"></span><h1 id="page-title">GlasFlow</h1></div>
        <div class="topbar-actions">
          ${themeButton()}
          <div class="notif-wrap">
            <button class="btn btn-ghost btn-icon icon-btn" type="button" id="bell" aria-haspopup="true" aria-expanded="false" aria-controls="notif-panel" aria-label="Notificaciones">${icon('bell')}<span class="badge-count" id="bell-count" hidden></span></button>
            <div class="notif-panel" id="notif-panel" hidden role="region" aria-label="Notificaciones"></div>
          </div>
          <div class="user-chip">
            <span class="avatar" aria-hidden="true">${esc(initials)}</span>
            <span class="user-meta"><span class="name">${esc(s.displayName || s.username)}</span><span class="role">${esc(ROLES[s.role] || s.role)}</span></span>
            <button class="btn btn-ghost btn-icon" type="button" id="logout" aria-label="Cerrar sesión" title="Cerrar sesión">${icon('logout')}</button>
          </div>
        </div>
      </header>
      <main class="content" id="main" tabindex="-1"></main>
    </div>
  </div>`;
  $$('[data-theme-toggle]').forEach(updateThemeButton);
  $('#menu-btn').addEventListener('click', () => {
    const shell = $('.shell');
    const open = !shell.classList.contains('nav-open');
    shell.classList.toggle('nav-open', open);
    $('#menu-btn').setAttribute('aria-expanded', String(open));
    if (open) $('.nav a')?.focus();
  });
  $('[data-close-nav]').addEventListener('click', closeNav);
  $('#logout').addEventListener('click', () => {
    stopPolling();
    session.clear();
    invalidate('');
    document.querySelector('.shell')?.remove();
    toast('Sesión cerrada correctamente.', 'info', 'Hasta pronto');
    navigate('#/login');
  });
  $('#bell').addEventListener('click', toggleNotifications);
  startPolling();
}
function closeNav() {
  const shell = $('.shell');
  if (shell?.classList.contains('nav-open')) {
    shell.classList.remove('nav-open');
    $('#menu-btn')?.setAttribute('aria-expanded', 'false');
  }
}
document.addEventListener('keydown', (e) => {
  if (e.key !== 'Escape') return;
  if (!$('#notif-panel')?.hidden) { closeNotifications(); $('#bell')?.focus(); }
  closeNav();
});

// ---------- Notifications ----------
let pollTimer = null;
function startPolling() {
  stopPolling();
  refreshBell();
  pollTimer = setInterval(() => { if (document.visibilityState === 'visible') refreshBell(); }, 60000);
}
function stopPolling() { if (pollTimer) clearInterval(pollTimer); pollTimer = null; }

async function refreshBell() {
  try {
    const unread = await get('/api/notifications', { unreadOnly: true, take: 200 });
    const badge = $('#bell-count');
    if (!badge) return;
    badge.hidden = unread.length === 0;
    badge.textContent = unread.length > 99 ? '99+' : String(unread.length);
    $('#bell').setAttribute('aria-label', unread.length ? `Notificaciones: ${unread.length} sin leer` : 'Notificaciones');
    if (!$('#notif-panel').hidden) renderNotifications();
  } catch { /* the bell is non-critical; errors surface on the page itself */ }
}

const NOTIF_TYPES = { LowStock: 'Inventario bajo', OrderConfirmed: 'Pedido confirmado', OrderShipped: 'Pedido embarcado', ProductionCompleted: 'Producción completada', ImportCompleted: 'Importación' };

async function renderNotifications() {
  const panel = $('#notif-panel');
  if (!panel.firstChild) panel.innerHTML = '<div class="state"><span class="spinner"></span></div>';
  try {
    const list = await get('/api/notifications', { take: 30 });
    const unread = list.filter((n) => !n.isRead).length;
    panel.innerHTML = `
      <div class="notif-head"><h3>Notificaciones <span class="muted small">${unread} sin leer</span></h3>
        <button class="btn btn-sm" type="button" data-read-all ${unread ? '' : 'disabled'}>${icon('checkAll')} Marcar todo como leído</button></div>
      ${list.length ? `<ul class="notif-list">${list.map((n) => `
        <li class="notif-item ${n.isRead ? '' : 'unread'}">
          <span class="sev ${esc(n.severity)}" aria-hidden="true"></span>
          <div style="flex:1;min-width:0">
            <div class="n-title">${esc(n.title)}</div>
            <div class="n-msg">${esc(n.message)}</div>
            <div class="n-meta"><span>${esc(NOTIF_TYPES[n.type] || n.type)}</span><span>${esc(relTime(n.createdAt))}</span>
              ${n.severity === 'Critical' ? '<span class="chip chip-danger">Crítica</span>' : n.severity === 'Warning' ? '<span class="chip chip-warn">Advertencia</span>' : ''}
              ${n.type === 'LowStock' ? `<a href="#/inventario?low=1" class="link-btn" data-goto>Ver inventario</a>` : ''}
              ${n.isRead ? '' : `<button class="link-btn" type="button" data-read="${n.id}">Marcar como leída</button>`}</div>
          </div></li>`).join('')}</ul>` : `<div class="state">${icon('bell')}<h4>Sin notificaciones</h4><p>Las alertas de inventario y eventos aparecerán aquí.</p></div>`}`;
    panel.querySelector('[data-read-all]')?.addEventListener('click', async (e) => {
      e.currentTarget.disabled = true;
      try { const r = await post('/api/notifications/read-all'); toast(`${r.updated} notificaciones marcadas como leídas.`); refreshBell(); renderNotifications(); }
      catch (err) { toastError(err); e.currentTarget.disabled = false; }
    });
    $$('[data-read]', panel).forEach((b) => b.addEventListener('click', async () => {
      b.disabled = true;
      try { await post(`/api/notifications/${b.dataset.read}/read`); refreshBell(); renderNotifications(); }
      catch (err) { toastError(err); b.disabled = false; }
    }));
    $$('[data-goto]', panel).forEach((a) => a.addEventListener('click', closeNotifications));
  } catch (err) {
    panel.innerHTML = `<div class="state error">${icon('alert')}<h4>No se pudieron cargar</h4><p>${esc(err.message)}</p></div>`;
  }
}
function toggleNotifications() {
  const panel = $('#notif-panel');
  const open = panel.hidden;
  panel.hidden = !open;
  $('#bell').setAttribute('aria-expanded', String(open));
  if (open) { panel.innerHTML = ''; renderNotifications(); }
}
function closeNotifications() {
  const panel = $('#notif-panel');
  if (panel && !panel.hidden) { panel.hidden = true; $('#bell').setAttribute('aria-expanded', 'false'); }
}
document.addEventListener('click', (e) => {
  const panel = $('#notif-panel');
  if (panel && !panel.hidden && !e.target.closest('.notif-wrap')) closeNotifications();
});

route();
