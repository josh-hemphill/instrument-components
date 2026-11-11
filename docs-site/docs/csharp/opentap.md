# OpenTAP integration

The OpenTAP pack supplies eight resource types and 74 generated operation steps. Resources derive from `DeviceInstrument` and use protocol-neutral handles. The host either calls `AttachDevice(handle, ownsHandle: false)` before Open or sets the session-local `OpenTapDevices.Provider` to an explicitly composed provider. The provider owns handles it opens. Resource Close disposes only owned handles.

All class views use one attached handle. Resource Open validates class capabilities without sending an IDN query. Identity, Reset and Shutdown are explicit steps. SafeShutdown requests Shutdown before a separate Reset; a Shutdown failure aborts the Reset. Unknown complete output inventory must fail explicitly. There is no editable mutable output-channel count on the resource.

Use `VisaScpiConnector` from the optional `InstrumentComponents.OpenTap.Visa` companion when the host chooses VISA and SCPI. Supply the static driver factories and passive evidence through its provider callback. Device packages are application dependencies; the OpenTAP pack does not bundle them or activate external assemblies by reflection.

Quantity-bearing results publish their actual units, including Unknown. Typed overload results produce Verdict.Fail. Ordinary measurements use the configured limits. Operation parameters follow the SDK domains. Round-trip TapPlan and all-step opaque-backend tests run without hardware.

The OpenTAP host is validated separately from the Native AOT SDK consumer and is not claimed to be AOT compatible. Build the pack with the repository's OpenTAP packaging command after the .NET tests pass; deploy the selected external driver assemblies as part of the host application.
