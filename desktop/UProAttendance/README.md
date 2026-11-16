# UPro Attendance para Windows

Aplicación WPF de escritorio para U Professional Hairstudio. Abre en **Registro de asistencia**. **Administración** abre **Registrar huellas** con acceso protegido. La solución usa .NET 10 y el logotipo oficial `public/logo.png` de la copia local de UProApp. La interfaz toma como referencia `app/webpage`, `PublicHeader` y `PreconsultaCTA`.

## Ejecutar en la Huawei

1. Instala **.NET Desktop Runtime 10 para Windows x64**. Para compilar, instala el SDK .NET 10. La solución se verificó con SDK 10.0.302 y runtime Windows Desktop 10.0.10.
2. Conecta el U20 y verifica que Windows lo indique como **Iniciado**. Para el equipo reportado, el identificador es `USB\VID_1162&PID_2200`. Usa el controlador oficial SecuGen compatible con FDx y valida el dispositivo con **SGDX Diagnostic Tool** si la apertura falla.
3. Obtén **FDx SDK Pro for Windows 4.21** del proveedor. Coloca `sgfplib.dll`, `sgfpamx.dll` y `sgwsqlib.dll` del directorio `bin\x64` del SDK junto a `UProAttendance.exe`. `sgwsqlib.dll` es el módulo WSQ del runtime. El flujo de asistencia trabaja con imágenes en memoria y plantillas SG400. En una instalación operativa, utiliza una carpeta de instalación administrada y aplica los términos de distribución de SecuGen.
4. Si tienes el código fuente, genera la carpeta de publicación desde este directorio.

   ```powershell
   dotnet publish App/UProAttendance.csproj -c Release -r win-x64 --self-contained false -m:1 -o .local/publish/win-x64
   ```

5. Ejecuta `UProAttendance.exe` desde la carpeta publicada o descomprimida. El paquete requiere .NET Desktop Runtime 10 x64. Para una compilación de diagnóstico x86, usa `-r win-x86 -p:PlatformTarget=x86`, Desktop Runtime x86 y los módulos de `bin\i386`. Mantén la arquitectura de los módulos igual a la del proceso. Esta entrega valida y publica x64.

