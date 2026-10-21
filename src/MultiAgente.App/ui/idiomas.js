'use strict';
// Idioma de la interfaz. Los textos se escriben en castellano en app.js con trad('…') y en index.html tal cual; para
// inglés y catalán se buscan en idiomas/en.js e idiomas/ca.js (la clave es el texto en castellano). Los mensajes que
// compone la aplicación (C#) llegan en castellano y se traducen con traducir(), que reconoce también los que llevan datos.

const IDIOMAS = { es: 'Español', en: 'English', ca: 'Català' };

const IDIOMA = (() => {
  let g = null;
  try { g = localStorage.getItem('idioma'); } catch { /* sin almacenamiento */ }
  if (g && IDIOMAS[g]) return g;
  const n = (navigator.language || 'es').toLowerCase();
  return n.startsWith('ca') ? 'ca' : n.startsWith('es') ? 'es' : 'en';
})();

const LOCALE = { es: 'es-ES', en: 'en-GB', ca: 'ca-ES' }[IDIOMA];
const DICCIONARIO = IDIOMA === 'es' ? {} : (window['TEXTOS_' + IDIOMA] || {});

// Texto fijo de la interfaz; {0}, {1}… se sustituyen por los datos.
function trad(texto, ...args) {
  const r = DICCIONARIO[texto] ?? texto;
  return args.length ? r.replace(/\{(\d+)\}/g, (m, i) => (args[i] ?? m)) : r;
}

// Claves con datos convertidas en patrones, de la más concreta (más texto fijo) a la más general.
let patrones = null;
function crearPatrones() {
  patrones = Object.keys(DICCIONARIO).filter((k) => /\{\d\}/.test(k)).map((k) => {
    const orden = [];
    const fuente = k.split(/(\{\d\})/).map((p) => {
      const m = /^\{(\d)\}$/.exec(p);
      if (m) { orden.push(+m[1]); return '([\\s\\S]+?)'; }
      return p.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    }).join('');
    return { k, re: new RegExp('^' + fuente + '$'), orden, fijo: k.replace(/\{\d\}/g, '').length };
  }).sort((a, b) => b.fijo - a.fijo);
}

// Mensaje que llega de la aplicación: exacto, o por patrón (traduciendo también los trozos que lleva dentro).
function traducir(texto, hondo = 0) {
  if (!texto || IDIOMA === 'es' || typeof texto !== 'string') return texto;
  if (DICCIONARIO[texto] != null) return DICCIONARIO[texto];
  if (hondo > 3) return texto;
  if (!patrones) crearPatrones();
  for (const p of patrones) {
    const m = p.re.exec(texto);
    if (!m) continue;
    const args = [];
    p.orden.forEach((n, i) => { args[n] = traducir(m[i + 1], hondo + 1); });
    return DICCIONARIO[p.k].replace(/\{(\d+)\}/g, (x, i) => (args[i] ?? x));
  }
  return texto;
}

// Textos fijos de index.html (incluidas las plantillas): nodos de texto y title, placeholder y aria-label.
function traducirDocumento(raiz) {
  if (IDIOMA === 'es') return;
  const recorrer = (nodo) => {
    for (const n of nodo.childNodes) {
      if (n.nodeType === 3) {
        const limpio = n.nodeValue.replace(/\s+/g, ' ').trim();
        const t = limpio && DICCIONARIO[limpio];
        if (t != null) {
          const ini = /^\s/.test(n.nodeValue) ? ' ' : '', fin = /\s$/.test(n.nodeValue) ? ' ' : '';
          n.nodeValue = ini + t + fin;
        }
      } else if (n.nodeType === 1) {
        if (n.closest && (n.closest('[translate="no"]') || n.tagName === 'SCRIPT' || n.tagName === 'STYLE' || n.namespaceURI === 'http://www.w3.org/2000/svg')) continue;
        for (const a of ['title', 'placeholder', 'aria-label']) {
          const v = n.getAttribute(a);
          if (v && DICCIONARIO[v] != null) n.setAttribute(a, DICCIONARIO[v]);
        }
        recorrer(n.tagName === 'TEMPLATE' ? n.content : n);
      }
    }
  };
  recorrer(raiz);
}

document.documentElement.lang = IDIOMA;
traducirDocumento(document.body);
