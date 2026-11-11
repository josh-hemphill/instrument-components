# Optional VISA transport

VISA is an optional standards transport, supplied by `instrument-visa` or `InstrumentComponents.Visa`. Install a compatible x64 VISA runtime on the bench host. Core contracts, facade, drivers and software validation do not require it.

The application must explicitly adapt a VISA message session to the selected driver's connector, supply passive identity evidence, register the chosen driver packages and configure IO timeout before opening. The OpenTAP VISA companion includes `VisaScpiConnector` for sync/worker SCPI IO and a provider accepting a static factory callback. Native async requires an actual async connector; it is not inferred from a sync VISA session.

VISA enumeration is separate from driver selection. Opening does not automatically identify, probe, reset, retry or reconnect. Physical bench plans must document address, transport/runtime versions, device firmware, package hashes, safe wiring, physical units and expected ranges. Do not run hardware validation until preview software gates pass and the bench configuration has been reviewed.