[Controladores y diagnóstico SecuGen](https://secugen.com/drivers/) · [Solicitud de SDK oficial](https://secugen.com/request-free-software/) · [Descarga de FDx 4.21 de SecuGen India](https://www.secugenindia.com/download)

## Qué se puede verificar en esta iteración

- La detección del lector usa el estado PnP del dispositivo presente. La disponibilidad requiere que FDx abra el dispositivo y devuelva dimensiones y tamaño de plantilla válidos.
- Captura y comparación usan la API C de FDx 4.21, contrastada con `Inc/sgfplib.h`, el manual SG1-0030A-025 y los ejemplos oficiales C# y C++. Las llamadas de captura nativa usan intervalos de 1,5 s. El flujo permite aproximadamente 18 s por muestra y hasta 10 s para recibir el evento Auto-On de retiro. Los errores reportan la operación y el código en el diagnóstico.
- El umbral inicial de `GetImageEx` y el de `GetImageQuality` son 50 en ambas operaciones. El primero mide la proporción de imagen ocupada por la huella y el segundo también considera calidad de crestas. Son valores de arranque basados en el manual. La consistencia entre las tres muestras usa `SL_HIGH`. Para detección de duplicidades y candidatos se usa `SL_NORMAL`. La identificación exige un único dedo candidato y al menos una comparación `SL_HIGH`. Estas configuraciones quedan sujetas a pruebas en el salón.
- Cada dedo conserva tres plantillas SG400 separadas en memoria. El manual documenta funciones de combinación ANSI 378 e ISO 19794 de múltiples vistas. Esta solución emplea comparación 1:1 de FDx contra la galería local y agrupa por empleado y dedo. SecuSearch Pro 3 ofrece búsqueda indexada y listas de candidatos para galerías mayores. El proveedor publica ejemplos en C# y un límite de 1.000 plantillas sin archivo de licencia. La compatibilidad efectiva de SecuSearch en la Huawei requiere validación por separado. [FDx SDK Pro](https://secugen.com/products/sdk/) · [SecuSearch Pro 3](https://secugen.com/products/secusearch/)
- La huella vectorial oscurece los trazos desde el centro una vez aceptada cada muestra. La barra cuenta muestras. La guía de variar ligeramente la posición se muestra después de recibir el evento de retiro. La etiqueta del dedo proviene de la selección durante el enrolamiento.

## Acceso y marcajes

En Release, **Registrar huellas** exige un proveedor autorizado de UProApp. La composición actual carece de ese proveedor y el formulario permanece cerrado. El programa prepara contratos para autorización administrativa, alcance por empleado, listado de empleados, galería de plantillas y envío de asistencia. Las plantillas de la galería son locales. Las imágenes se eliminan de los búferes de aplicación y las muestras en memoria se limpian al cancelar o cerrar.

Para una prueba de extremo a extremo con hardware, compila en **Debug**, coloca los módulos FDx junto al ejecutable Debug y abre PowerShell como administrador de Windows. Los nombres son ejemplos y el acceso al enrolamiento comprueba el token real de Windows. Esta opción está excluida del ejecutable Release.

```powershell
dotnet build UProAttendance.slnx -m:1 -nodeReuse:false
.\App\bin\Debug\net10.0-windows\UProAttendance.exe --laboratorio
```

1. En **Administración**, verifica el acceso, selecciona la estilista de prueba y el dedo. Captura tres impresiones levantando el dedo entre muestras.
2. Regresa a **Registro de asistencia**. Presenta un dedo enrolado y comprueba la identidad y la etiqueta. Pulsa explícitamente **Entrada** o **Salida**. La hora local es provisional y los marcajes de laboratorio se mantienen en memoria durante la sesión.
3. Para caracterizar el equipo, repite con posiciones centradas y ligeramente desplazadas. Registra intentos, coincidencias al primer intento y rechazos sin guardar imágenes o plantillas. Verifica un dedo sin enrolar, una muestra de otro dedo durante el enrolamiento, cancelación y desconexión.

Un marcaje nuevo lleva un UUID. Una solicitud sin respuesta conserva el evento pendiente con el mismo UUID durante el proceso. El backend futuro debe deduplicar por ese UUID y devolver la hora oficial. La aplicación presenta las horas en `America/Mexico_City`. El diseño de almacenamiento duradero, la conciliación con un backend real y el acceso de producción quedan pendientes de aprobación e integración.

## Estructura y pruebas

- `App/` contiene WPF, la marca, la navegación, el adaptador FDx y la composición de proveedores.
- `Core/` contiene contratos, casos de identificación y enrolamiento, galería de sesión y semántica de marcajes.
- `Tests/` contiene pruebas ejecutables sin paquetes externos, diagnóstico FDx y generación de capturas WPF.

```powershell
dotnet build UProAttendance.slnx -m:1 -nodeReuse:false
.\Tests\bin\Debug\net10.0-windows\UProAttendance.Tests.exe
.\Tests\bin\Debug\net10.0-windows\UProAttendance.Tests.exe --screenshots .local\screenshots
```

Para los diagnósticos, coloca los mismos módulos FDx x64 junto al ejecutable de pruebas. `--probe` comprueba apertura, cancelación antes de la captura y reapertura del lector. `--capture` pide al operador colocar un dedo y reporta solo el estado de captura. `--fixture <ruta>` abre el lector para inicializar FDx y usa el archivo `raw2wsq_finger.raw` del ZIP oficial como entrada en memoria para ejercitar calidad, extracción y comparación. El SDK, la muestra del proveedor y los resultados de compilación se mantienen fuera del control de versiones.

En el equipo de desarrollo Lenovo x64, pasaron las diez pruebas de lógica, la apertura y reapertura del U20 y la extracción y comparación de la muestra oficial. El diagnóstico con el sandbox de desarrollo devolvió código 2 en `SGFPM_OpenDevice`; el mismo diagnóstico abrió el lector al ejecutarse fuera de ese sandbox. El SDK reportó mensajes de configuración de liveness del controlador, por lo que esta prueba no valida antisuplantación. WPF verificó la pantalla inicial, la ruta administrativa, el bloqueo sin integración y el logotipo. Las capturas incluyen un diseño compacto renderizado a 144 DPI. Falta una sesión con operador para validar capturas físicas, eventos de retiro, reconocimiento con distintas colocaciones y operación en la Huawei.
