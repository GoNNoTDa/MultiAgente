// Interfaz de MultiAgente. Habla con la aplicación por mensajes JSON (chrome.webview): manda órdenes
// { tipo, ... } y recibe estado, conversacion, entrada, agente, adjuntos, modelos, carpeta, aviso…
'use strict';

const puente = window.chrome && window.chrome.webview;
const enviar = (m) => puente && puente.postMessage(m);
const $ = (id) => document.getElementById(id);

function el(etiqueta, clase, texto) {
  const e = document.createElement(etiqueta);
  if (clase) e.className = clase;
  if (texto !== undefined && texto !== null) e.textContent = texto;
  return e;
}

const S = {
  relevos: {},                  // sesión → relevo propuesto (o sin destino)
  estado: null,
  vista: 'inicio',
  actual: null,                 // sesión abierta
  sesiones: {},                 // id → { entradas: Map, orden: [], agente }
  nueva: { carpeta: null, proyectoId: null },
  cajaActiva: null,
  historial: [],
};

function ses(id) {
  if (!S.sesiones[id]) S.sesiones[id] = { entradas: new Map(), orden: [], agente: null };
  return S.sesiones[id];
}

function guardarLocal(k, v) { try { localStorage.setItem(k, v); } catch { /* sin almacenamiento */ } }
function leerLocal(k) { try { return localStorage.getItem(k); } catch { return null; } }

// ------------------------------------------------------------------ avisos

function aviso(texto) {
  const d = el('div', '', texto);
  $('avisos').append(d);
  setTimeout(() => d.remove(), Math.min(12000, 4000 + texto.length * 40));
}

// ------------------------------------------------------------------ markdown

if (window.marked) marked.setOptions({ gfm: true, breaks: false });

function md(texto) {
  const d = el('div', 'ia');
  const html = window.marked ? marked.parse(texto || '') : (texto || '');
  d.innerHTML = window.DOMPurify ? DOMPurify.sanitize(html) : '';
  d.querySelectorAll('pre code').forEach((c) => { try { if (window.hljs) hljs.highlightElement(c); } catch { /* lenguaje raro */ } });
  d.querySelectorAll('a[href]').forEach((a) => a.addEventListener('click', (e) => {
    e.preventDefault();
    enviar({ tipo: 'abrirEnlace', url: a.href });
  }));
  return d;
}

// ------------------------------------------------------------------ textos

const NOMBRES = {
  default: 'Pedir permiso', acceptEdits: 'Aceptar ediciones', plan: 'Planificar', auto: 'Automático',
  bypassPermissions: 'Sin preguntar', dontAsk: 'Sin preguntar',
  low: 'Esfuerzo bajo', medium: 'Esfuerzo medio', high: 'Esfuerzo alto', xhigh: 'Esfuerzo muy alto', max: 'Esfuerzo máximo',
};
const ESTADOS = {
  Cerrada: 'Cerrada', Arrancando: 'Arrancando…', Esperando: 'Esperándote', Trabajando: 'Trabajando…',
  PidePermiso: 'Pide permiso', Error: 'Con error',
};
const ICONOS = { read: '📖', edit: '✎', delete: '🗑', move: '↔', search: '🔍', execute: '▶', think: '💭', fetch: '🌐', switch_mode: '⇄' };
const ESTADO_HERR = { pending: 'pendiente', in_progress: 'en curso', completed: 'hecho', failed: 'falló' };

function nombreOpcion(opcion, valor) {
  const o = (opcion.options || []).find((x) => x.value === valor);
  if (opcion.category === 'mode' && NOMBRES[valor]) return NOMBRES[valor];
  if (opcion.category === 'thought_level') return valor === 'default' ? 'Esfuerzo predeterminado' : (NOMBRES[valor] || (o && o.name) || valor);
  if (opcion.category === 'model' && valor === 'default') return 'Modelo predeterminado';
  return (o && o.name) || valor;
}

function hora(iso) {
  const d = new Date(iso);
  return isNaN(d) ? '' : d.toLocaleTimeString('es-ES', { hour: '2-digit', minute: '2-digit' });
}

function dinero(v, moneda) {
  if (v == null) return '';
  return v.toLocaleString('es-ES', { minimumFractionDigits: 2, maximumFractionDigits: 2 }) + ' ' + (moneda === 'EUR' ? '€' : moneda && moneda !== 'USD' ? moneda : '$');
}

function textoUso(c) {
  const u = c.consumo;
  if (u && u.enfriandoHasta) return { texto: `Ha llegado a su límite · vuelve a las ${hora(u.enfriandoHasta)}`, agotada: true };
  const partes = [];
  if (c.limites && c.limites.presupuesto) partes.push(`mes ${dinero(c.gastoMes || 0, c.moneda)} de ${dinero(c.limites.presupuesto, c.moneda)}`);
  else if (c.gastoMes) partes.push(`mes ${dinero(c.gastoMes, c.moneda)}`);
  if (u && u.pct5h != null) partes.push(`5 h ${Math.round(u.pct5h)} %`);
  if (u && u.pctSemana != null) partes.push(`semana ${Math.round(u.pctSemana)} %`);
  if (c.sinMargen && c.autorizada && c.listo) return { texto: partes.join(' · ') + (partes.length ? ' · ' : '') + 'sin margen', agotada: true };
  return { texto: partes.join(' · '), agotada: false };
}

function claseProveedor(c) {
  if (!c) return 'p-comun';
  if (c.catalogo && c.catalogo !== 'anthropic') return 'p-api';
  return 'p-' + c.proveedor;
}

// ------------------------------------------------------------------ vistas

function mostrar(vista) {
  S.vista = vista;
  $('vInicio').hidden = vista !== 'inicio';
  $('vSesion').hidden = vista !== 'sesion';
  $('vCuentas').hidden = vista !== 'cuentas';
  $('vExtensiones').hidden = vista !== 'extensiones';
  $('vEstadisticas').hidden = vista !== 'estadisticas';
  $('vConsumo').hidden = vista !== 'consumo';
  $('btnConsumo').classList.toggle('on', vista === 'consumo');
  if (vista === 'consumo') pintarConsumo();
  $('btnEstadisticas').classList.toggle('on', vista === 'estadisticas');
  if (vista === 'estadisticas') enviar({ tipo: 'estadisticas', dias: periodoDias });
  $('btnCuentas').classList.toggle('on', vista === 'cuentas');
  $('btnExtensiones').classList.toggle('on', vista === 'extensiones');
  if (vista === 'extensiones') enviar({ tipo: 'extensiones' });
  if (vista !== 'sesion') S.actual = null;
  cerrarMenu();
  pintarArbol();
  pintarPanel();
  const caja = vista === 'inicio' ? cajaNueva : vista === 'sesion' ? cajaSesion : null;
  if (caja) { S.cajaActiva = caja; setTimeout(() => caja.ta.focus(), 0); }
}

function abrirSesion(id) {
  guardarLocal('ultimaSesion', id);
  S.actual = id;
  mostrar('sesion');
  S.actual = id;
  pintarArbol();
  pintarCabecera();
  pintarConversacion();
  pintarAgente();
  pintarPanel();
  enviar({ tipo: 'abrirSesion', id });
  pedirCambios(id);
}

// ------------------------------------------------------------------ lateral

function todasLasSesiones() {
  if (!S.estado) return [];
  return [...S.estado.proyectos.flatMap((p) => p.sesiones.map((s) => ({ ...s, proyectoId: p.id }))), ...S.estado.sueltas];
}

function estadoDe(s) {
  const viva = S.sesiones[s.id] && S.sesiones[s.id].agente;
  return viva ? viva.estado : s.estado;
}

let arrastrando = null;          // sesión que se está arrastrando a otro proyecto

function botonSesion(s) {
  const b = el('button', 'ses' + (s.id === S.actual ? ' activa' : ''));
  b.title = s.carpeta;
  b.draggable = true;
  b.append(el('i', 'est ' + estadoDe(s)), el('span', 't', s.titulo));
  const cuenta = S.estado.cuentas.find((c) => c.alias === s.cuenta);
  const p = el('span', 'punto ' + claseProveedor(cuenta));
  p.title = s.cuenta || '';
  const mas = el('span', 'mas-ses', '⋯');
  mas.title = 'Opciones de la sesión';
  mas.addEventListener('click', (e) => { e.stopPropagation(); menuSesion(s, mas); });
  b.append(p, mas);
  b.addEventListener('click', () => abrirSesion(s.id));
  b.addEventListener('contextmenu', (e) => { e.preventDefault(); menuSesion(s, e); });
  b.addEventListener('dragstart', (e) => {
    arrastrando = s.id;
    e.dataTransfer.effectAllowed = 'move';
    e.dataTransfer.setData('text/plain', s.titulo);
    $('arbol').classList.add('moviendo');
  });
  b.addEventListener('dragend', () => { arrastrando = null; $('arbol').classList.remove('moviendo'); });
  return b;
}

function plegados() {
  try { return new Set(JSON.parse(leerLocal('plegados') || '[]')); } catch { return new Set(); }
}

function grupoProyecto(p, sesiones, pleg) {
  const clave = p ? p.id : 'sueltas';
  const plegado = pleg.has(clave);
  const t = el('div', 'proyecto');
  t.title = p ? p.ruta : 'Sesiones que no están en ningún proyecto';
  t.append(el('span', 'flecha', plegado ? '▸' : '▾'), el('span', 'n', p ? p.nombre : 'Sin proyecto'));
  if (plegado && sesiones.length) t.append(el('span', 'cuantas', String(sesiones.length)));
  if (p) {
    const acc = el('span', 'acc');
    const nueva = el('button', '', '＋');
    nueva.title = 'Nueva sesión en este proyecto';
    nueva.addEventListener('click', (e) => { e.stopPropagation(); nuevaEnProyecto(p); });
    const mas = el('button', '', '⋯');
    mas.title = 'Opciones del proyecto';
    mas.addEventListener('click', (e) => { e.stopPropagation(); menuProyecto(p, mas); });
    acc.append(nueva, mas);
    t.append(acc);
    t.addEventListener('contextmenu', (e) => { e.preventDefault(); menuProyecto(p, e); });
  }
  t.addEventListener('click', () => {
    const ahora = plegados();
    if (ahora.has(clave)) ahora.delete(clave); else ahora.add(clave);
    guardarLocal('plegados', JSON.stringify([...ahora]));
    pintarArbol();
  });
  // Soltar aquí una sesión la pasa a este proyecto
  t.addEventListener('dragover', (e) => { if (!arrastrando) return; e.preventDefault(); e.dataTransfer.dropEffect = 'move'; t.classList.add('destino'); });
  t.addEventListener('dragleave', () => t.classList.remove('destino'));
  t.addEventListener('drop', (e) => {
    t.classList.remove('destino');
    if (!arrastrando) return;
    e.preventDefault();
    const s = todasLasSesiones().find((x) => x.id === arrastrando);
    if (s && (s.proyectoId || null) !== (p ? p.id : null)) enviar({ tipo: 'sesionMover', sesionId: s.id, proyectoId: p ? p.id : null });
    arrastrando = null;
  });
  const nodos = [t];
  if (!plegado) {
    if (!sesiones.length) nodos.push(el('div', 'vacio dentro', 'Sin sesiones'));
    nodos.push(...sesiones.map(botonSesion));
  }
  return nodos;
}

function pintarArbol() {
  const a = $('arbol');
  a.replaceChildren();
  if (!S.estado) return;
  if (!S.estado.proyectos.length && !S.estado.sueltas.length)
    a.append(el('div', 'vacio', 'Aún no hay sesiones. Empieza una sesión nueva, crea un proyecto o importa tus conversaciones con ＋.'));
  const pleg = plegados();
  for (const p of S.estado.proyectos) a.append(...grupoProyecto(p, p.sesiones, pleg));
  if (S.estado.sueltas.length) a.append(...grupoProyecto(null, S.estado.sueltas, pleg));

  const l = $('cuentasLat');
  l.replaceChildren();
  if (!S.estado.cuentas.length) l.append(el('div', 'vacio', 'Sin cuentas todavía.'));
  for (const c of S.estado.cuentas) {
    const f = el('div', 'cta');
    const e = !c.listo ? el('span', 'e falta', 'falta instalar') : !c.autorizada ? el('span', 'e falta', 'falta autorizar')
      : c.problema ? el('span', 'e falta', 'no funciona')
      : c.consumo && c.consumo.enfriandoHasta ? el('span', 'e falta', 'agotada · ' + hora(c.consumo.enfriandoHasta))
      : c.sinMargen ? el('span', 'e falta', 'al límite')
      : c.consumo && c.consumo.pct5h != null ? el('span', 'e', `5 h ${Math.round(c.consumo.pct5h)} %`)
      : el('span', 'e', 'lista');
    f.append(el('span', 'punto ' + claseProveedor(c)), el('span', 'a', c.alias), e);
    f.title = c.problema || c.catalogoNombre || c.proveedor;
    l.append(f);
  }
}

// ------------------------------------------------------------------ inicio

const GRUPOS = [
  { proveedor: 'Claude', nombre: 'Claude Code' },
  { proveedor: 'Codex', nombre: 'Codex' },
  { proveedor: 'Gemini', nombre: 'Gemini' },
  { proveedor: null, nombre: 'Node.js' },
];

function pintarInicio() {
  const e = S.estado;
  $('tarjetaSinCuentas').hidden = e.cuentas.length > 0;
  const lista = $('listaAgentes');
  lista.replaceChildren();
  const resumen = [];
  for (const g of GRUPOS) {
    const reqs = e.requisitos.filter((r) => r.proveedor === g.proveedor);
    const listo = reqs.every((r) => r.encontrado);
    if (g.proveedor) resumen.push(g.nombre + (listo ? ' ✓' : ' ✗'));
    const fila = el('div', 'prov');
    fila.append(el('span', 'punto p-' + (g.proveedor || 'comun')), el('span', 'nombre', g.nombre));
    const ul = el('ul');
    for (const r of reqs) {
      const li = el('li');
      li.append(el('span', r.encontrado ? 'ok' : 'falta', r.encontrado ? '✓' : '✗'), el('span', '', r.nombre),
                el('span', 'v', r.encontrado ? (r.version || 'instalado') : 'no está'));
      ul.append(li);
    }
    fila.append(ul, el('span', listo ? 'ok' : 'falta', listo ? 'Listo' : 'Falta instalar'));
    lista.append(fila);
  }
  const falta = e.requisitos.some((r) => !r.encontrado);
  $('resumenAgentes').textContent = resumen.join(' · ');
  $('pieAgentes').hidden = !falta;
  $('btnInstalar').hidden = !e.puedeInstalar;
  if (falta) $('tarjetaAgentes').open = true;
  document.querySelectorAll('.version').forEach((v) => { v.textContent = 'MultiAgente ' + e.version; });
  pintarCamposNueva();
}

