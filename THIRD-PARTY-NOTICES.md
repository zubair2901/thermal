# Third-party software

Thermal's single-file build includes these components:

| Component | License | Use |
| --- | --- | --- |
| [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) | MPL-2.0 | CPU, GPU, storage, network and battery sensors |
| [Microsoft.Diagnostics.Tracing.TraceEvent](https://github.com/microsoft/perfview) | MIT | Reading DXGI/D3D9 present events for the frame rate |
| [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2) | Microsoft WebView2 SDK license | Hosting the interface |
| [System.Management](https://github.com/dotnet/runtime), [System.Diagnostics.PerformanceCounter](https://github.com/dotnet/runtime), .NET runtime | MIT | WMI and performance counters |
| [React](https://github.com/facebook/react) | MIT | Interface |
| [Archivo](https://github.com/Omnibus-Type/Archivo) via [Fontsource](https://fontsource.org) | SIL Open Font License 1.1 | Interface typeface |

Not bundled, installed separately by the user if they choose:

| Component | License | Use |
| --- | --- | --- |
| [PawnIO](https://pawnio.eu) | See pawnio.eu | Lets LibreHardwareMonitor read CPU temperature and power on AMD and Intel processors |

The HP BIOS fan-speed commands follow the reverse-engineering notes published by the [OmenMon](https://github.com/OmenMon/OmenMon) project. No OmenMon code is included.
