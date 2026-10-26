import { get, post } from '../api.js';
import { icon } from '../icons.js';
import { esc, ftime, $, $$ } from '../ui.js';

// Conversation is kept in memory for the browser session so navigating away and back keeps it.
const history = [];

const INTENTS = {
  LowStock: 'Tema: inventario bajo', SalesSummary: 'Tema: ventas', ProductionStatus: 'Tema: producción',
  TopMovers: 'Tema: productos con más movimiento', DailyBriefing: 'Tema: prioridades del día'
};

export default async function assistant(root) {
  root.innerHTML = `
    <div class="page-head">
      <div><h2>Asistente IA</h2><p>Preguntas de negocio en lenguaje natural sobre inventario, ventas y producción.</p></div>
    </div>
    <div class="grid grid-main">
      <section class="card chat" aria-label="Conversación con el asistente">
        <div class="chat-log" id="chat-log" role="log" aria-live="polite" aria-relevant="additions"></div>
        <form class="chat-input" id="chat-form" novalidate>
          <div class="field">
            <label for="q" class="sr-only">Pregunta para el asistente</label>
            <textarea class="input" id="q" name="question" rows="1" maxlength="500" placeholder="Escriba una pregunta, p. ej. ¿Qué debería revisar hoy?" aria-describedby="q-help"></textarea>
            <span class="hint" id="q-help"><span id="q-count">0</span>/500 · Enter para enviar, Shift+Enter para salto de línea</span>
          </div>
          <button class="btn btn-primary" type="submit" id="send">${icon('send')} Enviar</button>
        </form>
      </section>
      <div class="stack">
        <section class="card" aria-labelledby="h-sug">
          <div class="card-head"><h3 id="h-sug">Preguntas sugeridas</h3></div>
          <div class="card-body"><div class="suggestions" id="suggestions"><span class="spinner"></span></div></div>
        </section>
        <section class="card" aria-labelledby="h-scope">
          <div class="card-head"><h3 id="h-scope">Alcance y privacidad</h3></div>
          <div class="card-body stack small">
            <p>El asistente <strong>solo ve resúmenes predefinidos de solo lectura</strong> que prepara el servidor (inventario bajo, ventas del mes, producción pendiente, movimientos y prioridades). No consulta la base de datos directamente ni puede modificar información.</p>
            <p>Si hay un proveedor de IA configurado (Anthropic u OpenAI), redacta la respuesta a partir de esos resúmenes. Sin proveedor o si falla, responde un motor local determinista con los mismos datos.</p>
            <p class="muted">Cada respuesta incluye los datos utilizados para que pueda verificarla.</p>
          </div>
        </section>
      </div>
    </div>`;

  const log = $('#chat-log', root);
  const form = $('#chat-form', root);
  const q = form.question;

  const renderAll = () => {
    log.innerHTML = history.length ? history.map(renderMsg).join('') : `<div class="state" id="chat-empty">${icon('assistant')}<h4>¿En qué le ayudo?</h4><p>Elija una pregunta sugerida o escriba la suya.</p></div>`;
    log.scrollTop = log.scrollHeight;
  };

  function renderMsg(m) {
    if (m.role === 'user') return `<div class="msg user"><div class="bubble">${esc(m.text)}</div><div class="meta">${esc(ftime(m.at))}</div></div>`;
    if (m.pending) return `<div class="msg bot"><div class="bubble"><span class="spinner" aria-hidden="true"></span> <span class="muted">Consultando datos…</span></div></div>`;
    if (m.error) return `<div class="msg bot err"><div class="bubble">${esc(m.error)}</div></div>`;
    const r = m.res;
    return `<div class="msg bot">
      <div class="bubble">${esc(r.answer.replace(/\r\n/g, '\n'))}</div>
      <div class="meta">
        <span class="chip chip-accent plain">${icon('cpu')} ${esc(r.provider)}</span>
        ${r.usedFallback ? '<span class="chip plain">respuesta local determinista</span>' : '<span class="chip chip-info plain">generada por IA</span>'}
        <span>${esc(INTENTS[r.intent] || r.intent)}</span><span>·</span><span>${esc(ftime(r.generatedAt))}</span>
      </div>
      ${r.data != null ? `<details class="data-used"><summary>Datos utilizados</summary><pre>${esc(JSON.stringify(r.data, null, 2))}</pre></details>` : ''}
    </div>`;
  }

  async function ask(text) {
    const question = text.trim();
    if (question.length < 3) { q.setAttribute('aria-invalid', 'true'); q.focus(); return; }
    q.removeAttribute('aria-invalid');
    history.push({ role: 'user', text: question, at: new Date() });
    const pending = { role: 'bot', pending: true };
    history.push(pending);
    renderAll();
    setBusy(true);
    try {
      const res = await post('/api/assistant/ask', { question });
      Object.assign(pending, { pending: false, res });
    } catch (err) {
      Object.assign(pending, { pending: false, error: err.status === 400 ? 'La pregunta debe tener entre 3 y 500 caracteres.' : err.message });
    }
    if (root.isConnected) { renderAll(); setBusy(false); q.focus(); }
  }

  function setBusy(busy) {
    $('#send', root).disabled = busy;
    $$('.suggestion', root).forEach((b) => { b.disabled = busy; });
  }

  form.addEventListener('submit', (e) => { e.preventDefault(); const t = q.value; q.value = ''; updateCount(); ask(t); });
  q.addEventListener('keydown', (e) => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); form.requestSubmit(); } });
  const updateCount = () => {
    $('#q-count', root).textContent = q.value.length;
    q.style.height = 'auto';
    q.style.height = Math.min(140, q.scrollHeight + 2) + 'px';
  };
  q.addEventListener('input', updateCount);

  renderAll();
  try {
    const sug = await get('/api/assistant/suggestions');
    $('#suggestions', root).innerHTML = sug.map((s) => `<button class="suggestion" type="button">${esc(s)}</button>`).join('');
    $$('.suggestion', root).forEach((b) => b.addEventListener('click', () => ask(b.textContent)));
  } catch (err) {
    $('#suggestions', root).innerHTML = `<p class="small muted">${esc(err.message)}</p>`;
  }
}
