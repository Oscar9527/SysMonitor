# Third-party notices

SysMonitor 1.2.14 bundles LibreHardwareMonitorLib 0.9.4 under the Mozilla Public License 2.0. It is packaged into the portable executable and used to read CPU temperature sensors and NVIDIA/AMD/Intel GPU telemetry exposed by local hardware drivers. Version 0.9.4 is intentionally retained because later stable builds regress AMD Ryzen 5000 mobile CPU temperature reads on validated hardware.

Project: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor
License: https://www.mozilla.org/MPL/2.0/

NVIDIA GPU telemetry is read from `nvidia-smi`, which is installed and licensed with the user's NVIDIA graphics driver and is not distributed inside SysMonitor.
