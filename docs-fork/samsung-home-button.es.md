# Samsung: que el botón Home del mando abra Free My TV

*Experimental, no oficial, solo en este fork.* [Read in English](samsung-home-button.md)

En las teles Samsung, Free My TV es una app normal y Smart Hub sigue siendo la pantalla de inicio. Este procedimiento
cambia la asignación de Home de la propia tele para que **el botón físico Home del mando abra Free My TV**. No hace
root, no instala ni borra nada y guarda una copia de los valores originales para poder volver a escribirlos.

La herramienta está en [`tizen/home-button/`](../tizen/home-button/).

## Estado, sin adornos

| Qué | Estado |
| --- | --- |
| El botón Home abre Free My TV | **Funciona** en una QN90B (ver abajo), comprobado pulsando Home en la tele |
| Lectura de valores tras el cambio | Confirmada (`HOME_APPLIED`, helper con salida 0) |
| `restore` (volver a los originales) | Preparado y con guardas, **aún no ejecutado** en una tele real |
| Persistencia tras standby / reinicio completo | **Sin verificar todavía** |
| Otros modelos o firmwares | Sin probar; la herramienta rechaza lo que no coincida con lo que le indiques |

No lo des por terminado hasta haber pulsado Home desde varias apps y una entrada HDMI, comprobar que sigues pudiendo
entrar a los ajustes de Samsung y probar `restore` una vez.

## Hardware probado

