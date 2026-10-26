// HTTP client for the GlasFlow API: bearer token, ProblemDetails parsing, CSV downloads.
const SESSION_KEY = 'glasflow.session';

export const session = {
  get() {
    try {
      const s = JSON.parse(sessionStorage.getItem(SESSION_KEY) || 'null');
      if (s && s.expiresAt && new Date(s.expiresAt) <= new Date()) { this.clear(); return null; }
      return s;
    } catch { return null; }
  },
  set(value) { try { sessionStorage.setItem(SESSION_KEY, JSON.stringify(value)); } catch { /* storage blocked */ } },
  clear() { try { sessionStorage.removeItem(SESSION_KEY); } catch { /* storage blocked */ } }
};

const TITLES_ES = {
  400: 'Datos no válidos',
  401: 'Sesión no válida',
  403: 'Permiso denegado',
  404: 'No encontrado',
  409: 'Conflicto',
  422: 'Regla de negocio',
  429: 'Demasiadas solicitudes',
  500: 'Error del servidor',
  501: 'No disponible'
};

const STATUS_ES = { Draft: 'Borrador', Confirmed: 'Confirmado', Shipped: 'Embarcado', Cancelled: 'Cancelado', Planned: 'Planeada', InProgress: 'En proceso', Completed: 'Completada' };
const st = (s) => STATUS_ES[s] || s;
/** Spanish versions of the API's business-rule messages (the API answers in English). Unknown messages pass through. */
const MESSAGES_ES = [
  [/^Insufficient stock in warehouse for: (.+)$/, (m) => `Existencia libre insuficiente en el almacén para: ${m[1]}`],
  [/^Insufficient stock: requested ([\d.,-]+), free ([\d.,-]+)\.$/, (m) => `Existencia insuficiente: se solicitaron ${m[1]} y hay ${m[2]} libres.`],
  [/^Insufficient free stock in source warehouse: requested ([\d.,-]+), free ([\d.,-]+)\.$/, (m) => `Existencia libre insuficiente en el almacén de origen: se solicitaron ${m[1]} y hay ${m[2]} libres.`],
  [/^Cannot (\w+) order (\S+) in status (\w+); it must be (\w+)\.?$/, (m) => `No se puede ${{ confirm: 'confirmar', ship: 'embarcar', cancel: 'cancelar' }[m[1]] || m[1]} el pedido ${m[2]} en estado ${st(m[3])}; debe estar en ${st(m[4])}.`],
  [/^Cannot cancel an order in status (\w+)\.$/, (m) => `No se puede cancelar un pedido en estado ${st(m[1])}.`],
  [/^Cannot cancel a production order in status (\w+)\.$/, (m) => `No se puede cancelar una orden de producción en estado ${st(m[1])}.`],
  [/^An order with reference '(.+)' already exists \(duplicate submission\)\.$/, (m) => `Ya existe un pedido con la referencia «${m[1]}» (envío duplicado).`],
  [/^Customer code '(.+)' already exists\.$/, (m) => `Ya existe un cliente con el código «${m[1]}».`],
  [/^Product code '(.+)' already exists\.$/, (m) => `Ya existe un producto con el código «${m[1]}».`],
  [/^ProductCode '(.+)' already belongs to another product\.$/, (m) => `El código «${m[1]}» ya pertenece a otro producto.`],
  [/^External id '(.+)' is already mapped to another (customer|product)\.$/, (m) => `El ID externo «${m[1]}» ya está asignado a otro ${m[2] === 'customer' ? 'cliente' : 'producto'}.`],
  [/^Each product may appear only once per order/, () => 'Cada producto solo puede aparecer una vez por pedido; combine las cantidades.'],
  [/^Accessories are purchased, not produced/, () => 'Los accesorios se compran, no se producen; cree la orden para un producto de vidrio.'],
  [/^Selling price cannot be lower than cost\.$|^SellingPrice is lower than Cost\.$/, () => 'El precio de venta no puede ser menor que el costo.'],
  [/^Adjustment would leave less stock \((.+)\) than is reserved \((.+)\)\.$/, (m) => `El ajuste dejaría menos existencia (${m[1]}) que la reservada (${m[2]}).`],
  [/^Adjustment would leave negative stock \((.+)\)\.$/, (m) => `El ajuste dejaría existencia negativa (${m[1]}).`],
  [/^Adjustment quantity cannot be zero\.$/, () => 'La cantidad del ajuste no puede ser cero.'],
  [/^Source and destination warehouses must differ\.$/, () => 'El almacén de origen y el de destino deben ser distintos.'],
  [/^Completed quantity would exceed the planned (.+)\.$/, (m) => `La cantidad completada excedería lo planeado (${m[1]}).`],
  [/^Register at least one completed unit before completing the order\.$/, () => 'Registre al menos una unidad terminada antes de completar la orden.'],
  [/^Only planned orders can be started \(current: (\w+)\)\.$/, (m) => `Solo se pueden iniciar órdenes planeadas (estado actual: ${st(m[1])}).`],
  [/^Only in-progress orders can be completed \(current: (\w+)\)\.$/, (m) => `Solo se pueden completar órdenes en proceso (estado actual: ${st(m[1])}).`],
  [/^Production can only be registered while in progress \(current: (\w+)\)\.$/, (m) => `Solo se registra producción en órdenes en proceso (estado actual: ${st(m[1])}).`],
  [/^(Customer|Product|Warehouse) (\S+) is inactive\.$/, (m) => `${{ Customer: 'El cliente', Product: 'El producto', Warehouse: 'El almacén' }[m[1]]} ${m[2]} está inactivo.`],
  [/^Missing required columns: (.+)\.$/, (m) => `Faltan columnas obligatorias: ${m[1]}.`],
  [/^Only \.csv files are accepted\.$/, () => 'Solo se aceptan archivos .csv.'],
  [/^The file is empty\.$/, () => 'El archivo está vacío.'],
  [/^Unknown category '(.+?)'/, (m) => `Categoría desconocida «${m[1]}».`],
  [/^Unknown unit '(.+)'\.$/, (m) => `Unidad desconocida «${m[1]}».`],
  [/^Unknown supplier '(.+)'\.$/, (m) => `Proveedor desconocido «${m[1]}».`],
  [/^(\w+) '(.*)' is not a valid non-negative number\.$/, (m) => `${m[1]}: «${m[2]}» no es un número válido no negativo.`],
  [/^Duplicate ExternalId in file\.$/, () => 'ID externo duplicado en el archivo.'],
  [/^Quantity must be greater than zero\.$/, () => 'La cantidad debe ser mayor que cero.'],
  [/^The record was changed by another user/, () => 'Otro usuario modificó el registro. Recargue e intente de nuevo.'],
  [/^The change conflicts with existing data/, () => 'El cambio entra en conflicto con datos existentes (registro duplicado o referenciado).']
];
export function translate(message) {
  if (!message) return message;
  for (const [re, fn] of MESSAGES_ES) { const m = re.exec(message); if (m) return fn(m); }
  return message;
}