function pintarCamposNueva() {
  const c = cajaNueva;
  const e = S.estado;
  c.camposNueva.hidden = false;
  const proy = S.nueva.proyectoId && e.proyectos.find((p) => p.id === S.nueva.proyectoId);
  if (!proy) S.nueva.proyectoId = null;
  c.carpetaTxt.textContent = proy ? `${proy.nombre} · ${S.nueva.carpeta}` : (S.nueva.carpeta || 'Elegir carpeta…');
  c.carpetaBtn.title = S.nueva.carpeta || 'Carpeta de trabajo';
  c.proyectos.replaceChildren(new Option('Proyectos…', ''));
  for (const p of e.proyectos) c.proyectos.append(new Option(p.nombre, p.id));
  c.proyectos.hidden = e.proyectos.length === 0;
  const elegida = c.selCuenta.value || leerLocal('quienNueva');
  llenarQuien(c.selCuenta);
  const valores = [...c.selCuenta.options].map((o) => o.value).filter(Boolean);
  c.selCuenta.value = valores.includes(elegida) ? elegida : (valores[0] || '');
}

// Opciones de «quién trabaja»: automático con cada equipo y, aparte, cada cuenta fija
function llenarQuien(sel) {
  const e = S.estado;
  sel.replaceChildren();
  if (!e.cuentas.length) { sel.append(new Option('Añade una cuenta', '')); return; }
  const g1 = document.createElement('optgroup');
  g1.label = 'Automático (reparte y releva)';
  for (const eq of e.equipos) if (eq.miembros.length) g1.append(new Option(`Automático · ${eq.nombre}`, 'equipo:' + eq.id));
  if (g1.children.length) sel.append(g1);
  const g2 = document.createElement('optgroup');
  g2.label = 'Cuenta fija';
  for (const cu of e.cuentas) {
    const nota = !cu.listo ? ' (falta instalar)' : !cu.autorizada ? ' (falta autorizar)' : cu.problema ? ' (no funciona)' : cu.sinMargen ? ' (sin margen)' : '';
    g2.append(new Option(cu.alias + nota, 'cuenta:' + cu.id));
  }
  sel.append(g2);
}

// ------------------------------------------------------------------ sesión: cabecera y agente

function sesionActual() {
  return todasLasSesiones().find((s) => s.id === S.actual);
}

function pintarCabecera() {
  const s = sesionActual();
  if (!s) return;
  $('sesTitulo').textContent = s.titulo;
  $('sesCarpeta').textContent = s.carpeta;
  const sel = $('sesCuenta');
  llenarQuien(sel);
  const cuenta = S.estado.cuentas.find((c) => c.id === s.cuentaId);
  if (s.equipoId) {
    const o = [...sel.options].find((x) => x.value === 'equipo:' + s.equipoId);
    if (o && cuenta) o.textContent += ` (ahora ${cuenta.alias})`;
    sel.value = 'equipo:' + s.equipoId;
  } else if (cuenta) sel.value = 'cuenta:' + cuenta.id;
  sel.title = cuenta ? `Trabaja ahora: ${cuenta.alias}` : '';
  $('sesPunto').className = 'punto ' + claseProveedor(cuenta);
  const ag = S.sesiones[s.id] && S.sesiones[s.id].agente;
  $('sesEstado').textContent = ESTADOS[ag ? ag.estado : s.estado] || '';
  const ctx = ag && ag.contexto;
  const cx = $('sesContexto');
  cx.hidden = !(ctx && ctx.tamano > 0);
  if (!cx.hidden) {
    const p = Math.min(100, Math.round(100 * ctx.usado / ctx.tamano));
    cx.style.setProperty('--p', p);
    cx.classList.toggle('alto', p >= 80);
    cx.querySelector('b').textContent = `${p} %`;
    cx.title = `Contexto usado: ${ctx.usado.toLocaleString('es-ES')} de ${ctx.tamano.toLocaleString('es-ES')} tokens` + (p >= 80 ? '. El agente compactará la conversación pronto.' : '');
  }
}

function pintarAgente() {
  const c = cajaSesion;
  const s = S.actual && S.sesiones[S.actual];
  const ag = s && s.agente;
  const estado = ag ? ag.estado : 'Arrancando';
  pintarCabecera();

  // Banda de error (sin cuenta, sin clave, límite…)
  c.banda.hidden = !(ag && ag.error && ag.estado === 'Error');
  if (!c.banda.hidden) {
    const d = el('div');
    const reintentar = el('button', 'boton fino', 'Reintentar');
    reintentar.addEventListener('click', () => enviar({ tipo: 'abrirSesion', id: S.actual }));
    const cuentas = el('button', 'boton fino', 'Cuentas');
    cuentas.addEventListener('click', () => mostrar('cuentas'));
    d.append(el('span', '', ag.error), reintentar, cuentas);
    c.banda.replaceChildren(d);
  }

  pintarRelevo();

  // Permiso pendiente
  const p = ag && ag.permiso;
  c.permiso.hidden = !p;
  if (p) {
    const d = el('div');
    const t = el('div', 'pt');
    t.append(el('b', '', 'El agente pide permiso: '), document.createTextNode(p.titulo));
    const ops = el('div', 'ops');
    for (const o of p.opciones) {
      const b = el('button', 'boton' + (String(o.kind || '').startsWith('allow') ? ' prim' : ''), o.name);
      b.addEventListener('click', () => enviar({ tipo: 'permiso', sesionId: S.actual, peticion: p.id, opcion: o.optionId }));
      ops.append(b);
    }
    d.append(t, ops);
    c.permiso.replaceChildren(d);
  }

  // Cola
  const cola = (ag && ag.cola) || [];
  c.cola.hidden = cola.length === 0;
  if (cola.length) {
    const d = el('div');
    d.append(el('div', '', 'En cola (lo leerá al terminar lo que está haciendo):'), ...cola.map((t) => el('div', '', '· ' + t)));
    c.cola.replaceChildren(d);
  }

  // Opciones (modo de permisos, modelo, esfuerzo…)
  c.opciones.replaceChildren();
  const orden = { mode: 0, model: 1, thought_level: 2 };
  const opciones = ((ag && ag.opciones) || []).filter((o) => (o.options || []).length > 1 && (o.options || []).length <= 60)
    .sort((a, b) => (orden[a.category] ?? 9) - (orden[b.category] ?? 9));
  for (const o of opciones) {
    const sel = el('select', 'chip');
    sel.title = o.description || o.name || '';
    for (const v of o.options) {
      const op = new Option(nombreOpcion(o, v.value), v.value);
      op.title = v.description || '';
      sel.append(op);
    }
    sel.value = o.currentValue;
    sel.addEventListener('change', () => enviar({ tipo: 'opcion', sesionId: S.actual, id: o.id, valor: sel.value }));
    c.opciones.append(sel);
  }
  c.comandos = (ag && ag.comandos) || [];

  const trabaja = estado === 'Trabajando' || estado === 'PidePermiso';
  c.parar.hidden = !trabaja;
  c.ta.placeholder = estado === 'Error' ? 'El agente no está disponible.'
    : trabaja ? 'Escribe: se mandará cuando termine (o pulsa ■ para interrumpir)…'
    : 'Escribe, arrastra ficheros o pega una captura (Ctrl+V)…  / para órdenes';
  pintarTrabajando();
}

function pintarRelevo() {
  const c = cajaSesion;
  const r = S.actual && S.relevos[S.actual];
  c.bandaRelevo.hidden = !r;
  if (!r) return;
  const d = el('div');
  if (r.sinDestino) {
    const b = el('button', 'boton fino', 'Cuentas');
    b.addEventListener('click', () => mostrar('cuentas'));
    d.append(el('span', '', `${r.motivo}, y ninguna cuenta del equipo tiene margen ahora. La sesión sigue con «${r.de}».`), b);
  } else {
    const t = el('span');
    t.append(document.createTextNode(`⇄ ${r.motivo} → sigue en «${r.a}» `));
    const reloj = el('span', 'reloj', r.segundos > 0 ? `en ${r.segundos} s` : 'ahora');
    t.append(reloj, document.createTextNode(r.esperaTurno ? ', al terminar el turno' : ''));
    const ya = el('button', 'boton fino prim', 'Cambiar ya');
    ya.addEventListener('click', () => enviar({ tipo: 'relevoYa', sesionId: S.actual }));
    const no = el('button', 'boton fino', 'Cancelar');
    no.addEventListener('click', () => enviar({ tipo: 'relevoCancelar', sesionId: S.actual }));
    d.append(t, ya, no);
  }
  c.bandaRelevo.replaceChildren(d);
}

// ------------------------------------------------------------------ conversación

let pendientes = new Set();
let pintando = false;

function pintarConversacion() {
  const cont = $('entradas');
  cont.replaceChildren();
  const s = S.actual && S.sesiones[S.actual];
  if (s) for (const id of s.orden) cont.append(nodoEntrada(s.entradas.get(id)));
  pintarTrabajando();
  bajar(true);
}

// «Pegado al final»: la conversación sigue al final mientras el usuario no suba; cambie lo que cambie de alto
// (respuestas que llegan, barra de permiso, cola…). Se suelta solo con un desplazamiento del usuario.
let pegado = true;
function cercaDelFinal() {
  const c = $('conv');
  return c.scrollHeight - c.scrollTop - c.clientHeight < 80;
}

function bajar(forzar) {
  const c = $('conv');
  if (forzar) pegado = true;
  if (pegado) c.scrollTop = c.scrollHeight;
}

function actualizarEntrada(sesionId, e) {
  const s = ses(sesionId);
  if (!s.entradas.has(e.id)) s.orden.push(e.id);
  s.entradas.set(e.id, e);
  if (sesionId !== S.actual || S.vista !== 'sesion') return;
  pendientes.add(e.id);
  if (pintando) return;
  pintando = true;
  requestAnimationFrame(() => {
    pintando = false;
    const cont = $('entradas');
    for (const id of pendientes) {
      const nuevo = nodoEntrada(s.entradas.get(id));
      const viejo = cont.querySelector(`[data-id="${CSS.escape(id)}"]`);
      if (viejo) {
        if (viejo.tagName === 'DETAILS' && viejo.open) nuevo.open = true;
        viejo.replaceWith(nuevo);
      } else {
        const trab = cont.querySelector('.trabajando');
        if (trab) trab.before(nuevo); else cont.append(nuevo);
      }
    }
    pendientes = new Set();
    bajar();
  });
}

function pintarTrabajando() {
  const cont = $('entradas');
  const viejo = cont.querySelector('.trabajando');
  const ag = S.actual && S.sesiones[S.actual] && S.sesiones[S.actual].agente;
  const estado = ag ? ag.estado : (S.vista === 'sesion' ? 'Arrancando' : null);
  if (estado === 'Trabajando' || estado === 'Arrancando') {
    const nombre = ag ? ag.cuenta.alias : '';
    const t = el('div', 'trabajando');
    t.append(el('i'), document.createTextNode(estado === 'Arrancando' ? `Arrancando ${nombre}…` : `${nombre} está trabajando…`));
    if (viejo) viejo.replaceWith(t); else cont.append(t);
    bajar();
  } else if (viejo) viejo.remove();
}

function urlAdjunto(a) {
  if (a.url) return a.url;
  if (!a.esImagen || !S.estado) return null;
  const raiz = (S.estado.raiz + '\\datos\\adjuntos\\').toLowerCase();
  if (!a.ruta.toLowerCase().startsWith(raiz)) return null;
  return 'https://adjuntos.multiagente/' + a.ruta.substring(raiz.length).split('\\').map(encodeURIComponent).join('/');
}

function chipAdjunto(a, alQuitar) {
  const url = urlAdjunto(a);
  let n;
  if (url) {
    n = el('div', 'mini');
    n.style.backgroundImage = `url("${url}")`;
    n.append(el('small', '', a.nombre));
  } else {
    n = el('div', 'fich');
    n.append(el('span', '', (a.tipo === 'inode/directory' ? '📁 ' : '📄 ') + a.nombre));
  }
  n.title = a.ruta;
  if (alQuitar) {
    const q = el('button', 'quitar', '✕');
    q.title = 'Quitar';
    q.addEventListener('click', (e) => { e.stopPropagation(); alQuitar(); });
    n.append(q);
  } else {
    n.style.cursor = 'pointer';
    n.addEventListener('click', () => enviar({ tipo: 'abrirRuta', ruta: a.ruta }));
  }
  return n;
}

function enlaceRuta(ruta, texto) {
  const b = el('button', 'enlace dif-ruta', texto || ruta);
  b.title = ruta;
  b.addEventListener('click', (e) => { e.preventDefault(); e.stopPropagation(); enviar({ tipo: 'abrirRuta', ruta }); });
  return b;
}

function nodoDiferencias(d) {
  const caja = el('div');
  const viejo = d.oldText || '';
  const nuevo = d.newText || '';
  const partes = window.Diff ? Diff.diffLines(viejo, nuevo) : [{ value: nuevo, added: true }];
  let mas = 0, menos = 0;
  const dif = el('div', 'dif');
  for (const p of partes) {
    const lineas = p.value.replace(/\n$/, '').split('\n');
    for (const l of lineas) {
      if (p.added) { mas++; dif.append(el('div', 'mas', '+ ' + l)); }
      else if (p.removed) { menos++; dif.append(el('div', 'menos', '− ' + l)); }
      else if (lineas.length <= 6) dif.append(el('div', '', '  ' + l));
    }
    if (!p.added && !p.removed && lineas.length > 6) dif.append(el('div', 'tenue', `  … ${lineas.length} líneas sin cambios`));
  }
  const cab = el('div');
  cab.append(enlaceRuta(d.path), el('span', 'tenue', `  +${mas} −${menos}`));
  caja.append(cab, dif);
  return caja;
}