- Samsung QN90B (2022), Tizen 6.5, ARMv7 (32 bits), firmware **T-PTMDEUC-1720.7**: funciona (este fork).
- Samsung Q70B, mismo firmware T-PTMDEUC-1720.7: probado por Arn0 con su propio launcher
  ([arn0-tv](https://github.com/NelsonEU/arn0-tv), [guía](https://github.com/NelsonEU/arn0-tv/blob/main/docs/setup-tv.md)).

La herramienta solo acepta Tizen 6.5 en ARMv7, y solo el modelo y firmware que le pases por línea de comandos.

## Requisitos

- Free My TV instalada en la tele ([CLAUDE.md](../CLAUDE.md), *Installing it on a Samsung TV*), app id
  `FreeMyTVio.FreeMyTV`.
- La tele en **Modo desarrollador** con la IP de este ordenador como *Host PC IP* (Apps → `1 2 3 4 5`), accesible por
  SDB en el puerto 26101, y que la tele pueda abrir una conexión TCP de vuelta a este ordenador (permite conexiones
  entrantes para Python en el cortafuegos).
- Python 3 (usa el cliente SDB de `tizen/install.py`; no hacen falta herramientas de Samsung).
- Un SDK de .NET (probado 7.0.203) con `NETStandard.Library.Ref` 2.1.0 para compilar los dos helpers. La tele los
  ejecuta con su propio `dotnet`.

## Cómo funciona

1. `home_button.py` sube por SDB un script corto a la carpeta temporal del SDK y lo dispara a través del tratamiento
   del nombre de paquete de `0 appinstall tpk`. La tele lo ejecuta como **usuario SDK (uid 901)**, no como root. El
   método está publicado en [samsung-tv-root](https://github.com/chris-ritsen/samsung-tv-root)
   (`src/samsung_tv_root/sdb.py`); su exploit de root **no** se usa aquí.
2. El script ejecuta un helper con el `dotnet` de la tele y devuelve la salida por una conexión TCP a este ordenador,
   comprobada por la IP de la tele y un token aleatorio. Script y helper se borran solos.
3. Los helpers (adaptados de `Query.cs` y `Control.cs` de Arn0) leen y escriben los ajustes de Home con las
   bibliotecas nativas de la tele:
   - la app por defecto de `http://tizen.org/appcontrol/operation/main` (`aul_get_default_app` /
     `app_operation_set_current_app`),
   - `db/appfw/home_appid` (vconf),
   - y leen, sin cambiarlos, `db/appfw/startup_appid`, `db/appfw/booting_appid_backup`, `db/first_screen/type` y
     `db/menu/smart_hub/ontvsetting/auto_start`.

En la tele probada el valor original de todos era Smart Hub (`com.samsung.tv.csfs`) y `auto_start=1`; tras `apply`
solo la operación main y `home_appid` apuntan a Free My TV.

El servicio appinstall de SDB suele responder con un rechazo (`the TV refused shell:0 appinstall ...`) aunque el
script se haya ejecutado. La herramienta ignora esa respuesta y decide el éxito solo por el callback autenticado, el
marcador del helper (`HOME_APPLIED=`, etc.) y `HELPER_EXIT=0`. Sin callback el estado es **desconocido**: ejecuta
`query` antes de nada.

## Pasos

Desde la raíz del repositorio. Informes y baselines van a `./home-button-reports/` (ignorado por git; contienen la IP
y los ajustes de tu tele: mantenlos privados y a salvo, el baseline es tu camino de vuelta).

```sh
# 0. Compilar los helpers (una vez)
tizen/home-button/build.sh

# 1. Solo lectura: modelo, plataforma, uid, firmware, dotnet y bibliotecas
python3 tizen/home-button/home_button.py info --tv <IP_TELE>
#    apunta el modelName y el firmware (p. ej. T-PTMDEUC-1720.7) del informe

# 2. Leer los ajustes de Home actuales y guardarlos como valores originales de esta tele
python3 tizen/home-button/home_button.py query --tv <IP_TELE> --model <MODELO> --firmware <FIRMWARE> --save-baseline
#    -> home-button-reports/baseline-<MODELO>-<token>.json  (se rechaza si Home ya apunta a Free My TV)
#    comprueba launcher_boot_supported=1 en el informe

# 3. Comprobar que la tele sigue coincidiendo con ese baseline (solo lectura)
python3 tizen/home-button/home_button.py verify --tv <IP_TELE> --model <MODELO> --firmware <FIRMWARE> --baseline <BASELINE>

# 4. Apuntar Home a Free My TV
python3 tizen/home-button/home_button.py apply --tv <IP_TELE> --model <MODELO> --firmware <FIRMWARE> --baseline <BASELINE>

# 5. Cuando quieras volver a Smart Hub
python3 tizen/home-button/home_button.py restore --tv <IP_TELE> --model <MODELO> --firmware <FIRMWARE> --baseline <BASELINE>
```

`--app-id` (por defecto `FreeMyTVio.FreeMyTV`) y `--package` (por defecto, lo que va antes del punto) permiten usar
otra app. El código de salida es 0 solo cuando la tele confirmó toda la operación; aun así, lee el informe.

### Guardas (no hay que quitar ninguna para "hacer que funcione")

- Runner: el `modelName` de la tele (API de red) debe ser igual a `--model`; SDB debe indicar Tizen 6.5 / armv7; el
  baseline debe haberse guardado para el mismo modelo, firmware y app; todos los valores se comprueban como tokens
  simples tipo app id antes de llegar al shell de la tele.
- Helpers: proceso de 32 bits, uid 901, `--firmware` presente en `/etc/prd_info.ini`.
- `apply`: la tele debe coincidir exactamente con el baseline, debe existir la carpeta del paquete de la app y la tele
  debe decir que la app puede arrancar como launcher (`util_is_appbooting_supported_webapp_byappid`). Cada escritura
  se relee; si algo falla vuelve a escribir el baseline y lo indica (o imprime `URGENT` si eso también falló).
- `restore`: se niega si algún valor no es ni el original ni la app destino (alguien más lo cambió).
- Un baseline que ya contiene la app destino se rechaza: se capturó después de un cambio.

## Riesgos

- **Vía no documentada.** Depende de cómo el servicio appinstall de SDB de una tele comercial trata un nombre de
  paquete, ejecutando código como uid 901 (`sdk`). Samsung puede cerrarla en cualquier actualización y no es una
  forma oficial de configurar Home.
- **No oficial, sin garantía.** Estás cambiando preferencias del sistema de tu tele.
- **Depende del firmware.** Se probó en un único firmware. Las guardas lo detienen en otros; no las quites.
- **Nunca desinstales Free My TV mientras sea la app de Home.** Ejecuta antes `restore`. Reinstalar con otro
  certificado también obliga a desinstalar, así que conserva el mismo certificado de firma.
- **Conserva un camino de vuelta** a Smart Hub y a los ajustes de Samsung: comprueba que sigues pudiendo abrirlos (la
  tarjeta TV Settings de Free My TV, o la tecla de ajustes/menú del mando si la tiene), deja el Modo desarrollador
  activo y guarda el baseline y esta herramienta.
- Una actualización de firmware puede deshacer o ignorar el cambio, o cerrar el método.

## Recuperación

1. Ejecuta `query` para ver los valores actuales; no repitas `apply` a ciegas (de todas formas rechaza una tele que
   ya no coincide con el baseline).
2. Ejecuta `restore` con el baseline guardado en el paso 2.
3. Si la herramienta no llega a la tele: abre los ajustes de Samsung desde la tarjeta TV Settings de Free My TV (o la
   tecla de ajustes del mando) y revisa la red y el Modo desarrollador, que se puede volver a activar desde Apps →
   `1 2 3 4 5`. Como último recurso, un restablecimiento de fábrica (Ajustes → General → Restablecer) devuelve los
   valores de Samsung, pero borra también el resto de tus ajustes.
4. Una ejecución que se cortó a medias puede dejar `home-button-*.sh` / `.dll` en `/home/owner/share/tmp/sdk_tools/`;
   sus nombres aparecen en el informe.

## Créditos y referencias

- **Arn0**, de Arnaud Etienne ([NelsonEU/arn0-tv](https://github.com/NelsonEU/arn0-tv), MIT): los helpers nativos de
  consulta y control de Home de los que se adapta esto, y la primera prueba en una Q70B con este firmware. Su licencia
  se conserva en [`tizen/home-button/LICENSE-arn0`](../tizen/home-button/LICENSE-arn0) y se le cita en cada fuente.
- [samsung-tv-root](https://github.com/chris-ritsen/samsung-tv-root) (chris-ritsen): el método de ejecución como
  usuario SDK por SDB (sin usar la parte de root).
- [CleanFrame](https://github.com/nthnclrk/CleanFrame/blob/main/docs/agent-tv-setup-guide.md): asignación nativa de
  Home y precauciones de recuperación en The Frame.
- [Couchroot](https://github.com/splitcipher/couchroot): Home/administración en QN90A y S90D.
- [Vetro](https://github.com/andrearicci93/vetro): inventario de servicios con root (no se usa aquí; posible
  referencia futura para un debloat reversible, que esta herramienta no hace).
- *Autorun Last App* en los ajustes de la tele es otra cosa: abre la última app al encender, no cambia el botón Home.
