use super::*;
/// Native contract: no detached accesses after the returned future is dropped.
/// Resource close must be synchronous, nonblocking, resource-only.
pub trait AsyncMessageIo: Send {
    fn write<'a>(&'a mut self, message: &'a str) -> DriverFuture<'a, ()>;
    fn query<'a>(&'a mut self, message: &'a str) -> DriverFuture<'a, String>;
    fn recover(&mut self) -> DriverFuture<'_, ()> {
        Box::pin(async {
            Err(error(
                ErrorKind::SessionNeedsRecovery,
                "host must explicitly resynchronize message IO",
            ))
        })
    }
    fn close(&mut self) -> Result<()>;
}
pub trait AsyncConnector: Send + Sync {
    fn open<'a>(
        &'a self,
        context: &'a OpenContext<'_>,
    ) -> DriverFuture<'a, Box<dyn AsyncMessageIo>>;
}
#[derive(Clone)]
pub struct AsyncScpiFactory {
    descriptor: DriverDescriptor,
    profile: Profile,
    routes: ProfileRoutes,
    connector: Arc<dyn AsyncConnector>,
}
impl AsyncScpiFactory {
    pub fn with_endpoint_profiles(mut self, profiles: Vec<(Endpoint, Profile)>) -> Result<Self> {
        self.routes = profile_routes(self.profile.id(), profiles)?;
        self.descriptor.operations = route_operations(&self.profile, &self.routes);
        Ok(self)
    }

    pub fn with_rules(mut self, rules: Vec<MatchRule>) -> Self {
        self.descriptor.rules = rules;
        self
    }

