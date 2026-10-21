---
target: interfaz de MultiAgente
total_score: 24
max_score: 40
na_heuristics: 
p0_count: 0
p1_count: 3
target_identity: "file:C:\\projects\\MultiAgente\\src\\MultiAgente.App\\ui\\index.html"
target_fingerprint: "sha256:e03fa791e4382457cf843a478ea2028cc23aaf652c176b84c06d72ae85dc148e"
target_path: "C:\\projects\\MultiAgente\\src\\MultiAgente.App\\ui\\index.html"
timestamp: 2026-10-21T17-12-37Z
slug: src-multiagente-app-ui-index-html
closed: true
---
Method: dual-agent (A: revisión de diseño · B: detector y navegador)

## Puntuación (Nielsen)
| # | Heurística | Nota | Problema principal |
|---|---|---|---|
| 1 | Estado del sistema visible | 3 | Con el visor de cambios abierto se ocultan el estado y Archivar; la cuenta atrás del relevo depende del motor |
| 2 | Lenguaje del usuario | 3 | «Automático» con dos significados; inglés del agente que se cuela |
| 3 | Control y libertad | 2 | Archivar sin vista de archivadas ni restaurar; sin deshacer |
| 4 | Coherencia | 2 | Dos formatos de diff; window.prompt/confirm frente a diálogo propio; Archivar rojo/discreto; logotipos C/C y G/G |
| 5 | Prevención de errores | 2 | Clave de API en prompt a la vista; «Permitir siempre» sin alcance; «Sin preguntar» sin aviso |
| 6 | Reconocer, no recordar | 3 | Acciones solo al pasar el ratón; colores de punto a memorizar |
| 7 | Flexibilidad | 2 | Sin buscador de sesiones ni teclas para permisos |
| 8 | Estética y minimalismo | 3 | Zona de abajo 377/900 px en sesión cargada; límite dicho dos veces |
| 9 | Recuperación de errores | 2 | Fallos de importación en avisos que desaparecen; «no funciona» vago |
| 10 | Ayuda | 2 | Párrafos explicativos, sin ayuda contextual ni primera vez guiada |
| Total | | 24/40 | Aceptable |

## Veredicto de especificidad
Imitación cuidada de la app de Claude; lo propio (relevo, equipos, umbral de cambio) se ve poco. Detector: código limpio; en navegador, contraste (lateral claro --tenue/--ambar sobre --lateral 4,2 y 3,2; --azul no redefinido en oscuro 3,1; hljs claro 3,7/2,9; letras M/N/D 3,0–4,1; texto blanco en logotipos 3,2–4,2; .sub ~130 car/línea; texto 11,5 px en .uso).

## Problemas prioritarios
- [P1] Zona de abajo saturada al pedir permiso (3 botones principales, permiso sin diff) → layout + clarify
- [P1] Estado solo por color, choca con colores de proveedor; rojo para permiso; logotipos repetidos → colorize + harden
- [P1] Teclado/lector de pantalla (cabeceras de proyecto y «⋯» sin foco, controles sin etiqueta, permiso y relevo sin anunciar) y contrastes → harden + audit
- [P2] window.prompt/confirm para clave y borrados; archivar sin vuelta; «Quitar» cuenta borra login → harden
- [P2] «Automático» doble; límite + relevo duplicado y con aspecto de error; inglés del agente; «Quién» → clarify

## Fallos por persona
Experto: sin Ctrl+K ni teclas de permiso/Cambios; Guardar por equipo; sin selección múltiple. Teclado/lector: proyectos y «⋯» sin foco, estado sin texto, controles sin etiqueta, permiso/cuenta atrás sin anunciar. Primera vez: Cuentas mezcla lista/equipos/alta; Estadísticas vs Consumo; enviar sin carpeta; alcance de «Permitir siempre».

## Menores
Selector de cuenta cortado con el visor abierto; píldora de relevo en dos líneas; tablas con bordes pesados; «Sin sesiones» como fila; emoji 👁 en ver clave; avisos de 4–12 s.

## Preguntas
¿Por qué el relevo parece un error? ¿Identidad neutral de «centralita»? ¿Un único «Automático» con el editor detrás de «Personalizar»?