export class ApiError extends Error {
  constructor(status, problem, fallback) {
    const title = TITLES_ES[status] || problem?.title || 'Error';
    let message = translate(problem?.detail) || fallback || problem?.title || 'Ocurrió un error inesperado.';
    if (status === 403) message = 'Su rol no tiene permiso para realizar esta acción. Solicite acceso a un administrador.';
    if (status === 429) message = 'Se alcanzó el límite de solicitudes por minuto. Espere unos segundos e intente de nuevo.';
    if (status === 400 && problem?.errors && !problem.detail) message = 'Revise los campos marcados.';
    super(message);
    this.status = status;
    this.title = title;
    this.problem = problem || null;
    this.fieldErrors = problem?.errors || null;
  }
}

let onUnauthorized = () => {};
export function setUnauthorizedHandler(fn) { onUnauthorized = fn; }

function buildUrl(path, query) {
  const url = new URL(path, window.location.origin);
  if (query) {
    for (const [k, v] of Object.entries(query)) {
      if (v === undefined || v === null || v === '') continue;
      url.searchParams.set(k, String(v));
    }
  }
  return url.pathname + url.search;
}

async function send(path, { method = 'GET', body, query, form, auth = true } = {}) {
  const headers = { Accept: 'application/json' };
  const s = session.get();
  if (auth && s?.token) headers.Authorization = `Bearer ${s.token}`;
  let payload;
  if (form) payload = form;
  else if (body !== undefined) { headers['Content-Type'] = 'application/json'; payload = JSON.stringify(body); }
  let res;
  try {
    res = await fetch(buildUrl(path, query), { method, headers, body: payload });
  } catch {
    throw new ApiError(0, null, 'No se pudo conectar con el servidor. Verifique que la API esté en ejecución.');
  }
  if (res.status === 401 && auth) {
    session.clear();
    onUnauthorized();
    throw new ApiError(401, null, 'Su sesión expiró. Inicie sesión de nuevo.');
  }
  return res;
}