    pub fn new(profile: Profile, scheme: &str, connector: Arc<dyn AsyncConnector>) -> Self {
        Self {
            descriptor: DriverDescriptor {
                id: profile.id().into(),
                contract_major: CONTRACT_MAJOR,
                rules: vec![MatchRule::StandardScpi {
                    scheme: scheme.into(),
                }],
                operations: operations(&profile),
            },
            profile,
            routes: BTreeMap::new(),
            connector,
        }
    }
}
impl DriverFactory for AsyncScpiFactory {
    fn descriptor(&self) -> DriverDescriptor {
        self.descriptor.clone()
    }
    fn execution_modes(&self) -> BTreeSet<ExecutionMode> {
        [ExecutionMode::NativeAsync].into_iter().collect()
    }
    fn open_native<'a>(
        &'a self,
        context: &'a OpenContext<'_>,
    ) -> DriverFuture<'a, Box<dyn AsyncDeviceBackend>> {
        Box::pin(async move {
            open_device(
                selected_profile(&self.profile, &self.routes, context)?,
                self.connector.clone(),
                context,
            )
            .await
        })
    }
}
struct AsyncScpiDevice {
    engine: Engine,
    io: Box<dyn AsyncMessageIo>,
    ownership: ConnectionOwnership,
    closed: bool,
}
impl AsyncScpiDevice {
    async fn execute(&mut self, request: CatalogRequest) -> Result<CatalogReply> {
        if self.closed {
            return Err(error(ErrorKind::SessionClosed, "SCPI resource closed"));
        }
        let prepared = self.engine.prepare(&request)?;
        let mut value = None;
        let mut preamble = None;
        let mut failures = Vec::new();
        for step in &prepared.steps {
            let result = if step.response == ResponseRole::Write {
                self.io.write(&step.command).await
            } else {
                self.io
                    .query(&step.command)
                    .await
                    .and_then(response_checked)
                    .and_then(|response| match step.response {
                        ResponseRole::Value => {
                            value = Some(response);
                            Ok(())
                        }
                        ResponseRole::Preamble => {
                            preamble = Some(response);
                            Ok(())
                        }
                        ResponseRole::Completion => completion(&response),
                        _ => unreachable!(),
                    })
            };
            if let Err(e) = result {
                self.engine.invalidate();
                if request.operation() != CatalogOperation::Shutdown {
                    return Err(e);
                }
                failures.push(e.to_string());
            }
        }
        if !failures.is_empty() {
            return Err(error(ErrorKind::Shutdown, &failures.join("; ")));
        }
        let result = self.engine.finish(&request, &prepared, value, preamble);
        if result.is_err() {
            self.engine.invalidate();
        }
        result
    }
    fn release(&mut self) -> Result<()> {
        if self.closed {
            return Ok(());
        }
        self.closed = true;
        if self.ownership == ConnectionOwnership::Owned {
            self.io.close()
        } else {
            Ok(())
        }
    }
}
impl Drop for AsyncScpiDevice {
    fn drop(&mut self) {
        let _ = self.release();
    }
}
impl AsyncCatalogBackend for AsyncScpiDevice {
    fn invoke_async(&mut self, request: CatalogRequest) -> DriverFuture<'_, CatalogReply> {
        Box::pin(self.execute(request))
    }
}
impl AsyncDmmMeasurement for AsyncScpiDevice {
    fn measure_voltage_dc(&mut self, range: Option<f64>) -> DriverFuture<'_, Measurement> {
        Box::pin(async move {
            measurement(
                self.execute(CatalogRequest::DmmMeasureVoltageDc { range })
                    .await?,
            )
        })
    }
}
impl AsyncPsuOutput for AsyncScpiDevice {
    fn set_voltage(&mut self, channel: u32, volts: f64) -> DriverFuture<'_, ()> {
        Box::pin(async move {
            self.execute(CatalogRequest::DcPowerSupplySetVoltage { channel, volts })
                .await?
                .into_unit()
        })
    }
    fn read_voltage(&mut self, channel: u32) -> DriverFuture<'_, Measurement> {
        Box::pin(async move {
            measurement(
                self.execute(CatalogRequest::DcPowerSupplyReadVoltage { channel })
                    .await?,
            )
        })
    }
    fn output_enable(&mut self, channel: u32, enabled: bool) -> DriverFuture<'_, ()> {
        Box::pin(async move {
            self.execute(CatalogRequest::DcPowerSupplyOutputEnable { channel, enabled })
                .await?
                .into_unit()
        })
    }
}
impl AsyncRemoteSense for AsyncScpiDevice {
    fn set_mode(&mut self, channel: u32, mode: instrument_api::SenseMode) -> DriverFuture<'_, ()> {
        let mode = match mode {
            instrument_api::SenseMode::Internal => catalog::SenseMode::Internal,
            instrument_api::SenseMode::External => catalog::SenseMode::External,
        };
        Box::pin(async move {
            self.execute(CatalogRequest::DcPowerSupplySetSenseMode { channel, mode })
                .await?
                .into_unit()
        })
    }
}
impl AsyncReset for AsyncScpiDevice {
    fn reset(&mut self) -> DriverFuture<'_, ()> {
        Box::pin(async move { self.execute(CatalogRequest::Reset).await?.into_unit() })
    }
}
impl AsyncSafeShutdown for AsyncScpiDevice {
    fn shutdown(&mut self) -> DriverFuture<'_, ()> {
        Box::pin(async move { self.execute(CatalogRequest::Shutdown).await?.into_unit() })
    }
}
impl AsyncDeviceBackend for AsyncScpiDevice {
    fn operations(&self) -> BTreeSet<Operation> {
        operations(&self.engine.profile)
    }
    fn catalog(&mut self) -> Option<&mut dyn AsyncCatalogBackend> {
        Some(self)
    }
    fn dmm(&mut self) -> Option<&mut dyn AsyncDmmMeasurement> {
        Some(self)
    }
    fn psu(&mut self) -> Option<&mut dyn AsyncPsuOutput> {
        Some(self)
    }
    fn remote_sense(&mut self) -> Option<&mut dyn AsyncRemoteSense> {
        Some(self)
    }
    fn reset(&mut self) -> Option<&mut dyn AsyncReset> {
        Some(self)
    }
    fn shutdown(&mut self) -> Option<&mut dyn AsyncSafeShutdown> {
        Some(self)
    }
    fn recover(&mut self) -> DriverFuture<'_, ()> {
        Box::pin(async move {
            self.io.recover().await?;
            self.engine.invalidate();
            Ok(())
        })
    }
    fn close(&mut self) -> Result<()> {
        self.release()
    }
}

pub(super) async fn open_device(
    profile: Profile,
    connector: Arc<dyn AsyncConnector>,
    context: &OpenContext<'_>,
) -> Result<Box<dyn AsyncDeviceBackend>> {
    Ok(Box::new(AsyncScpiDevice {
        engine: Engine::new(profile),
        io: connector.open(context).await?,
        ownership: context.ownership,
        closed: false,
    }))
}
