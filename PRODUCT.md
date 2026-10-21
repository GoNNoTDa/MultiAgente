# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

(Aplicación de escritorio para Windows: anfitrión .NET WinForms con WebView2; toda la interfaz es HTML/CSS/JS en
`src/MultiAgente.App/ui`.)

## Users

- Personas que trabajan a diario con agentes de IA (Claude Code, Codex, Gemini CLI y modelos por API) y tienen varias
  cuentas o suscripciones: perfiles técnicos y también **gente no técnica** que usa los agentes para tareas de oficina.
  Hay que poder entenderlo todo sin saber qué es una CLI, un token o una API.
- Trabajan en Windows, en su propio PC, a menudo durante horas en la misma sesión.

## Product Purpose

Una sola ventana, como la aplicación de escritorio de Claude Code, para trabajar con varios agentes y varias cuentas de
varios proveedores. Éxito: el trabajo **no se para** cuando una cuenta llega a su límite y el usuario no tiene que saltar
entre programas ni repetir el contexto.

## Positioning

Las dos cosas por igual, juntas:
- **Todo en un sitio**: Claude Code, Codex, Gemini y proveedores por API (GLM, DeepSeek, NVIDIA…) en la misma ventana,
  con proyectos, extensiones (MCP, skills) e historiales compartidos entre cuentas.
- **Nunca se para**: equipos de cuentas que reparten el trabajo y se relevan automáticamente al agotarse una, con la misma
  conversación (mismo proveedor) o con un traspaso de contexto (otro proveedor).

Es neutral entre proveedores: no pertenece a ninguno ni debe parecerse a la app de uno solo.

## Operating Context

- Sesiones de trabajo sobre una carpeta del disco; el agente edita ficheros, ejecuta órdenes y pide permiso.
- Consumo y límites por cuenta (bloque de 5 h, semanal, presupuesto mensual); relevos con cuenta atrás.
- Portátil: la carpeta del programa lleva sus datos (`datos\`) y se copia a otro PC.

## Capabilities and Constraints

- Habla con los agentes por ACP (Agent Client Protocol); traductor local para proveedores que solo tienen la API de OpenAI.
- Las claves se guardan cifradas; nunca en la base de datos ni en los registros.
- **Idiomas de la interfaz: castellano, inglés y catalán** (decidido; hoy solo existe el castellano).
- Sin conexión a servicios propios: todo local salvo las llamadas a los proveedores.

## Brand Commitments

- Nombre: **MultiAgente**.
- Identidad **neutral de «centralita»** entre proveedores (decidido): sin guiños visuales a un proveedor concreto; los
  colores de proveedor solo identifican cuentas.
- Icono: bocadillo de chat con tres puntos (los tres agentes en una conversación).
- Voz: lenguaje llano y cálido, sin jerga; explica qué se conserva y qué no antes de cualquier acción delicada.

## Evidence on Hand

- README en castellano e inglés; repositorio público con licencia GPL-3.0.
- Sin testimonios, clientes ni métricas de uso: no inventarlos.

## Product Principles

1. El trabajo no se para: un límite de cuenta es un relevo, no un error.
2. Comprensible para quien no es técnico: cada decisión dice qué pasa y qué se conserva.
3. Neutral entre proveedores: ninguno manda en la identidad.
4. El usuario manda en lo delicado: permisos, claves y borrados, siempre visibles y reversibles cuando se puede.
5. Portátil y local: nada fuera de la carpeta del programa.

## Accessibility & Inclusion

- Manejable con teclado y lector de pantalla (incluidos permisos y relevos) y contraste WCAG AA en tema claro y oscuro.
- Respeta la reducción de movimiento de Windows.
