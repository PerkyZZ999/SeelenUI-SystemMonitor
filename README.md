# Seelen UI System Monitor (CPU/GPU)

Small, local .NET 8 service that exposes CPU and GPU usage as JSON and a Seelen UI toolbar module that displays the values in real time.

- Backend: `SysMonitor_App` (ASP.NET Minimal API on `http://127.0.0.1:58090`)
- Toolbar module: `plugins/plugin.yml` (Text toolbar item with remoteData)
- Optional theme styles: `themes/system-monitor/theme.yml`

## Features
- CPU load via LibreHardwareMonitor (LHM)
- GPU load via a resilient pipeline:
  1) GPU‑Z shared memory (when `?gpuz=1` is used and GPU‑Z is running)
  2) Windows GPU Engine PDH counters (sum/max with smoothing)
  3) LHM GPU sensors (preferred names)
- Jitter smoothing and short “hold last” windows to avoid zero spikes during sensor table rebuilds
- CORS enabled (GET only) so Seelen’s WebView can fetch metrics

## Why a small backend is required
Seelen UI widgets/plugins run inside a sandboxed WebView. They don’t have direct access to native Windows APIs (PDH/Performance Counters, WMI), shared memory (GPU‑Z), or vendor-specific libraries. The toolbar items API is intentionally simple: fetch data via HTTP (`remoteData`) and render it with a math/template expression.

To obtain accurate CPU/GPU load you need native access:
- Windows PDH “GPU Engine” counters
- LibreHardwareMonitor sensors
- GPU‑Z shared memory (when available)

Because those are not exposed to widgets, this project includes a tiny local .NET service that reads the sensors and exposes them as JSON for the widget to fetch. An alternative would be adding a new native capability to Seelen itself; until then, a local service is the simplest, robust approach.

References:
- Library overview: https://seelen.io/docs/library
- API: https://seelen.io/docs/library/api
- Types: https://seelen.io/docs/library/types
- Plugin schema: https://github.com/Seelen-Inc/slu-lib/blob/master/gen/schemas/plugin.schema.json
- Toolbar items schema: https://github.com/Seelen-Inc/slu-lib/blob/master/gen/schemas/toolbar_items.schema.json

## JSON API
- Base URL: `http://127.0.0.1:58090`
- Endpoints:
  - `/metrics` → `{ cpuLoad: number, gpuLoad: number, timestamp: string }`
    - Query params:
      - `gpuz=1` or `gpuz=true` to prefer GPU‑Z shared memory for GPU load.
  - `/debug` → Full sanitized dump of detected sensors (for inspection/testing)

Example:
```http
GET http://127.0.0.1:58090/metrics?gpuz=1
{
  "cpuLoad": 12.5,
  "gpuLoad": 31.7,
  "timestamp": "2025-09-15T18:25:43.511Z"
}
```

## Build & Run (Backend)
Prereqs: .NET 8 SDK on Windows.

```powershell
cd SysMonitor_App
# Debug run
dotnet run
# Or build
dotnet build -c Release
# Binary output (example): SysMonitor_App\bin\Release\net8.0\SeelenMetrics.exe
```

The service listens on `http://127.0.0.1:58090` (configured in `Program.cs`). Change the URL/port there if needed.

## Install (Seelen UI)
Seelen reads resources from your user data folder. Place files directly under the root of each folder (no extra subfolders):

- Plugins
  - Copy `plugins/plugin.yml` to:
    - `C:\Users\<YOU>\AppData\Roaming\com.seelen.seelen-ui\plugins\plugin.yml`
- Widgets (optional; kept for reference)
  - Copy `widgets/system-monitor/resource.yml` to:
    - `C:\Users\<YOU>\AppData\Roaming\com.seelen.seelen-ui\widgets\resource.yml`
- Themes (optional styling)
  - Copy the folder `themes/system-monitor` into:
    - `C:\Users\<YOU>\AppData\Roaming\com.seelen.seelen-ui\themes\system-monitor`

Then in Seelen:
1) Restart Seelen UI
2) Settings → Resources → Plugins and Widgets → Ensure they show up
3) Right‑click toolbar → Modules → Add module → select `@Charles/system-monitor`

By default the plugin points to GPU‑Z mode:
```yaml
remoteData:
  metrics:
    url: 'http://127.0.0.1:58090/metrics?gpuz=1'
    updateIntervalSeconds: 2
```
Remove `?gpuz=1` if you do not run GPU‑Z (LHM/PDH will be used automatically).

## Autostart the backend
Pick one of the following methods so the metrics service starts on login.

### A) Startup folder (simplest)
1) Press Win+R → type `shell:startup` → Enter
2) Create a shortcut to `SeelenMetrics.exe` in that folder
3) Optional: set the shortcut to “Run minimized”

### B) Registry Run key (current user)
Run in PowerShell (update the path):
```powershell
$exe = 'C:\\path\\to\\SeelenMetrics.exe'
New-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'SeelenMetrics' -PropertyType String -Value '"' + $exe + '" --minimized' -Force
```
Remove:
```powershell
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'SeelenMetrics'
```

### C) Task Scheduler (highest privileges)
Run in PowerShell (update the path):
```powershell
$exe = 'C:\\path\\to\\SeelenMetrics.exe'
schtasks /Create /TN "SeelenMetrics" /TR '"'"$exe"'" --minimized' /SC ONLOGON /RL HIGHEST /F
```
Remove:
```powershell
schtasks /Delete /TN "SeelenMetrics" /F
```

## GPU‑Z notes (for gpuz mode)
- Keep GPU‑Z running. In GPU‑Z, enable “Continue refreshing sensors in background” so the shared memory stays current.
- The service reads GPU‑Z’s shared memory (version, busy flag, lastUpdate and the 128 sensor records) and selects the most relevant % sensor (GPU Load / Core Load / Utilization / Usage / Render / Graphics).
- Short zero “blips” during app closes are filtered; last good value is held briefly.

## Troubleshooting
- Module shows `!?`:
  - Your plugin YAML must use `type: text` and a `template` string with a `return` expression per Seelen’s schema.
  - Check DevTools in Seelen debug build (focus toolbar → Ctrl+Shift+I) for template parse errors.
- GPU reads 0%:
  - Ensure GPU‑Z is running if using `?gpuz=1`.
  - Try `http://127.0.0.1:58090/metrics` (no gpuz) to use LHM/PDH fallback.
  - Open `http://127.0.0.1:58090/debug` and verify GPU sensors are present.
- CORS/network:
  - Service binds to `127.0.0.1`. CORS allows GET from any origin; Seelen fetches fine on localhost.
- After relaunching Seelen UI:
  - If GPU load stops updating, restart `SeelenMetrics.exe` (the backend). This refreshes shared memory bindings and PDH counters.

## Project layout
```
SysMonitor_App/                 # .NET 8 minimal API (SeelenMetrics.exe)
plugins/
  plugin.yml                    # Toolbar item bound to Fancy Toolbar
widgets/
  system-monitor/
    resource.yml                # Widget (optional) that registers a DOM badge
themes/
  system-monitor/
    theme.yml                   # Optional toolbar styles
```

## Credits
- CPU/GPU sensors via LibreHardwareMonitor & Windows PDH
- GPU‑Z shared memory parsing based on the documented layout by TechPowerUp

## License
MIT (for this project). GPU‑Z is owned by TechPowerUp; respect their license/terms when using GPU‑Z.