async function readProblem(res) {
  const text = await res.text().catch(() => '');
  if (!text) return null;
  try { return JSON.parse(text); } catch { return { detail: text.slice(0, 300) }; }
}

export async function api(path, options = {}) {
  const res = await send(path, options);
  if (!res.ok) throw new ApiError(res.status, await readProblem(res));
  if (res.status === 204) return null;
  const type = res.headers.get('content-type') || '';
  return type.includes('json') ? res.json() : res.text();
}

export const get = (path, query) => api(path, { query });
export const post = (path, body, query) => api(path, { method: 'POST', body, query });
export const put = (path, body) => api(path, { method: 'PUT', body });
export const del = (path) => api(path, { method: 'DELETE' });

function filenameFrom(disposition, fallback) {
  if (!disposition) return fallback;
  const star = /filename\*\s*=\s*UTF-8''([^;]+)/i.exec(disposition);
  if (star) { try { return decodeURIComponent(star[1].trim().replace(/"/g, '')); } catch { /* fall through */ } }
  const plain = /filename\s*=\s*"?([^";]+)"?/i.exec(disposition);
  return plain ? plain[1].trim() : fallback;
}

/** Downloads a file with the auth header and saves it with the server-provided filename. */
export async function download(path, query, fallbackName = 'glasflow.csv') {
  const res = await send(path, { query });
  if (!res.ok) throw new ApiError(res.status, await readProblem(res));
  const blob = await res.blob();
  const name = filenameFrom(res.headers.get('content-disposition'), fallbackName);
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url; a.download = name; a.style.display = 'none';
  document.body.appendChild(a); a.click(); a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 2000);
  return name;
}

export async function login(username, password) {
  const res = await send('/api/auth/login', { method: 'POST', body: { username, password }, auth: false });
  if (!res.ok) {
    const problem = await readProblem(res);
    if (res.status === 401) throw new ApiError(401, null, 'Usuario o contraseña incorrectos.');
    throw new ApiError(res.status, problem);
  }
  return res.json();
}

/** Small per-session cache for reference data that rarely changes. */
const cache = new Map();
export function cached(key, loader, ttlMs = 60000) {
  const hit = cache.get(key);
  if (hit && Date.now() - hit.at < ttlMs) return hit.promise;
  const promise = loader().catch((e) => { cache.delete(key); throw e; });
  cache.set(key, { at: Date.now(), promise });
  return promise;
}
export function invalidate(prefix) { for (const k of [...cache.keys()]) if (k.startsWith(prefix)) cache.delete(k); }

export const ref = {
  warehouses: () => cached('warehouses', () => get('/api/warehouses')),
  categories: () => cached('categories', () => get('/api/products/categories'), 600000),
  suppliers: () => cached('suppliers', () => get('/api/suppliers')),
  products: () => cached('products', () => get('/api/products', { pageSize: 200 }).then((r) => r.items)),
  customers: () => cached('customers', () => get('/api/customers', { pageSize: 200 }).then((r) => r.items))
};