function cuerpoHerramienta(e) {
  const c = el('div', 'cuerpo');
  for (const u of e.ubicaciones || []) if (u && u.path) c.append(enlaceRuta(u.path, u.path + (u.line ? ':' + u.line : '')), el('br'));
  const datos = e.datos || {};
  if (e.clase === 'execute' && (datos.command || datos.cmd)) c.append(el('pre', '', [].concat(datos.command || datos.cmd).join(' ')));
  for (const item of e.contenido || []) {
    if (!item) continue;
    if (item.type === 'diff') c.append(nodoDiferencias(item));
    else if (item.type === 'content' && item.content) {
      if (item.content.type === 'text') c.append(el('pre', '', item.content.text));
      else if (item.content.type === 'resource_link') c.append(enlaceRuta(decodeURIComponent((item.content.uri || '').replace(/^file:\/\/\//, '')), item.content.name));
    } else if (item.type === 'terminal') c.append(el('div', 'tenue', '(salida en la terminal del agente)'));
  }
  if (!c.childNodes.length) c.append(el('div', 'tenue', 'Sin más detalle.'));
  return c;
}

function nodoEntrada(e) {
  let n;
  switch (e.tipo) {
    case 'usuario': {
      n = el('div', 'yo');
      if (e.adjuntos && e.adjuntos.length) {
        const a = el('div', 'adjs');
        a.append(...e.adjuntos.map((x) => chipAdjunto(x)));
        n.append(a);
      }
      if (e.texto) n.append(el('div', 'burbuja', e.texto));
      break;
    }
    case 'agente':
      n = md(e.texto);
      break;
    case 'pensamiento': {
      n = el('details', 'pens');
      n.append(el('summary', '', 'Razonamiento'), el('div', '', e.texto));
      break;
    }
    case 'herramienta': {
      n = el('details', 'herr');
      const s = el('summary');
      s.append(el('span', 'ic', ICONOS[e.clase] || '•'), el('span', 'tt', e.titulo || 'Herramienta'),
               el('span', 'st ' + (e.estado || ''), ESTADO_HERR[e.estado] || ''));
      n.append(s);
      n.addEventListener('toggle', () => {
        if (n.open && !n.querySelector('.cuerpo')) n.append(cuerpoHerramienta(S.sesiones[S.actual].entradas.get(e.id) || e));
      });
      break;
    }
    case 'plan': {
      n = el('div', 'plan');
      n.append(el('b', '', 'Tareas'));
      const ul = el('ul');
      for (const p of e.pasos || []) {
        const marca = p.status === 'completed' ? '☑' : p.status === 'in_progress' ? '◐' : '☐';
        ul.append(el('li', p.status || '', `${marca} ${p.content}`));
      }
      n.append(ul);
      break;
    }
    case 'aviso':
      n = el('div', 'aviso ' + (e.nivel || ''), e.texto);
      break;
    case 'relevo': {
      n = el('div', 'relevo-linea');
      const t = el('span');
      const cuenta = S.estado && S.estado.cuentas.find((c) => c.alias === e.cuenta);
      t.append(el('span', 'punto ' + claseProveedor(cuenta)), document.createTextNode(' ' + e.texto));
      n.append(t);
      break;
    }
    default:
      n = el('div', 'tenue', e.texto || '');
  }
  n.dataset.id = e.id;
  return n;
}

// ------------------------------------------------------------------ caja de escribir

function crearCaja(contenedor, modo) {
  contenedor.replaceChildren($('tCaja').content.cloneNode(true));
  const q = (s) => contenedor.querySelector(s);
  const c = {
    modo, adjuntos: [], comandos: [], selOrden: 0, ultimoEsc: 0,
    banda: q('.banda:not(.relevo)'), bandaRelevo: q('.banda.relevo'), permiso: q('.permiso'), cola: q('.cola'), camposNueva: q('.campos-nueva'),
    pend: q('.pend'), ordenes: q('.ordenes'), ta: q('textarea'), opciones: q('.opciones'),
    parar: q('.parar'), enviarBtn: q('.enviar'), mas: q('.mas-btn'),
    carpetaBtn: q('.carpeta'), carpetaTxt: q('.carpeta span'), proyectos: q('.proyectos'), selCuenta: q('.cuenta-nueva'),
  };
  const activar = () => { S.cajaActiva = c; };
  c.ta.addEventListener('focus', activar);
  c.ta.addEventListener('input', () => { ajustarAlto(c); pintarOrdenes(c); });
  c.ta.addEventListener('keydown', (e) => teclaCaja(c, e));
  c.ta.addEventListener('paste', (e) => pegar(c, e));
  c.mas.addEventListener('click', () => { activar(); enviar({ tipo: 'elegirFicheros' }); });
  c.enviarBtn.addEventListener('click', () => enviarDesde(c));
  c.parar.addEventListener('click', () => enviar({ tipo: 'interrumpir', sesionId: S.actual }));
  if (modo === 'nueva') {
    c.carpetaBtn.addEventListener('click', () => enviar({ tipo: 'elegirCarpeta', para: 'nueva', inicial: S.nueva.carpeta }));
    c.proyectos.addEventListener('change', () => {
      const p = S.estado.proyectos.find((x) => x.id === c.proyectos.value);
      if (p) { S.nueva.carpeta = p.ruta; S.nueva.proyectoId = p.id; pintarCamposNueva(); }
      c.proyectos.value = '';
    });
    c.selCuenta.addEventListener('change', () => guardarLocal('quienNueva', c.selCuenta.value));
    c.ta.placeholder = '¿Qué hacemos? Escribe, arrastra ficheros o pega una captura (Ctrl+V)…';
  }
  return c;
}

function ajustarAlto(c) {
  c.ta.style.height = 'auto';
  c.ta.style.height = Math.min(c.ta.scrollHeight, window.innerHeight * 0.4) + 'px';
}

function pintarPend(c) {
  c.pend.hidden = c.adjuntos.length === 0;
  c.pend.replaceChildren(...c.adjuntos.map((a, i) => chipAdjunto(a, () => { c.adjuntos.splice(i, 1); pintarPend(c); })));
}

function anadirAdjuntos(c, lista) {
  for (const a of lista) if (!c.adjuntos.some((x) => x.ruta === a.ruta)) c.adjuntos.push(a);
  pintarPend(c);
  c.ta.focus();
}

function enviarDesde(c) {
  const texto = c.ta.value.trim();
  if (!texto && !c.adjuntos.length) return;
  const adjuntos = c.adjuntos.map((a) => ({ ruta: a.ruta, nombre: a.nombre }));
  if (c.modo === 'nueva') {
    if (!S.nueva.carpeta) { aviso('Elige primero la carpeta de trabajo.'); return; }
    if (!c.selCuenta.value) { aviso('Añade una cuenta para empezar.'); mostrar('cuentas'); return; }
    guardarLocal('quienNueva', c.selCuenta.value);
    c.enviarBtn.disabled = true;
    enviar({ tipo: 'sesionNueva', carpeta: S.nueva.carpeta, proyectoId: S.nueva.proyectoId, quien: c.selCuenta.value, texto, adjuntos });
    return; // el texto se limpia cuando la sesión se abre
  }
  enviar({ tipo: 'enviar', sesionId: S.actual, texto, adjuntos });
  limpiarCaja(c, texto);
}

function limpiarCaja(c, texto) {
  if (texto) S.historial.push(texto);
  c.ta.value = '';
  c.adjuntos = [];
  pintarPend(c);
  ajustarAlto(c);
  pintarOrdenes(c);
}

function teclaCaja(c, e) {
  const menu = !c.ordenes.hidden;
  if (menu && (e.key === 'ArrowDown' || e.key === 'ArrowUp')) {
    e.preventDefault();
    const n = c.ordenes.children.length;
    c.selOrden = (c.selOrden + (e.key === 'ArrowDown' ? 1 : n - 1)) % n;
    marcarOrden(c);
    return;
  }
  if (menu && (e.key === 'Tab' || (e.key === 'Enter' && !e.shiftKey))) {
    e.preventDefault();
    const b = c.ordenes.children[c.selOrden];
    if (b) b.click();
    return;
  }
  if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) {
    e.preventDefault();
    enviarDesde(c);
    return;
  }
  if (e.key === 'ArrowUp' && !c.ta.value && S.historial.length) {
    e.preventDefault();
    c.ta.value = S.historial[S.historial.length - 1];
    ajustarAlto(c);
    return;
  }
  if (e.key === 'Escape') {
    if (menu) { c.ordenes.hidden = true; return; }
    const ahora = Date.now();
    if (ahora - c.ultimoEsc < 700 && c.modo === 'sesion') enviar({ tipo: 'interrumpir', sesionId: S.actual });
    c.ultimoEsc = ahora;
  }
}

function pintarOrdenes(c) {
  const v = c.ta.value;
  const m = /^\/(\S*)$/.exec(v);
  if (!m || c.modo !== 'sesion' || !c.comandos.length) { c.ordenes.hidden = true; return; }
  const filtro = m[1].toLowerCase();
  const lista = c.comandos.filter((o) => o.name.toLowerCase().includes(filtro)).slice(0, 30);
  c.ordenes.hidden = lista.length === 0;
  c.ordenes.replaceChildren(...lista.map((o) => {
    const b = el('button');
    b.type = 'button';
    b.append(el('b', '', '/' + o.name), el('span', '', o.description || ''));
    b.addEventListener('click', () => {
      c.ta.value = '/' + o.name + ' ';
      c.ordenes.hidden = true;
      c.ta.focus();
    });
    return b;
  }));
  c.selOrden = 0;
  marcarOrden(c);
}

function marcarOrden(c) {
  [...c.ordenes.children].forEach((b, i) => b.classList.toggle('sel', i === c.selOrden));
  const b = c.ordenes.children[c.selOrden];
  if (b) b.scrollIntoView({ block: 'nearest' });
}

function pegar(c, e) {
  const items = [...((e.clipboardData && e.clipboardData.items) || [])];
  const imagenes = items.filter((i) => i.kind === 'file' && i.type.startsWith('image/'));
  const ficheros = items.filter((i) => i.kind === 'file' && !i.type.startsWith('image/')).map((i) => i.getAsFile()).filter(Boolean);
  if (!imagenes.length && !ficheros.length) return;
  e.preventDefault();
  S.cajaActiva = c;
  for (const it of imagenes) {
    const f = it.getAsFile();
    if (!f) continue;
    const r = new FileReader();
    r.onload = () => {
      const base64 = String(r.result).split(',')[1] || '';
      enviar({ tipo: 'pegar', sesionId: c.modo === 'sesion' ? S.actual : null, nombre: f.name && f.name !== 'image.png' ? f.name : '', mime: f.type, base64 });
    };
    r.readAsDataURL(f);
  }
  if (ficheros.length && puente && puente.postMessageWithAdditionalObjects) puente.postMessageWithAdditionalObjects({ tipo: 'soltar' }, ficheros);
}

// ------------------------------------------------------------------ arrastrar y soltar

let arrastres = 0;
function hayFicheros(e) { return e.dataTransfer && [...e.dataTransfer.types].includes('Files'); }
document.addEventListener('dragenter', (e) => {
  if (!hayFicheros(e) || !['inicio', 'sesion'].includes(S.vista)) return;
  e.preventDefault();
  arrastres++;
  $('soltar').hidden = false;
});
document.addEventListener('dragover', (e) => { if (hayFicheros(e)) e.preventDefault(); });
document.addEventListener('dragleave', () => { if (--arrastres <= 0) { arrastres = 0; $('soltar').hidden = true; } });
document.addEventListener('drop', (e) => {
  arrastres = 0;
  $('soltar').hidden = true;
  if (!hayFicheros(e)) return;
  e.preventDefault();
  if (!['inicio', 'sesion'].includes(S.vista)) return;
  S.cajaActiva = S.vista === 'inicio' ? cajaNueva : cajaSesion;
  if (puente && puente.postMessageWithAdditionalObjects) puente.postMessageWithAdditionalObjects({ tipo: 'soltar' }, e.dataTransfer.files);
});

// ------------------------------------------------------------------ cuentas

function catalogo() { return (S.estado && S.estado.catalogo && S.estado.catalogo.proveedores) || []; }

let firmaTipos = '';
function pintarTiposCuenta() {
  const sel = $('fcTipo');
  const firma = JSON.stringify(catalogo().map((p) => p.id)) + JSON.stringify(S.estado.proveedores.map((p) => p.listo));
  if (firma === firmaTipos) return;
  firmaTipos = firma;
  const anterior = sel.value;
  sel.replaceChildren();
  const grupo = (t) => { const g = document.createElement('optgroup'); g.label = t; sel.append(g); return g; };
  const listo = (p) => (S.estado.proveedores.find((x) => x.id === p) || {}).listo;
  const g1 = grupo('Suscripción (inicio de sesión con la herramienta oficial)');
  g1.append(new Option('Claude · Pro / Max / Team' + (listo('Claude') ? '' : ' (falta instalar)'), 'login:Claude'),
            new Option('Codex · cuenta de ChatGPT' + (listo('Codex') ? '' : ' (falta instalar)'), 'login:Codex'),
            new Option('Gemini · cuenta de Google' + (listo('Gemini') ? '' : ' (falta instalar)'), 'login:Gemini'));
  const g2 = grupo('Clave de API del propio proveedor');
  g2.append(new Option('Anthropic (Claude)', 'cat:anthropic'), new Option('OpenAI (Codex)', 'api:Codex'), new Option('Google (Gemini)', 'api:Gemini'));
  const g3 = grupo('Otros modelos con Claude Code (API compatible con Anthropic)');
  for (const p of catalogo().filter((x) => x.agente === 'Claude' && x.id !== 'anthropic')) g3.append(new Option(p.nombre, 'cat:' + p.id));
  const g4 = grupo('Otros modelos con Codex (API compatible con OpenAI)');
  for (const p of catalogo().filter((x) => x.agente === 'Codex')) g4.append(new Option(p.nombre, 'cat:' + p.id));
  sel.value = anterior || 'login:Claude';
  if (!sel.value) sel.selectedIndex = 0;
  cambioTipo();
}

function tipoElegido() {
  const [clase, id] = $('fcTipo').value.split(':');
  const cat = clase === 'cat' ? catalogo().find((p) => p.id === id) : null;
  return { clase, id, cat };
}

function cambioTipo() {
  const { clase, id, cat } = tipoElegido();
  const necesitaClave = clase === 'api' || (cat && !cat.sinClave);
  $('fcClaveL').hidden = !necesitaClave;
  $('fcEndpointL').hidden = !(cat && cat.baseUrl);
  $('fcModeloL').hidden = !cat;
  $('fcEndpoint').value = (cat && cat.baseUrl) || '';
  $('fcModelo').value = cat && cat.soloModelos ? cat.modelos[0] : '';
  const dl = $('fcModelos');
  dl.replaceChildren(...((cat && cat.modelos) || []).map((m) => new Option(m, m)));
  $('fcModelosInfo').textContent = !cat ? '' : cat.soloModelos ? `Solo funcionan estos: ${cat.modelos.join(', ')}.`
    : cat.listar ? 'Pulsa «Actualizar lista» para ver los modelos que ofrece ahora el proveedor.' : 'Este proveedor no da su lista: se proponen los conocidos.';
  $('fcListar').hidden = !(cat && cat.listar);
  const prov = clase === 'cat' ? (cat && cat.agente) : id;
  const donde = (cat && cat.claveUrl) || ((S.estado.proveedores.find((p) => p.id === prov) || {}).dondeClave);
  $('fcDondeClave').textContent = donde ? 'Dónde consigo la clave' : '';
  $('fcDondeClave').dataset.url = donde || '';
  let ayuda = '';
  if (clase === 'login') ayuda = 'Al añadirla, pulsa «Autorizar…»: se abre la herramienta oficial para que inicies sesión en el navegador.';
  else if (cat) ayuda = (cat.agente === 'Codex' ? 'Se usa con el agente de Codex. ' : 'Se usa con el agente de Claude Code (todas sus herramientas). ') + (cat.notas || '');
  else if (clase === 'api') ayuda = 'La clave se guarda cifrada en la carpeta de MultiAgente.';
  $('fcAyuda').textContent = ayuda;
  $('fcCatalogo').textContent = cat && cat.confianza ? `Datos del catálogo (${S.estado.catalogo.version}) · confianza ${cat.confianza}` : '';
  if (!$('fcAlias').dataset.tocado) $('fcAlias').value = cat ? cat.nombre.split(' (')[0] : ({ Claude: 'Claude', Codex: 'Codex', Gemini: 'Gemini' }[id] || '');
}

function formModelo(c) {
  const d = el('div', 'limites form-modelo');
  const cat = catalogo().find((p) => p.id === c.catalogo);
  const entrada = el('input');
  entrada.value = c.modelo || '';
  entrada.setAttribute('list', 'cmModelos');
  entrada.spellcheck = false;
  const lista = el('datalist');
  lista.id = 'cmModelos';
  lista.replaceChildren(...((cat && cat.modelos) || []).map((m) => new Option(m, m)));
  const ver = el('button', 'boton fino', 'Ver los del proveedor');
  ver.hidden = !(cat && cat.listar);
  ver.addEventListener('click', () => { ver.disabled = true; enviar({ tipo: 'modelos', para: 'cuenta', catalogo: c.catalogo, cuentaId: c.id }); });
  const todos = el('button', 'boton fino', 'Probar cuáles funcionan');
  todos.hidden = !(cat && cat.traducir && cat.listar);
  todos.title = 'Prueba con tu clave todos los modelos de chat del proveedor (tarda un poco)';
  todos.addEventListener('click', () => { todos.disabled = true; todos.textContent = 'Probando modelos…'; enviar({ tipo: 'modelosProbar', id: c.id }); });
  const ok = el('button', 'boton fino prim', 'Probar y guardar');
  ok.addEventListener('click', () => { ok.disabled = true; ok.textContent = 'Probando…'; enviar({ tipo: 'cuentaModelo', id: c.id, modelo: entrada.value }); });
  const l = el('label', '', 'Modelo');
  l.append(entrada, lista);
  d.append(l, ver, todos, ok, el('span', 'tenue', 'Se prueba con una petición mínima antes de guardarlo (puedes poner varios separados por comas: se queda el primero que funcione). Las sesiones abiertas de esta cuenta siguen con el modelo nuevo.'));
  return d;
}

function pintarCuentas() {
  pintarTiposCuenta();
  pintarEquipos();
  const l = $('listaCuentas');
  l.replaceChildren();
  l.hidden = S.estado.cuentas.length === 0;
  for (const c of S.estado.cuentas) {
    const f = el('div', 'cuenta');
    const logo = el('span', 'logo ' + claseProveedor(c), (c.catalogoNombre || c.proveedor).charAt(0).toUpperCase());
    const n = el('span', 'n', c.alias);
    const detalle = c.catalogoNombre ? `${c.catalogoNombre} · con ${c.proveedor === 'Claude' ? 'Claude Code' : c.proveedor}` : c.proveedor + (c.tipo === 'Api' ? ' · clave de API' : ' · suscripción');
    n.append(el('small', '', detalle + (c.modelo ? ' · ' + c.modelo : '')));
    const uso = textoUso(c);
    if (uso.texto) n.append(el('span', 'uso' + (uso.agotada ? ' agotada' : ''), uso.texto));
    const t = el('span', 't');
    t.append(!c.listo ? el('span', 'falta', '● Falta instalar el agente') : c.problema ? el('span', 'falta', '● ' + c.problema) : c.autorizada ? el('span', 'ok', c.tipo === 'Api' ? '● Clave guardada' : '● Autorizada') : el('span', 'falta', '● Falta autorizar'));
    const acc = el('span', 'acc');
    if (c.tipo === 'Login') {
      const b = el('button', 'boton fino' + (c.autorizada ? '' : ' prim'), c.autorizada ? 'Volver a autorizar…' : 'Autorizar…');
      b.addEventListener('click', () => { enviar({ tipo: 'cuentaAutorizar', id: c.id }); aviso('Se ha abierto una ventana para iniciar sesión. Al terminar, vuelve aquí.'); });
      acc.append(b);
    } else {
      const b = el('button', 'boton fino', 'Cambiar clave');
      b.addEventListener('click', () => {
        const clave = window.prompt(`Clave de API nueva para «${c.alias}»:`);
        if (clave) enviar({ tipo: 'cuentaClave', id: c.id, clave });
      });
      acc.append(b);
    }
    if (c.tipo === 'Api' && c.catalogo) {
      const mo = el('button', 'boton fino' + (c.problema ? ' prim' : ''), 'Modelo…');
      mo.addEventListener('click', () => {
        const ya = f.querySelector('.form-modelo');
        if (ya) { ya.remove(); return; }
        f.append(formModelo(c));
      });
      acc.append(mo);
    }
    const limites = el('button', 'boton fino', 'Límites…');
    limites.addEventListener('click', () => {
      const ya = f.querySelector('.limites:not(.form-modelo)');
      if (ya) { ya.remove(); return; }
      f.append(formLimites(c));
    });
    acc.append(limites);
    const quitar = el('button', 'boton fino peligro', 'Quitar');
    quitar.addEventListener('click', () => {
      if (window.confirm(`¿Quitar la cuenta «${c.alias}»? Se borran su carpeta (con su inicio de sesión) y su clave. Las conversaciones se conservan.`))
        enviar({ tipo: 'cuentaQuitar', id: c.id });
    });
    acc.append(quitar);
    f.append(logo, n, t, acc);
    l.append(f);
  }
}

function formLimites(c) {
  const d = el('div', 'limites');
  const campo = (titulo, valor, paso, ayuda) => {
    const l = el('label', '', titulo);
    const i = el('input'); i.type = 'number'; i.step = paso; i.min = 0; i.value = valor ?? ''; i.placeholder = ayuda || '';
    l.append(i);
    d.append(l);
    return i;
  };
  const u5 = campo('Relevar al … % del bloque de 5 h', c.limites.umbral5h, 1);
  const us = campo('Relevar al … % semanal', c.limites.umbralSemana, 1);
  const pr = campo(`Presupuesto del mes (${c.moneda === 'EUR' ? '€' : c.moneda || '$'})`, c.limites.presupuesto, 0.5, 'sin límite');
  const ok = el('button', 'boton fino prim', 'Guardar');
  const num = (i) => (i.value.trim() === '' ? null : Number(i.value));
  ok.addEventListener('click', () => enviar({ tipo: 'cuentaLimites', id: c.id, umbral5h: num(u5), umbralSemana: num(us), presupuesto: num(pr) }));
  d.append(ok, el('span', 'tenue', c.proveedor === 'Claude' && c.tipo === 'Login'
    ? 'Los porcentajes salen del consumo de tu suscripción.'
    : 'El presupuesto cuenta el coste que informa el agente; al agotarlo, la cuenta pasa a reserva hasta el mes que viene.'));
  return d;
}

// ------------------------------------------------------------------ estadísticas

let periodoDias = 7;
const numero = (n) => (n || 0).toLocaleString('es-ES');

function pintarEstadisticas(m) {
  const t = m.total;
  const cifra = (valor, texto) => { const d = el('div', 'cifra'); d.append(el('b', '', valor), el('span', '', texto)); return d; };
  $('cifras').replaceChildren(cifra(numero(t.turnos), 'turnos'), cifra(numero(t.tokens), 'tokens'),
                              cifra(t.coste != null ? dinero(t.coste, t.moneda) : '—', 'coste informado'));
  const cab = () => {
    const f = el('div', 'fila-est cab');
    f.append(el('span', '', ''), el('span', 'num', 'Turnos'), el('span', 'num', 'Tokens'), el('span', 'num', 'Coste'));
    return f;
  };
  const fila = (izq, d) => {
    const f = el('div', 'fila-est');
    f.append(izq, el('span', 'num', numero(d.turnos)), el('span', 'num', numero(d.tokens)), el('span', 'num', d.coste != null ? dinero(d.coste, d.moneda) : '—'));
    return f;
  };
  const lc = $('estCuentas');
  lc.replaceChildren(cab());
  if (!m.porCuenta.length) lc.append(el('div', 'lista-vacia', 'Sin uso en este periodo.'));
  for (const c of m.porCuenta) {
    const izq = el('span', 't');
    izq.append(el('span', 'punto ' + claseProveedor(c)), el('span', '', c.cuenta));
    lc.append(fila(izq, c.datos));
  }
  const ls = $('estSesiones');
  ls.replaceChildren(cab());
  if (!m.porSesion.length) ls.append(el('div', 'lista-vacia', 'Sin uso en este periodo.'));
  for (const s of m.porSesion) {
    const izq = el('span', 't');
    const b = el('button', 'enlace', s.titulo);
    b.title = 'Abrir la sesión';
    b.addEventListener('click', () => abrirSesion(s.id));
    izq.append(b, el('span', 'tenue', [...s.cuentas].join(', ')));
    ls.append(fila(izq, s.datos));
  }
  const dias = $('estDias');
  dias.replaceChildren();
  const max = Math.max(1, ...m.porDia.map((d) => d.datos.tokens));
  const cada = Math.ceil(m.porDia.length / 10);
  m.porDia.forEach((d, i) => {
    const b = el('div', 'barra');
    b.style.height = `${(100 * d.datos.tokens) / max}%`;
    b.title = `${d.dia}: ${numero(d.datos.tokens)} tokens · ${numero(d.datos.turnos)} turnos` + (d.datos.coste != null ? ` · ${dinero(d.datos.coste, d.datos.moneda)}` : '');
    if (i % cada === 0 || m.porDia.length <= 10) b.append(el('span', '', d.dia.slice(5).split('-').reverse().join('/')));
    dias.append(b);
  });
}

// ------------------------------------------------------------------ equipos

let borradorEquipo = null;   // equipo nuevo aún sin guardar
const REGLAS = { Prioridad: 'Prioridad (en orden)', Rotacion: 'Rotación', MenosUsada: 'La menos usada', SuscripcionPrimero: 'Suscripciones primero' };

function pintarEquipos() {
  const l = $('listaEquipos');
  l.replaceChildren();
  const equipos = [...S.estado.equipos];
  if (borradorEquipo) equipos.push(borradorEquipo);
  if (!equipos.length) l.append(el('p', 'tenue', 'Añade cuentas: se crea solo el equipo «Principal» con todas.'));
  for (const eq of equipos) l.append(tarjetaEquipo(eq));
  $('chkResumen').checked = S.estado.resumenVivo !== false;
}

function tarjetaEquipo(eq) {
  const t = el('div', 'equipo');
  const cab = el('div', 'cab-eq');
  const nombre = el('input');
  nombre.type = 'text'; nombre.value = eq.nombre; nombre.maxLength = 40;
  const regla = el('select');
  for (const [v, txt] of Object.entries(REGLAS)) regla.append(new Option(txt, v));
  regla.value = eq.regla;
  const otro = el('input'); otro.type = 'checkbox'; otro.checked = eq.permiteOtroProveedor;
  const lOtro = el('label'); lOtro.append(otro, document.createTextNode(' pasar a otro proveedor'));
  const atras = el('input'); atras.type = 'number'; atras.min = 0; atras.max = 600; atras.value = eq.cuentaAtras;
  const lAtras = el('label'); lAtras.append(document.createTextNode('cuenta atrás '), atras, document.createTextNode(' s'));
  const der = el('span', 'der');
  const guardar = el('button', 'boton fino prim', 'Guardar');
  der.append(guardar);
  if (!eq.predeterminado) {
    const quitar = el('button', 'boton fino peligro', eq.id ? 'Quitar' : 'Descartar');
    quitar.addEventListener('click', () => {
      if (!eq.id) { borradorEquipo = null; pintarEquipos(); return; }
      if (window.confirm(`¿Quitar el equipo «${eq.nombre}»? Sus sesiones pasan al equipo predeterminado.`)) enviar({ tipo: 'equipoBorrar', id: eq.id });
    });
    der.append(quitar);
  } else der.prepend(el('span', 'tenue', 'predeterminado '));
  cab.append(nombre, regla, lOtro, lAtras, der);

  // Miembros: los del equipo en su orden y, debajo, las demás cuentas (fuera)
  let orden = eq.miembros.filter((id) => S.estado.cuentas.some((c) => c.id === id));
  const lista = el('div', 'miembros');
  const pintarMiembros = () => {
    lista.replaceChildren();
    const fuera = S.estado.cuentas.filter((c) => !orden.includes(c.id)).map((c) => c.id);
    [...orden, ...fuera].forEach((id) => {
      const c = S.estado.cuentas.find((x) => x.id === id);
      const dentro = orden.includes(id);
      const f = el('div', 'miembro' + (dentro ? '' : ' fuera'));
      const chk = el('input'); chk.type = 'checkbox'; chk.checked = dentro;
      chk.addEventListener('change', () => { orden = chk.checked ? [...orden, id] : orden.filter((x) => x !== id); pintarMiembros(); });
      f.append(el('span', 'num', dentro ? String(orden.indexOf(id) + 1) : ''), chk, el('span', 'punto ' + claseProveedor(c)), el('span', 'a', c.alias));
      const uso = textoUso(c);
      if (uso.texto) f.append(el('span', 'tenue', uso.texto));
      if (dentro) {
        const i = orden.indexOf(id);
        const sube = el('button', '', '▲'); sube.title = 'Subir'; sube.disabled = i === 0;
        sube.addEventListener('click', () => { [orden[i - 1], orden[i]] = [orden[i], orden[i - 1]]; pintarMiembros(); });
        const baja = el('button', '', '▼'); baja.title = 'Bajar'; baja.disabled = i === orden.length - 1;
        baja.addEventListener('click', () => { [orden[i + 1], orden[i]] = [orden[i], orden[i + 1]]; pintarMiembros(); });
        f.append(sube, baja);
      }
      lista.append(f);
    });
  };
  pintarMiembros();
  guardar.addEventListener('click', () => {
    enviar({ tipo: 'equipoGuardar', id: eq.id || null, nombre: nombre.value, regla: regla.value, permiteOtroProveedor: otro.checked,
             cuentaAtras: Number(atras.value) || 0, miembros: orden });
    if (!eq.id) borradorEquipo = null;
  });
  t.append(cab, lista);
  return t;
}

function anadirCuenta() {
  const { clase, id, cat } = tipoElegido();
  if (cat && cat.traducir) { $('fcAnadir').disabled = true; $('fcAnadir').textContent = 'Probando el modelo…'; }
  enviar({
    tipo: 'cuentaNueva',
    proveedor: clase === 'cat' ? cat.agente : id,
    tipoCuenta: clase === 'login' ? 'Login' : 'Api',
    alias: $('fcAlias').value,
    clave: $('fcClaveL').hidden ? null : $('fcClave').value,
    catalogo: cat ? cat.id : null,
    endpoint: $('fcEndpointL').hidden ? null : $('fcEndpoint').value,
    modelo: $('fcModeloL').hidden ? null : $('fcModelo').value,
  });
}

// ------------------------------------------------------------------ extensiones

let ext = null;
let instrTocadas = false;

function interruptor(activo, alCambiar, titulo) {
  const l = el('label', 'interr');
  l.title = titulo || '';
  const i = el('input'); i.type = 'checkbox'; i.checked = activo;
  i.addEventListener('change', () => alCambiar(i.checked));
  l.append(i, el('span'));
  return l;
}

function filaExt(nombre, detalle, acciones, opciones = {}) {
  const f = el('div', 'ext' + (opciones.inactivo ? ' inactivo' : ''));
  const n = el('div', 'n', nombre);
  for (const d of [].concat(detalle || [])) if (d) n.append(typeof d === 'string' ? el('small', '', d) : d);
  const acc = el('div', 'acc');
  acc.append(...acciones);
  f.append(n, acc);
  return f;
}

function botonQuitar(ruta, que) {
  const b = el('button', 'boton fino peligro', 'Quitar');
  b.addEventListener('click', () => {
    if (window.confirm(`¿Quitar ${que}? Se mueve a la papelera de MultiAgente (datos\\papelera).`)) enviar({ tipo: 'extQuitar', ruta });
  });
  return b;
}

function botonAbrir(ruta) {
  const b = el('button', 'boton fino', 'Abrir');
  b.addEventListener('click', () => enviar({ tipo: 'abrirRuta', ruta }));
  return b;
}

function pintarExtensiones() {
  if (!ext) return;
  // MCP
  const lm = $('listaMcp');
  lm.replaceChildren();
  if (!ext.mcp.length) lm.append(el('div', 'lista-vacia', 'Aún no hay servidores MCP. Añade uno o impórtalos de tu configuración de Claude, Codex o Gemini.'));
  for (const s of ext.mcp) {
    const que = s.tipo === 'stdio' ? [s.comando, ...(s.argumentos || [])].join(' ') : `${s.tipo.toUpperCase()} · ${s.url}`;
    const detalle = [que];
    if (s.origen) detalle.push(s.origen);
    if (s.agentes && s.agentes.length) detalle.push('solo ' + s.agentes.map((a) => ({ Claude: 'Claude Code' })[a] || a).join(', '));
    if (s.problema) detalle.push(el('small', 'problema', '⚠ ' + s.problema));
    const editar = el('button', 'boton fino', 'Editar');
    editar.addEventListener('click', () => formMcp(s));
    const quitar = el('button', 'boton fino peligro', 'Quitar');
    quitar.addEventListener('click', () => { if (window.confirm(`¿Quitar el servidor MCP «${s.nombre}» y sus claves?`)) enviar({ tipo: 'mcpBorrar', id: s.id }); });
    lm.append(filaExt(s.nombre, detalle, [interruptor(s.activo, (v) => enviar({ tipo: 'mcpActivar', id: s.id, activo: v }), 'Activo'), editar, quitar], { inactivo: !s.activo }));
  }
  // Skills
  const ls = $('listaSkills');
  ls.replaceChildren();
  if (!ext.skills.elementos.length) ls.append(el('div', 'lista-vacia', 'Sin skills todavía.'));
  for (const k of ext.skills.elementos) ls.append(filaExt(k.nombre, k.descripcion, [botonAbrir(k.ruta), botonQuitar(k.ruta, `la skill «${k.nombre}»`)]));
  // Órdenes, agentes, prompts
  const ce = $('carpetasExt');
  ce.replaceChildren();
  for (const c of ext.carpetas) {
    const h = el('h2', 'titulo-seccion', c.titulo);
    const imp = el('button', 'boton fino', 'Importar de mi configuración…'); imp.dataset.importar = c.clave;
    const an = el('button', 'boton fino', 'Añadir ficheros…'); an.dataset.anadir = c.clave;
    const ab = el('button', 'boton fino', 'Abrir carpeta'); ab.dataset.carpeta = c.clave;
    h.append(imp, an, ab);
    const caja = el('div', 'tarjeta'); caja.id = 'importables-' + c.clave; caja.hidden = true;
    const lista = el('div', 'lista');
    if (!c.elementos.length) lista.append(el('div', 'lista-vacia', 'Ninguno.'));
    for (const e of c.elementos) lista.append(filaExt(e.nombre, e.descripcion, [botonAbrir(e.ruta), botonQuitar(e.ruta, `«${e.nombre}»`)]));
    ce.append(h, caja, lista);
  }
  // Plugins
  const lp = $('listaPlugins');
  lp.replaceChildren();
  if (!ext.plugins.length) lp.append(el('div', 'lista-vacia', 'Ningún plugin instalado todavía.'));
  for (const p of ext.plugins)
    lp.append(filaExt(p.nombre, [p.tienda ? `de ${p.tienda}` : null, p.version ? 'versión ' + p.version : null].filter(Boolean).join(' · '),
      [interruptor(p.activo, (v) => enviar({ tipo: 'pluginActivar', id: p.id, activo: v }), 'Activo')], { inactivo: !p.activo }));
  // Instrucciones
  if (!instrTocadas) $('instrTexto').value = ext.instrucciones || '';
}

function carpetaDe(clave) {
  if (clave === 'skills') return ext.skills.carpeta;
  const c = ext.carpetas.find((x) => x.clave === clave);
  return c && c.carpeta;
}

function listaSeleccion(contenedor, titulo, elementos, textoBoton, alAceptar) {
  contenedor.hidden = false;
  contenedor.replaceChildren(el('h2', '', titulo));
  if (!elementos.length) {
    contenedor.append(el('p', '', 'No hay nada nuevo que traer.'));
  } else {
    const sel = el('div', 'sel-lista');
    for (const e of elementos) {
      const l = el('label');
      const c = el('input'); c.type = 'checkbox'; c.checked = true; c.value = e.valor;
      const t = el('span', '', e.nombre);
      if (e.detalle) t.append(el('br'), el('small', '', e.detalle));
      l.append(c, t);
      sel.append(l);
    }
    const ok = el('button', 'boton prim fino', textoBoton);
    ok.addEventListener('click', () => {
      const elegidos = [...sel.querySelectorAll('input:checked')].map((i) => i.value);
      if (elegidos.length) alAceptar(elegidos);
      contenedor.hidden = true;
    });
    contenedor.append(sel, ok, document.createTextNode(' '));
  }
  const cerrar = el('button', 'boton fino', 'Cerrar');
  cerrar.addEventListener('click', () => { contenedor.hidden = true; });
  contenedor.append(cerrar);
}

function formMcp(s) {
  const f = $('mcpForm');
  f.hidden = false;
  const nuevo = !s;
  s = s || { id: null, nombre: '', tipo: 'stdio', comando: 'npx', argumentos: [], url: '', valores: [], activo: true };
  f.replaceChildren(el('h2', '', nuevo ? 'Servidor MCP nuevo' : `Editar «${s.nombre}»`));
  const campos = el('div', 'campos');
  const nombre = el('input'); nombre.value = s.nombre; nombre.placeholder = 'p. ej. github';
  const tipo = el('select');
  for (const [v, t] of [['stdio', 'Programa en este equipo (stdio)'], ['http', 'Remoto por HTTP'], ['sse', 'Remoto por SSE (antiguo)']]) tipo.append(new Option(t, v));
  tipo.value = s.tipo;
  const comando = el('input'); comando.value = s.comando || ''; comando.placeholder = 'npx, uvx, node, docker, ruta de un .exe…';
  const args = el('textarea', 'editor'); args.rows = 3; args.value = (s.argumentos || []).join('\n'); args.placeholder = 'Un argumento por línea, p. ej.\n-y\n@modelcontextprotocol/server-github';
  const url = el('input'); url.value = s.url || ''; url.placeholder = 'https://…/mcp';
  const lab = (t, c) => { const l = el('label', '', t); l.append(c); return l; };
  const lComando = lab('Orden', comando), lArgs = lab('Argumentos', args), lUrl = lab('Dirección', url);
  const valores = el('div', 'valores');
  const tituloValores = el('label');
  const filas = [];
  const filaValor = (v) => {
    const n = el('input'); n.type = 'text'; n.value = v.nombre || ''; n.placeholder = 'NOMBRE';
    const val = el('input'); val.type = v.secreto ? 'password' : 'text'; val.value = v.valor || '';
    val.placeholder = v.secreto && v.guardado ? '(guardado; escribe para cambiarlo)' : 'valor (admite ${VARIABLE})';
    const sec = el('input'); sec.type = 'checkbox'; sec.checked = !!v.secreto; sec.title = 'Secreto: se guarda cifrado';
    sec.addEventListener('change', () => { val.type = sec.checked ? 'password' : 'text'; });
    const lSec = el('label'); lSec.append(sec, document.createTextNode(' secreto'));
    const q = el('button', 'boton fino', '✕');
    const fila = { n, val, sec, quitar: () => { [n, val, lSec, q].forEach((x) => x.remove()); filas.splice(filas.indexOf(fila), 1); } };
    q.addEventListener('click', fila.quitar);
    valores.append(n, val, lSec, q);
    filas.push(fila);
  };
  (s.valores || []).forEach(filaValor);
  const masValor = el('button', 'boton fino', '＋ Añadir');
  masValor.addEventListener('click', () => filaValor({}));
  const cambioTipo = () => {
    const stdio = tipo.value === 'stdio';
    lComando.hidden = lArgs.hidden = !stdio;
    lUrl.hidden = stdio;
    tituloValores.textContent = stdio ? 'Variables de entorno' : 'Cabeceras HTTP (p. ej. Authorization)';
  };
  tipo.addEventListener('change', cambioTipo);
  cambioTipo();
  const agentes = el('div', 'agentes-mcp');
  const marcas = ['Claude', 'Codex', 'Gemini'].map((a) => {
    const l = el('label', 'casilla');
    const c = el('input'); c.type = 'checkbox'; c.value = a;
    c.checked = !(s.agentes && s.agentes.length) || s.agentes.includes(a);
    l.append(c, document.createTextNode(' ' + ({ Claude: 'Claude Code', Codex: 'Codex', Gemini: 'Gemini' })[a]));
    agentes.append(l);
    return c;
  });
  const lAgentes = el('label', '', 'Lo usan');
  lAgentes.append(agentes, el('span', 'ayuda', 'Quita los agentes en los que no funciona (p. ej. si pide iniciar sesión en cada uno).'));
  campos.append(lab('Nombre', nombre), lab('Tipo', tipo), lComando, lArgs, lUrl, tituloValores, valores, masValor, lAgentes);
  const guardar = el('button', 'boton prim', 'Guardar');
  guardar.addEventListener('click', () => enviar({
    tipo: 'mcpGuardar',
    servidor: {
      id: s.id, nombre: nombre.value, tipo: tipo.value, comando: comando.value || null, url: url.value || null, activo: s.activo !== false,
      argumentos: args.value.split('\n').map((x) => x.trim()).filter(Boolean),
      valores: filas.map((x) => ({ nombre: x.n.value.trim(), valor: x.val.value || null, secreto: x.sec.checked })),
      origen: s.origen || null,
      agentes: marcas.every((c) => c.checked) ? null : marcas.filter((c) => c.checked).map((c) => c.value),
    },
  }));
  const cancelar = el('button', 'boton', 'Cancelar');
  cancelar.addEventListener('click', () => { f.hidden = true; });
  const pie = el('footer'); pie.append(el('span'), cancelar, guardar);
  f.append(campos, pie);
  nombre.focus();
}

// ------------------------------------------------------------------ mensajes de la aplicación

function alRecibir(m) {
  switch (m.tipo) {
    case 'estado': {
      const primero = !S.estado;
      S.estado = m;
      if (primero) {
        const ultima = leerLocal('ultimaSesion');
        if (ultima && todasLasSesiones().some((s) => s.id === ultima)) setTimeout(() => abrirSesion(ultima), 0);
      }
      pintarArbol();
      pintarInicio();
      pintarCuentas();
      if (S.vista === 'sesion') pintarCabecera();
      pintarConsumo();
      $('btnConsumoActualizar').disabled = false;
      break;
    }
    case 'conversacion': {
      const s = ses(m.sesionId);
      s.entradas = new Map(m.entradas.map((e) => [e.id, e]));
      s.orden = m.entradas.map((e) => e.id);
      if (m.agente) s.agente = m.agente;
      if (m.sesionId === S.actual) { pintarConversacion(); pintarAgente(); }
      break;
    }
    case 'entrada':
      actualizarEntrada(m.sesionId, m.entrada);
      if (m.sesionId === S.actual && m.entrada.tipo === 'herramienta') pedirCambiosLuego();
      break;
    case 'relevo':
      if (m.relevo) S.relevos[m.sesionId] = m.relevo; else delete S.relevos[m.sesionId];
      if (m.sesionId === S.actual) pintarRelevo();
      break;
    case 'agente':
      if (m.sesionId === S.actual && m.agente.estado === 'Esperando' && (ses(m.sesionId).agente || {}).estado !== 'Esperando') pedirCambiosLuego();
      ses(m.sesionId).agente = m.agente;
      pintarArbol();
      if (m.sesionId === S.actual) pintarAgente();
      break;
    case 'sesionAbierta':
      cajaNueva.enviarBtn.disabled = false;
      limpiarCaja(cajaNueva, cajaNueva.ta.value.trim());
      abrirSesion(m.id);
      break;
    case 'carpeta':
      if (m.para === 'nueva') { S.nueva.carpeta = m.ruta; S.nueva.proyectoId = null; pintarCamposNueva(); cajaNueva.ta.focus(); }
      if (m.para === 'proyecto') {
        $('dpRuta').value = m.ruta;
        if (!$('dpNombre').value.trim()) $('dpNombre').value = m.ruta.split('\\').filter(Boolean).pop() || '';
      }
      break;
    case 'adjuntos':
      anadirAdjuntos(S.cajaActiva || (S.vista === 'sesion' ? cajaSesion : cajaNueva), m.adjuntos);
      break;
    case 'modelos': {
      if (m.para === 'cuenta') {
        const dl = $('cmModelos');
        if (dl) dl.replaceChildren(...m.modelos.map((x) => new Option(x.nombre && x.nombre !== x.id ? `${x.id} — ${x.nombre}` : x.id, x.id)));
        document.querySelectorAll('.form-modelo button').forEach((b) => { b.disabled = false; });
        aviso(`${m.modelos.length} modelos del proveedor. Escribe para filtrar.`);
        break;
      }
      $('fcModelos').replaceChildren(...m.modelos.map((x) => new Option(x.nombre && x.nombre !== x.id ? `${x.id} — ${x.nombre}` : x.id, x.id)));
      $('fcModelosInfo').textContent = `${m.modelos.length} modelos disponibles. Escribe para filtrar o elige uno de la lista.`;
      $('fcListar').disabled = false;
      $('fcModelo').focus();
      break;
    }
    case 'cuentaModeloGuardado':
      aviso(m.texto + ' Las sesiones de esta cuenta siguen con él.');
      break;
    case 'modelosProbados': {
      const dl = $('cmModelos');
      if (dl) dl.replaceChildren(...m.funcionan.map((x) => new Option(x, x)));
      document.querySelectorAll('.form-modelo button').forEach((b) => { b.disabled = false; if (b.textContent === 'Probando modelos…') b.textContent = 'Probar cuáles funcionan'; });
      const entrada = document.querySelector('.form-modelo input');
      if (entrada && m.funcionan.length && !m.funcionan.includes(entrada.value)) entrada.value = m.funcionan[0];
      aviso(m.funcionan.length
        ? `Funcionan ${m.funcionan.length} de ${m.probados} modelos con tu cuenta: ${m.funcionan.join(', ')}. Elige uno y pulsa «Probar y guardar».`
        : `Ninguno de los ${m.probados} modelos de chat responde con tu cuenta ahora mismo. Revisa la clave o prueba más tarde.`);
      break;
    }
    case 'cuentaCreada':
      $('fcAnadir').disabled = false;
      $('fcAnadir').textContent = 'Añadir';
      $('fcAlias').value = '';
      delete $('fcAlias').dataset.tocado;
      $('fcClave').value = '';
      cambioTipo();
      aviso('Cuenta añadida.' + (tipoElegido().clase === 'login' ? ' Pulsa «Autorizar…» para iniciar sesión.' : ''));
      break;
    case 'estadisticas':
      pintarEstadisticas(m);
      break;
    case 'limitesGuardados':
      aviso('Límites guardados.');
      break;
    case 'extensiones':
      ext = m;
      pintarExtensiones();
      break;
    case 'mcpGuardado':
      $('mcpForm').hidden = true;
      aviso('Servidor MCP guardado. Lo tendrán las sesiones que abras a partir de ahora.');
      break;
    case 'mcpImportables':
      listaSeleccion($('mcpImportables'), 'Servidores MCP de tu configuración', m.servidores.map((s) => ({
        valor: s.nombre, nombre: `${s.nombre} (${s.tipo})`,
        detalle: (s.tipo === 'stdio' ? [s.comando, ...(s.argumentos || [])].join(' ') : s.url) + ' · ' + s.origen,
      })), 'Traer los marcados', (nombres) => enviar({ tipo: 'mcpImportar', nombres }));
      break;
    case 'extImportables':
      listaSeleccion($('importables-' + m.clave), 'De tu configuración de siempre (se copian; los originales no se tocan)',
        m.elementos.map((e) => ({ valor: e.ruta, nombre: e.nombre, detalle: [e.descripcion, e.ruta].filter(Boolean).join(' · ') })),
        'Copiar los marcados', (rutas) => enviar({ tipo: 'extImportar', clave: m.clave, rutas }));
      break;
    case 'instruccionesGuardadas':
      instrTocadas = false;
      aviso('Instrucciones guardadas: ya están en todas tus cuentas.');
      break;
    case 'instruccionesUsuario': {
      const c = $('instrUsuario');
      c.hidden = false;
      c.replaceChildren(el('h2', '', 'Tus instrucciones de siempre'));
      if (!m.lista.length) c.append(el('p', '', 'No se ha encontrado ninguna (~/.claude/CLAUDE.md, ~/.codex/AGENTS.md, ~/.gemini/GEMINI.md).'));
      for (const i of m.lista) {
        const b = el('button', 'boton fino', 'Añadir al final');
        b.addEventListener('click', () => {
          const t = $('instrTexto');
          t.value = (t.value.trim() ? t.value.trimEnd() + '\n\n' : '') + i.texto.trim() + '\n';
          instrTocadas = true;
          c.hidden = true;
        });
        c.append(filaExt(i.nombre, i.texto.slice(0, 160).replace(/\s+/g, ' ') + (i.texto.length > 160 ? '…' : ''), [b]));
      }
      const cerrar = el('button', 'boton fino', 'Cerrar');
      cerrar.addEventListener('click', () => { c.hidden = true; });
      c.append(cerrar);
      break;
    }
    case 'proyectoGuardado': {
      $('dlgProyecto').close();
      const p = S.estado.proyectos.find((x) => x.id === m.id);
      if (m.nuevo && p) { nuevaEnProyecto(p); aviso(`Proyecto «${p.nombre}» creado. Escribe para empezar su primera sesión.`); }
      break;
    }
    case 'cambios':
      recibirCambios(m);
      break;
    case 'historiales':
      IM.lista = m.lista;
      IM.cargando = false;
      pintarImportar();
      break;
    case 'historialesImportados': {
      $('dlgImportar').close();
      const n = m.importadas;
      aviso(n ? `${n === 1 ? 'Conversación importada' : n + ' conversaciones importadas'}: están en el lateral, en el proyecto de su carpeta.` : 'No se ha importado ninguna conversación.');
      for (const f of m.fallos.slice(0, 5)) aviso(f);
      if (m.fallos.length > 5) aviso(`Y ${m.fallos.length - 5} más que no se han podido importar (detalle en datos\\logs).`);
      break;
    }
    case 'cambioFichero':
      PC.difs.set(m.sesionId + '|' + m.ruta, m);
      if (m.sesionId === S.actual) pintarPanel();
      break;
    case 'aviso':
      if (m.origen === 'proyectoGuardar' && $('dlgProyecto').open) { $('dpError').textContent = m.texto; $('dpError').hidden = false; break; }
      if (m.origen === 'consumoActualizar') $('btnConsumoActualizar').disabled = false;
      if (m.origen === 'historialesImportar' && $('dlgImportar').open) {
        $('imError').textContent = m.texto; $('imError').hidden = false; pintarBotonImportar(); break;
      }
      aviso(m.texto);
      if (m.origen === 'sesionNueva') cajaNueva.enviarBtn.disabled = false;
      if (m.origen === 'modelos') { $('fcListar').disabled = false; document.querySelectorAll('.form-modelo button').forEach((b) => { b.disabled = false; }); }
      if (m.origen === 'cuentaModelo') document.querySelectorAll('.form-modelo .prim').forEach((b) => { b.disabled = false; b.textContent = 'Probar y guardar'; });
      if (m.origen === 'modelosProbar') document.querySelectorAll('.form-modelo button').forEach((b) => { b.disabled = false; if (b.textContent === 'Probando modelos…') b.textContent = 'Probar cuáles funcionan'; });
      if (m.origen === 'cuentaNueva') { $('fcAnadir').disabled = false; $('fcAnadir').textContent = 'Añadir'; }
      break;
  }
}

// ------------------------------------------------------------------ menús flotantes

function menuFlotante(donde, items) {
  const m = $('menuFlot');
  m.replaceChildren();
  for (const it of items) {
    if (it === '-') { m.append(el('div', 'sep')); continue; }
    if (it.titulo) { m.append(el('div', 'tit', it.titulo)); continue; }
    const b = el('button', (it.peligro ? 'peligro' : '') + (it.actual ? ' actual' : ''), (it.actual ? '✓ ' : '') + it.texto);
    b.setAttribute('role', 'menuitem');
    b.disabled = !!it.actual;
    b.addEventListener('click', () => { cerrarMenu(); it.accion(); });
    m.append(b);
  }
  m.hidden = false;
  let x, y;
  if (donde instanceof Event) { x = donde.clientX; y = donde.clientY; }
  else { const r = donde.getBoundingClientRect(); x = r.left; y = r.bottom + 4; }
  m.style.left = Math.max(8, Math.min(x, innerWidth - m.offsetWidth - 8)) + 'px';
  m.style.top = Math.max(8, Math.min(y, innerHeight - m.offsetHeight - 8)) + 'px';
  const primero = m.querySelector('button:not(:disabled)');
  if (primero) primero.focus();
}

function cerrarMenu() { $('menuFlot').hidden = true; }

$('menuFlot').addEventListener('keydown', (e) => {
  const botones = [...$('menuFlot').querySelectorAll('button:not(:disabled)')];
  const i = botones.indexOf(document.activeElement);
  if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
    e.preventDefault();
    const n = botones.length;
    botones[(i + (e.key === 'ArrowDown' ? 1 : n - 1) + n) % n].focus();
  } else if (e.key === 'Escape') { e.preventDefault(); cerrarMenu(); }
});
document.addEventListener('mousedown', (e) => { if (!$('menuFlot').hidden && !$('menuFlot').contains(e.target)) cerrarMenu(); });
window.addEventListener('blur', cerrarMenu);
window.addEventListener('resize', cerrarMenu);

// ------------------------------------------------------------------ proyectos

function nuevaEnProyecto(p) {
  S.nueva.carpeta = p.ruta;
  S.nueva.proyectoId = p.id;
  mostrar('inicio');
  pintarCamposNueva();
}

function menuProyecto(p, donde) {
  menuFlotante(donde, [
    { texto: 'Nueva sesión aquí', accion: () => nuevaEnProyecto(p) },
    { texto: 'Cambiar nombre o carpeta…', accion: () => dialogoProyecto(p) },
    { texto: 'Abrir la carpeta', accion: () => enviar({ tipo: 'abrirRuta', ruta: p.ruta }) },
    '-',
    { texto: 'Quitar el proyecto…', peligro: true, accion: () => {
      if (window.confirm(`¿Quitar el proyecto «${p.nombre}»? Sus sesiones no se borran: pasan a «Sin proyecto». La carpeta no se toca.`))
        enviar({ tipo: 'proyectoBorrar', id: p.id });
    } },
  ]);
}

function menuSesion(s, donde) {
  const destinos = S.estado.proyectos.map((p) => ({
    texto: p.nombre, actual: s.proyectoId === p.id,
    accion: () => enviar({ tipo: 'sesionMover', sesionId: s.id, proyectoId: p.id }),
  }));
  destinos.push({ texto: 'Sin proyecto', actual: !s.proyectoId, accion: () => enviar({ tipo: 'sesionMover', sesionId: s.id, proyectoId: null }) });
  menuFlotante(donde, [
    { texto: 'Cambiar el nombre…', accion: () => renombrarSesion(s) },
    { texto: 'Abrir la carpeta', accion: () => enviar({ tipo: 'abrirRuta', ruta: s.carpeta }) },
    '-',
    { titulo: 'Mover a' },
    ...destinos,
    '-',
    { texto: 'Archivar…', peligro: true, accion: () => archivarSesion(s.id) },
  ]);
}

function renombrarSesion(s) {
  const t = window.prompt('Nombre de la sesión:', s.titulo);
  if (t && t.trim() && t.trim() !== s.titulo) enviar({ tipo: 'sesionRenombrar', sesionId: s.id, titulo: t.trim() });
}

function archivarSesion(id) {
  if (!window.confirm('¿Archivar esta sesión? Se cierra su agente y deja de verse en la lista (la conversación se conserva).')) return;
  enviar({ tipo: 'archivar', sesionId: id });
  if (S.actual === id) mostrar('inicio');
}

let proyectoEditado = null;
function dialogoProyecto(p) {
  proyectoEditado = p ? p.id : null;
  $('dpTitulo').textContent = p ? `Proyecto «${p.nombre}»` : 'Nuevo proyecto';
  $('dpNombre').value = p ? p.nombre : '';
  $('dpRuta').value = p ? p.ruta : '';
  $('dpGuardar').textContent = p ? 'Guardar' : 'Crear';
  $('dpError').hidden = true;
  $('dlgProyecto').showModal();
  $('dpNombre').focus();
}

function guardarProyecto() {
  const nombre = $('dpNombre').value.trim();
  const ruta = $('dpRuta').value.trim();
  const error = !nombre ? 'Ponle un nombre al proyecto.' : !ruta ? 'Elige la carpeta de trabajo.' : null;
  $('dpError').hidden = !error;
  if (error) { $('dpError').textContent = error; return; }
  enviar({ tipo: 'proyectoGuardar', id: proyectoEditado, nombre, ruta });
}

// ------------------------------------------------------------------ importar conversaciones previas

const IM = { lista: [], marcadas: new Set(), cargando: false };

function abrirImportar() {
  IM.lista = [];
  IM.marcadas = new Set();
  IM.cargando = true;
  $('imLista').replaceChildren(el('div', 'pc-vacio', 'Buscando conversaciones en este PC…'));
  $('imError').hidden = true;
  pintarBotonImportar();
  $('dlgImportar').showModal();
  $('imBuscar').focus();
  enviar({ tipo: 'historiales' });
}

function visiblesImportar() {
  const q = $('imBuscar').value.trim().toLowerCase();
  const origen = $('imOrigen').value;
  const ocultar = $('imOcultar').checked;
  return IM.lista.filter((h) => (!origen || h.origen === origen) && (!ocultar || !h.importada) &&
    (!q || (h.titulo || '').toLowerCase().includes(q) || (h.carpeta || '').toLowerCase().includes(q)));
}

function fechaCorta(iso) {
  const d = new Date(iso);
  if (isNaN(d)) return '';
  const otroAnio = d.getFullYear() !== new Date().getFullYear();
  return d.toLocaleDateString('es-ES', { day: 'numeric', month: 'short', year: otroAnio ? 'numeric' : undefined }) + ' ' + hora(iso);
}

function pintarBotonImportar() {
  const n = IM.marcadas.size;
  $('imImportar').disabled = n === 0;
  $('imImportar').textContent = n ? `Importar ${n}` : 'Importar';
}

function pintarImportar() {
  const l = $('imLista');
  if (IM.cargando) return;
  l.replaceChildren();
  const vis = visiblesImportar();
  if (!IM.lista.length) l.append(el('div', 'pc-vacio', 'No hay conversaciones de Claude Code, Codex ni Gemini en este PC.'));
  else if (!vis.length) l.append(el('div', 'pc-vacio', 'Ninguna coincide con el filtro.'));
  for (const h of vis) {
    const no = h.importada || !h.existe;
    const fila = el('label', 'imp-fila' + (no ? ' no' : ''));
    const c = el('input');
    c.type = 'checkbox';
    c.checked = IM.marcadas.has(h.clave);
    c.disabled = no;
    c.addEventListener('change', () => {
      if (c.checked) IM.marcadas.add(h.clave); else IM.marcadas.delete(h.clave);
      pintarBotonImportar();
    });
    const t = el('div', 'imp-t');
    const cab = el('div', 'imp-cab');
    cab.append(el('span', 'punto p-' + h.origen), el('b', '', h.titulo || '(sin título)'));
    if (h.importada) cab.append(el('span', 'imp-nota', 'ya importada'));
    else if (!h.existe) cab.append(el('span', 'imp-nota', 'su carpeta ya no existe'));
    const meta = el('div', 'imp-meta', [h.nombreOrigen, h.carpeta || 'carpeta desconocida', fechaCorta(h.fecha),
      `${h.mensajes} ${h.mensajes === 1 ? 'mensaje' : 'mensajes'} tuyos`].join(' · '));
    meta.title = h.carpeta || '';
    t.append(cab, meta);
    fila.append(c, t);
    l.append(fila);
  }
  pintarBotonImportar();
}

// ------------------------------------------------------------------ visor de cambios

// datos: sesión → cambios de su carpeta (git); difs: «sesión|ruta» → diferencias de un fichero; abiertos: ficheros desplegados
const PC = { abierto: leerLocal('panelCambios') === '1', modo: leerLocal('panelModo') || 'git', datos: {}, difs: new Map(), abiertos: new Set(), espera: null };

const ESTADO_FICH = { M: ['M', 'cambiado'], A: ['A', 'añadido'], D: ['D', 'borrado'], '?': ['N', 'nuevo (git aún no lo sigue)'], T: ['T', 'cambio de tipo'] };
const LENGUAJES = {
  cs: 'csharp', js: 'javascript', mjs: 'javascript', cjs: 'javascript', jsx: 'javascript', ts: 'typescript', tsx: 'typescript',
  py: 'python', json: 'json', html: 'xml', htm: 'xml', xml: 'xml', csproj: 'xml', props: 'xml', xaml: 'xml', svg: 'xml',
  css: 'css', scss: 'scss', md: 'markdown', ps1: 'powershell', psm1: 'powershell', sql: 'sql', yml: 'yaml', yaml: 'yaml',
  sh: 'bash', java: 'java', go: 'go', rs: 'rust', c: 'c', h: 'c', cpp: 'cpp', hpp: 'cpp', php: 'php', rb: 'ruby', kt: 'kotlin',
  swift: 'swift', vb: 'vbnet', toml: 'ini', ini: 'ini',
};

function pedirCambios(id = S.actual) { if (id) enviar({ tipo: 'cambios', sesionId: id }); }
function pedirCambiosLuego() { clearTimeout(PC.espera); PC.espera = setTimeout(() => pedirCambios(), 900); }

function recibirCambios(m) {
  PC.datos[m.sesionId] = m;
  // Lo que ya se había abierto se vuelve a pedir: puede haber cambiado
  for (const k of [...PC.difs.keys()]) if (k.startsWith(m.sesionId + '|')) PC.difs.delete(k);
  if (m.sesionId === S.actual)
    for (const f of m.ficheros) if (PC.abiertos.has('git:' + f.ruta)) enviar({ tipo: 'cambioFichero', sesionId: m.sesionId, ruta: f.ruta });
  if (m.sesionId === S.actual) pintarPanel();
}

// Ediciones que han informado los agentes en la sesión, por fichero (en orden)
function edicionesDeSesion(id) {
  const porFichero = new Map();
  const s = S.sesiones[id];
  if (!s) return porFichero;
  for (const eid of s.orden) {
    const e = s.entradas.get(eid);
    if (!e || e.tipo !== 'herramienta') continue;
    for (const item of e.contenido || []) {
      if (!item || item.type !== 'diff' || !item.path) continue;
      if (!porFichero.has(item.path)) porFichero.set(item.path, []);
      porFichero.get(item.path).push(item);
    }
  }
  return porFichero;
}

function contarEdicion(item) {
  let mas = 0, menos = 0;
  for (const p of window.Diff ? Diff.diffLines(item.oldText || '', item.newText || '') : []) {
    if (p.added) mas += p.count; else if (p.removed) menos += p.count;
  }
  return { mas, menos };
}

function pintarBotonCambios() {
  const d = S.actual && PC.datos[S.actual];
  const n = d && d.git ? d.ficheros.length : S.actual ? edicionesDeSesion(S.actual).size : 0;
  $('cambiosN').textContent = n ? String(n) : '';
}

function pintarPanel() {
  const ver = PC.abierto && S.vista === 'sesion' && !!S.actual;
  $('panelCambios').hidden = !ver;
  $('btnCambios').classList.toggle('on', ver);
  pintarBotonCambios();
  if (!ver) return;
  const d = PC.datos[S.actual];
  const modo = d && !d.git ? 'sesion' : PC.modo;
  for (const b of $('pcModo').children) {
    b.classList.toggle('on', b.dataset.modo === modo);
    b.disabled = b.dataset.modo === 'git' && !!d && !d.git;
  }
  const cuerpo = $('pcCuerpo');
  const arriba = cuerpo.scrollTop;
  cuerpo.replaceChildren();
  if (modo === 'git') {
    if (!d) { $('pcInfo').textContent = ''; cuerpo.append(el('div', 'pc-vacio', 'Mirando los cambios…')); return; }
    const mas = d.ficheros.reduce((a, f) => a + (f.mas || 0), 0);
    const menos = d.ficheros.reduce((a, f) => a + (f.menos || 0), 0);
    $('pcInfo').textContent = [d.rama && d.rama !== 'HEAD' ? d.rama : null, `${d.ficheros.length} ficheros`, d.ficheros.length ? `+${mas} −${menos}` : null].filter(Boolean).join(' · ');
    $('pcInfo').title = d.raiz ? `Repositorio: ${d.raiz}` : '';
    if (!d.ficheros.length) cuerpo.append(el('div', 'pc-vacio', 'No hay cambios sin confirmar en la carpeta de la sesión.'));
    for (const f of d.ficheros) cuerpo.append(filaCambio(f));
    if (d.recortado) cuerpo.append(el('div', 'pc-vacio', 'Hay más ficheros cambiados: se muestran los primeros.'));
  } else {
    const porFichero = edicionesDeSesion(S.actual);
    $('pcInfo').textContent = `${porFichero.size} ficheros editados`;
    $('pcInfo').title = '';
    if (d && !d.git) cuerpo.append(el('div', 'pc-nota', 'La carpeta no está en un repositorio git: se ven las ediciones que han hecho los agentes en esta sesión.'));
    if (!porFichero.size) cuerpo.append(el('div', 'pc-vacio', 'Los agentes aún no han editado ficheros en esta sesión.'));
    for (const [ruta, items] of porFichero) cuerpo.append(filaEdiciones(ruta, items));
  }
  cuerpo.scrollTop = arriba;
}

function rutaRelativa(ruta) {
  const s = sesionActual();
  const base = s ? s.carpeta.replace(/[\\/]+$/, '') + '\\' : '';
  return base && ruta.toLowerCase().startsWith(base.toLowerCase()) ? ruta.substring(base.length) : ruta;
}

function nombreRuta(ruta) {
  const partes = ruta.split(/[\\/]/);
  const n = el('span', 'pc-ruta');
  n.append(el('b', '', partes.pop()));
  if (partes.length) n.append(el('span', 'tenue', ' ' + partes.join('\\')));
  return n;
}

function cifras(mas, menos, binario) {
  const c = el('span', 'pc-cifras');
  if (binario) c.append(el('span', 'tenue', 'binario'));
  else c.append(el('span', 'mas', '+' + (mas || 0)), el('span', 'menos', '−' + (menos || 0)));
  return c;
}

function cabFichero(clave, ruta, etiqueta, mas, menos, binario, alAbrir) {
  const abierto = PC.abiertos.has(clave);
  const cab = el('button', 'pc-cab');
  cab.append(el('span', 'flecha', abierto ? '▾' : '▸'), etiqueta, nombreRuta(ruta), cifras(mas, menos, binario));
  cab.addEventListener('click', () => {
    if (PC.abiertos.has(clave)) PC.abiertos.delete(clave);
    else { PC.abiertos.add(clave); if (alAbrir) alAbrir(); }
    pintarPanel();
  });
  return cab;
}

function filaCambio(f) {
  const clave = 'git:' + f.ruta;
  const caja = el('div', 'pc-fich');
  const [letra, texto] = ESTADO_FICH[f.estado] || [f.estado, ''];
  const etiqueta = el('span', 'pc-est e-' + letra, letra);
  etiqueta.title = texto;
  const llave = S.actual + '|' + f.ruta;
  caja.append(cabFichero(clave, f.ruta, etiqueta, f.mas, f.menos, f.binario,
    () => { if (!PC.difs.has(llave)) enviar({ tipo: 'cambioFichero', sesionId: S.actual, ruta: f.ruta }); }));
  if (!PC.abiertos.has(clave)) return caja;
  const d = PC.difs.get(llave);
  const cuerpo = el('div', 'pc-dif');
  if (!d) cuerpo.append(el('div', 'pc-vacio', 'Cargando…'));
  else if (d.binario) cuerpo.append(el('div', 'pc-vacio', 'Fichero binario: no se pueden mostrar las diferencias.'));
  else if (!d.texto) cuerpo.append(el('div', 'pc-vacio', 'Sin diferencias de texto.'));
  else cuerpo.append(vistaUnificada(d.texto, f.ruta));
  if (d && d.recortado) cuerpo.append(el('div', 'pc-vacio', 'Diferencias muy largas: se muestra el principio.'));
  const s = sesionActual();
  if (s && f.estado !== 'D') {
    const pie = el('div', 'pc-pie');
    pie.append(enlaceRuta(s.carpeta.replace(/[\\/]+$/, '') + '\\' + f.ruta, 'Abrir el fichero'));
    cuerpo.append(pie);
  }
  caja.append(cuerpo);
  return caja;
}

function filaEdiciones(ruta, items) {
  const clave = 'sesion:' + ruta;
  const caja = el('div', 'pc-fich');
  let mas = 0, menos = 0;
  for (const it of items) { const c = contarEdicion(it); mas += c.mas; menos += c.menos; }
  const etiqueta = el('span', 'pc-est e-M', String(items.length));
  etiqueta.title = items.length === 1 ? 'una edición' : `${items.length} ediciones`;
  caja.append(cabFichero(clave, rutaRelativa(ruta), etiqueta, mas, menos, false));
  if (!PC.abiertos.has(clave)) return caja;
  const cuerpo = el('div', 'pc-dif');
  items.forEach((it, i) => {
    if (items.length > 1) cuerpo.append(el('div', 'pc-paso', `Edición ${i + 1}`));
    cuerpo.append(vistaUnificada(unificada(it.oldText || '', it.newText || ''), ruta, false));
  });
  const pie = el('div', 'pc-pie');
  pie.append(enlaceRuta(ruta, 'Abrir el fichero'));
  cuerpo.append(pie);
  caja.append(cuerpo);
  return caja;
}

// Diferencias de una edición en el mismo formato que git (sin números de línea reales: el agente da solo el fragmento)
function unificada(viejo, nuevo) {
  const lineas = [];
  for (const p of window.Diff ? Diff.diffLines(viejo, nuevo) : [{ value: nuevo, added: true }]) {
    const marca = p.added ? '+' : p.removed ? '-' : ' ';
    for (const l of p.value.replace(/\n$/, '').split('\n')) lineas.push(marca + l);
  }
  return lineas.join('\n');
}

function lenguajeDe(ruta) {
  const ext = (ruta.split('.').pop() || '').toLowerCase();
  const l = LENGUAJES[ext];
  return l && window.hljs && hljs.getLanguage(l) ? l : null;
}

function vistaUnificada(texto, ruta, numeros = true) {
  const t = el('div', 'udif');
  const lang = lenguajeDe(ruta);
  const lineas = texto.replace(/\n$/, '').split('\n');
  const max = 3000;
  let a = 1, b = 1;
  for (const l of lineas.slice(0, max)) {
    const h = /^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@(.*)$/.exec(l);
    if (h) {
      a = Number(h[1]); b = Number(h[2]);
      const f = el('div', 'h');
      f.append(el('span', 'n'), el('span', 'n'), el('span', 's'), el('span', 'c', h[3].trim() || '⋯'));
      t.append(f);
      continue;
    }
    if (l.startsWith('\\')) continue; // «\ No newline at end of file»
    const marca = l[0];
    const f = el('div', marca === '+' ? 'mas' : marca === '-' ? 'menos' : '');
    const na = marca === '+' ? '' : String(a++);
    const nb = marca === '-' ? '' : String(b++);
    const c = el('span', 'c');
    const codigo = l.substring(1);
    if (lang) { try { c.innerHTML = hljs.highlight(codigo, { language: lang, ignoreIllegals: true }).value; } catch { c.textContent = codigo; } }
    else c.textContent = codigo;
    f.append(el('span', 'n', numeros ? na : ''), el('span', 'n', numeros ? nb : ''), el('span', 's', marca === ' ' ? '' : marca === '-' ? '−' : '+'), c);
    t.append(f);
  }
  if (lineas.length > max) t.append(el('div', 'pc-vacio', `… ${lineas.length - max} líneas más`));
  return t;
}

// Ancho del visor: se arrastra su borde izquierdo
(() => {
  const ancho = Number(leerLocal('panelAncho'));
  if (ancho > 0) $('panelCambios').style.width = ancho + 'px';
  $('panelAsa').addEventListener('mousedown', (e) => {
    e.preventDefault();
    document.body.classList.add('redimensionando');
    const mover = (ev) => {
      const w = Math.max(320, Math.min(window.innerWidth - 272 - 380, window.innerWidth - ev.clientX));
      $('panelCambios').style.width = w + 'px';
    };
    const soltar = () => {
      document.removeEventListener('mousemove', mover);
      document.removeEventListener('mouseup', soltar);
      document.body.classList.remove('redimensionando');
      guardarLocal('panelAncho', String(parseInt($('panelCambios').style.width, 10) || ''));
    };
    document.addEventListener('mousemove', mover);
    document.addEventListener('mouseup', soltar);
  });
})();

// ------------------------------------------------------------------ consumo y límites

function nombreProveedor(c) {
  const p = S.estado.proveedores.find((x) => x.id === c.proveedor);
  return (p ? p.nombre : c.proveedor) + (c.tipo === 'Api' ? ' · API' : ' · suscripción');
}

function duracion(ms) {
  const min = Math.max(1, Math.round(ms / 60000));
  if (min < 60) return `${min} min`;
  const h = Math.floor(min / 60);
  if (h < 48) return `${h} h ${min % 60} min`;
  return `${Math.round(h / 24)} días`;
}

function cuandoReinicia(iso, conDia) {
  const d = new Date(iso);
  const falta = d - Date.now();
  if (isNaN(d) || falta <= 0) return 'en breve';
  const mismoDia = d.toDateString() === new Date().toDateString();
  const dia = conDia && !mismoDia ? d.toLocaleDateString('es-ES', { weekday: 'long' }) + ' ' : '';
  return `${dia}a las ${hora(iso)} (en ${duracion(falta)})`;
}

function haceCuanto(iso) {
  const min = Math.round((Date.now() - new Date(iso)) / 60000);
  return min < 1 ? 'ahora mismo' : min < 60 ? `hace ${min} min` : `a las ${hora(iso)}`;
}

function barraConsumo(nombre, pct, umbral, nota, notaDer, aviso = umbral - 15) {
  const f = el('div', 'cs-fila');
  const cab = el('div', 'cs-tit');
  cab.append(el('span', '', nombre), el('b', '', `${Math.round(pct)} %`));
  const barra = el('div', 'cs-barra' + (pct >= umbral ? ' alto' : pct >= aviso ? ' medio' : ''));
  const relleno = el('i');
  relleno.style.width = Math.max(0, Math.min(100, pct)) + '%';
  const marca = el('span', 'cs-umbral');
  marca.style.left = Math.min(100, umbral) + '%';
  barra.append(relleno, marca);
  barra.setAttribute('role', 'meter');
  barra.setAttribute('aria-valuenow', String(Math.round(pct)));
  barra.setAttribute('aria-label', nombre);
  const pie = el('div', 'cs-sub');
  pie.append(el('span', '', nota || ''), el('span', '', notaDer || ''));
  f.append(cab, barra, pie);
  return f;
}

function filaMes(c) {
  const pres = c.limites && c.limites.presupuesto;
  const gasto = c.gastoMes;
  const fin = new Date(new Date().getFullYear(), new Date().getMonth() + 1, 1).toLocaleDateString('es-ES', { day: 'numeric', month: 'long' });
  if (pres) {
    const f = barraConsumo('Este mes', 100 * (gasto || 0) / pres, 100, `${dinero(gasto || 0, c.moneda)} de ${dinero(pres, c.moneda)} · aviso al 80 %`, `empieza de cero el ${fin}`, 80);
    return f;
  }
  const f = el('div', 'cs-fila');
  const cab = el('div', 'cs-tit');
  cab.append(el('span', '', 'Este mes'), el('b', '', gasto != null ? dinero(gasto, c.moneda) : '—'));
  f.append(cab, el('div', 'cs-sub', gasto != null ? 'Sin presupuesto: pon uno en «Límites…» para que avise y pase a otra cuenta al agotarlo.'
    : c.tipo === 'Login' ? 'Suscripción: el agente no informa de coste.' : 'El agente aún no ha informado de coste.'));
  return f;
}

function notaSinConsumo(c) {
  if (!c.listo) return 'Falta instalar su agente.';
  if (!c.autorizada) return 'Autoriza la cuenta para ver su consumo.';
  if (c.proveedor === 'Claude' && c.tipo === 'Login') return 'Aún no se ha podido leer su consumo. Pulsa «Actualizar ahora».';
  if (c.tipo === 'Api') return 'Cuenta de API: no tiene bloques de 5 horas ni semanales; se paga por uso.';
  return `${(S.estado.proveedores.find((x) => x.id === c.proveedor) || {}).nombre || c.proveedor} no le da a MultiAgente el % de sus límites. Si llega al tope, el equipo pasa a otra cuenta al recibir el aviso de límite.`;
}

function tarjetaConsumo(c) {
  const t = el('div', 'tarjeta consumo');
  const u = c.consumo || {};
  const cab = el('div', 'cs-cab');
  const [clase, estado] = !c.listo ? ['falta', 'falta instalar'] : !c.autorizada ? ['falta', 'falta autorizar']
    : u.enfriandoHasta ? ['falta', `agotada · vuelve a las ${hora(u.enfriandoHasta)}`]
    : c.sinMargen ? ['falta', 'sin margen'] : ['ok', 'con margen'];
  cab.append(el('span', 'punto ' + claseProveedor(c)), el('b', '', c.alias), el('span', 'tenue', c.catalogoNombre || nombreProveedor(c)),
             el('span', 'cs-estado ' + clase, estado));
  t.append(cab);
  if (u.enfriandoHasta && u.motivo) t.append(el('div', 'cs-nota', u.motivo));
  const lim = c.limites || {};
  if (u.pct5h != null) t.append(barraConsumo('Bloque de 5 horas', u.pct5h, lim.umbral5h, u.reinicio5h ? `Se reinicia ${cuandoReinicia(u.reinicio5h)}` : '', `cambia de cuenta al ${lim.umbral5h} %`));
  if (u.pctSemana != null) t.append(barraConsumo('Semanal', u.pctSemana, lim.umbralSemana, u.reinicioSemana ? `Se reinicia ${cuandoReinicia(u.reinicioSemana, true)}` : '', `cambia de cuenta al ${lim.umbralSemana} %`));
  if (u.pct5h == null && u.pctSemana == null) t.append(el('div', 'cs-nota', notaSinConsumo(c)));
  t.append(filaMes(c));
  const pie = el('div', 'cs-pie');
  pie.append(el('span', 'tenue', u.momento ? `Consumo leído ${haceCuanto(u.momento)}` : ''));
  const b = el('button', 'boton fino', 'Límites…');
  b.addEventListener('click', () => mostrar('cuentas'));
  pie.append(b);
  t.append(pie);
  return t;
}

function pintarConsumo() {
  if (S.vista !== 'consumo' || !S.estado) return;
  const lista = $('consumoCuentas');
  lista.replaceChildren();
  if (!S.estado.cuentas.length) lista.append(el('div', 'lista-vacia', 'Añade una cuenta para ver su consumo.'));
  for (const c of S.estado.cuentas) lista.append(tarjetaConsumo(c));
}
setInterval(pintarConsumo, 30000);

// ------------------------------------------------------------------ arranque

$('conv').addEventListener('wheel', () => requestAnimationFrame(() => { pegado = cercaDelFinal(); }), { passive: true });
$('conv').addEventListener('keydown', () => requestAnimationFrame(() => { pegado = cercaDelFinal(); }));
$('conv').addEventListener('mousedown', () => { const fin = () => { pegado = cercaDelFinal(); document.removeEventListener('mouseup', fin); }; document.addEventListener('mouseup', fin); });
new ResizeObserver(() => bajar()).observe($('conv'));
new ResizeObserver(() => bajar()).observe($('entradas'));

const cajaNueva = crearCaja($('abajoInicio'), 'nueva');
const cajaSesion = crearCaja($('abajoSesion'), 'sesion');

$('btnNueva').addEventListener('click', () => mostrar('inicio'));
$('btnCuentas').addEventListener('click', () => mostrar('cuentas'));
$('btnIrCuentas').addEventListener('click', () => mostrar('cuentas'));
$('btnRecomprobar').addEventListener('click', () => enviar({ tipo: 'recomprobar' }));
$('btnInstalar').addEventListener('click', () => enviar({ tipo: 'instalarRequisitos' }));
$('sesCarpeta').addEventListener('click', () => { const s = sesionActual(); if (s) enviar({ tipo: 'abrirRuta', ruta: s.carpeta }); });
$('sesCuenta').addEventListener('change', (e) => {
  const s = sesionActual();
  if (!s) return;
  const quien = e.target.value;
  const [clase, id] = quien.split(':');
  const texto = clase === 'equipo'
    ? `¿Pasar la sesión a «Automático · ${(S.estado.equipos.find((x) => x.id === id) || {}).nombre}»? El equipo elegirá la cuenta y la relevará cuando se agote.`
    : `¿Seguir esta sesión con «${(S.estado.cuentas.find((x) => x.id === id) || {}).alias}» fija? La conversación continúa; si es de otro proveedor, se le pasa un resumen de lo hecho.`;
  if (window.confirm(texto)) enviar({ tipo: 'cambiarCuenta', sesionId: s.id, quien });
  else pintarCabecera();
});
$('btnArchivar').addEventListener('click', () => { if (S.actual) archivarSesion(S.actual); });
$('sesTitulo').addEventListener('dblclick', () => { const s = sesionActual(); if (s) renombrarSesion(s); });
$('btnCambios').addEventListener('click', () => {
  PC.abierto = !PC.abierto;
  guardarLocal('panelCambios', PC.abierto ? '1' : '0');
  if (PC.abierto) pedirCambios();
  pintarPanel();
});
$('pcCerrar').addEventListener('click', () => { PC.abierto = false; guardarLocal('panelCambios', '0'); pintarPanel(); });
$('pcRecargar').addEventListener('click', () => pedirCambios());
$('pcModo').addEventListener('click', (e) => {
  const b = e.target.closest('button');
  if (!b || b.disabled) return;
  PC.modo = b.dataset.modo;
  guardarLocal('panelModo', PC.modo);
  pintarPanel();
});
$('btnProyectoNuevo').addEventListener('click', (e) => menuFlotante(e.currentTarget, [
  { texto: 'Nuevo proyecto…', accion: () => dialogoProyecto(null) },
  { texto: 'Importar conversaciones de este PC…', accion: abrirImportar },
]));
$('imBuscar').addEventListener('input', pintarImportar);
$('imOrigen').addEventListener('change', pintarImportar);
$('imOcultar').addEventListener('change', pintarImportar);
$('imTodas').addEventListener('click', () => {
  for (const h of visiblesImportar()) if (!h.importada && h.existe) IM.marcadas.add(h.clave);
  pintarImportar();
});
$('imCerrar').addEventListener('click', () => $('dlgImportar').close());
$('imImportar').addEventListener('click', () => {
  if (!IM.marcadas.size) return;
  $('imImportar').disabled = true;
  $('imImportar').textContent = 'Importando…';
  $('imError').hidden = true;
  enviar({ tipo: 'historialesImportar', claves: [...IM.marcadas] });
});
$('dpElegir').addEventListener('click', () => enviar({ tipo: 'elegirCarpeta', para: 'proyecto', inicial: $('dpRuta').value }));
$('dpGuardar').addEventListener('click', guardarProyecto);
$('dpCancelar').addEventListener('click', () => $('dlgProyecto').close());
$('dpNombre').addEventListener('keydown', (e) => { if (e.key === 'Enter') guardarProyecto(); });
$('btnConsumo').addEventListener('click', () => mostrar('consumo'));
$('btnConsumoActualizar').addEventListener('click', () => { $('btnConsumoActualizar').disabled = true; enviar({ tipo: 'consumoActualizar' }); });
$('fcTipo').addEventListener('change', () => { delete $('fcAlias').dataset.tocado; cambioTipo(); });
$('fcAlias').addEventListener('input', () => { $('fcAlias').dataset.tocado = '1'; });
$('fcVerClave').addEventListener('click', () => { const c = $('fcClave'); c.type = c.type === 'password' ? 'text' : 'password'; });
$('fcDondeClave').addEventListener('click', () => { if ($('fcDondeClave').dataset.url) enviar({ tipo: 'abrirEnlace', url: $('fcDondeClave').dataset.url }); });
$('fcListar').addEventListener('click', () => {
  const { cat } = tipoElegido();
  if (!cat) return;
  $('fcListar').disabled = true;
  $('fcModelosInfo').textContent = 'Pidiendo la lista al proveedor…';
  enviar({ tipo: 'modelos', para: 'form', catalogo: cat.id, endpoint: $('fcEndpoint').value, clave: $('fcClave').value });
});
$('fcAnadir').addEventListener('click', anadirCuenta);
$('btnNuevoEquipo').addEventListener('click', () => {
  borradorEquipo = { id: null, nombre: 'Equipo ' + (S.estado.equipos.length + 1), regla: 'Prioridad', permiteOtroProveedor: true,
                     cuentaAtras: 60, predeterminado: false, miembros: [] };
  pintarEquipos();
});
$('btnExtensiones').addEventListener('click', () => mostrar('extensiones'));
$('btnEstadisticas').addEventListener('click', () => mostrar('estadisticas'));
$('periodo').addEventListener('click', (e) => {
  const b = e.target.closest('button');
  if (!b) return;
  periodoDias = Number(b.dataset.dias);
  [...$('periodo').children].forEach((x) => x.classList.toggle('on', x === b));
  enviar({ tipo: 'estadisticas', dias: periodoDias });
});
$('btnMcpNuevo').addEventListener('click', () => formMcp(null));
$('btnMcpImportar').addEventListener('click', () => enviar({ tipo: 'mcpImportables' }));
$('btnSkillNueva').addEventListener('click', () => {
  const nombre = window.prompt('Nombre de la skill nueva (p. ej. revisar-codigo):');
  if (!nombre) return;
  const descripcion = window.prompt('¿Cuándo debe usarla el agente? (una frase)') || '';
  enviar({ tipo: 'skillNueva', nombre, descripcion });
});
$('vExtensiones').addEventListener('click', (e) => {
  const b = e.target.closest('button');
  if (!b || !ext) return;
  if (b.dataset.importar) enviar({ tipo: 'extImportables', clave: b.dataset.importar });
  else if (b.dataset.anadir) enviar({ tipo: 'extAnadir', clave: b.dataset.anadir });
  else if (b.dataset.carpeta) enviar({ tipo: 'abrirRuta', ruta: carpetaDe(b.dataset.carpeta) });
});
$('instrTexto').addEventListener('input', () => { instrTocadas = true; });
$('btnInstrGuardar').addEventListener('click', () => enviar({ tipo: 'instruccionesGuardar', texto: $('instrTexto').value }));
$('btnInstrUsuario').addEventListener('click', () => enviar({ tipo: 'instruccionesUsuario' }));
$('chkResumen').addEventListener('change', (e) => enviar({ tipo: 'ajuste', clave: 'resumenVivo', valor: e.target.checked ? null : 'no' }));

document.addEventListener('keydown', (e) => {
  if (e.ctrlKey && (e.key === 'n' || e.key === 'N')) { e.preventDefault(); mostrar('inicio'); }
});
window.addEventListener('focus', () => { enviar({ tipo: 'recomprobar' }); if (S.vista === 'sesion') pedirCambiosLuego(); });
if (puente) puente.addEventListener('message', (e) => alRecibir(e.data));

mostrar('inicio');
enviar({ tipo: 'listo' });
