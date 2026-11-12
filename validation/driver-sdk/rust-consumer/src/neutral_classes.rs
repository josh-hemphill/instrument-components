//! Packed-consumer rooting proof for neutral capabilities, independent of vendor code.
use instrument_api::{catalog::*, *};
use std::{collections::VecDeque, sync::Mutex};

struct Executor(Mutex<VecDeque<(CatalogRequest, CatalogReply)>>);
impl Executor {
    fn new() -> Self {
        Self(Mutex::new(
            [
                (
                    CatalogRequest::DataAcquisitionMeasureVoltageDc {
                        channel: 7,
                        range: None,
                    },
                    CatalogReply::Quantity(Quantity {
                        value: 1.25,
                        unit: Unit::Volt,
                        status: MeasurementStatus::Valid,
                    }),
                ),
                (
                    CatalogRequest::RfSignalGeneratorConfigure {
                        frequency_hz: 30_000_000.0,
                        level_dbm: -30.0,
                    },
                    CatalogReply::Unit,
                ),
                (
                    CatalogRequest::RfSignalGeneratorOutputEnable { enabled: true },
                    CatalogReply::Unit,
                ),
                (
                    CatalogRequest::RfSignalGeneratorOutputStateQuery,
                    CatalogReply::Boolean(true),
                ),
                (
                    CatalogRequest::RfReceiverMeasureFrequency,
                    CatalogReply::Quantity(Quantity {
                        value: 30_000_000.0,
                        unit: Unit::Hertz,
                        status: MeasurementStatus::Valid,
                    }),
                ),
                (
                    CatalogRequest::RfReceiverMeasurePower,
                    CatalogReply::Quantity(Quantity {
                        value: -10.0,
                        unit: Unit::Dbm,
                        status: MeasurementStatus::Valid,
                    }),
                ),
            ]
            .into(),
        ))
    }
    fn complete(&self) {
        assert!(self.0.lock().unwrap().is_empty());
    }
}
impl CatalogExecutor for Executor {
    fn invoke(&self, request: CatalogRequest) -> Result<CatalogReply> {
        request.validate()?;
        let (expected, reply) = self
            .0
            .lock()
            .unwrap()
            .pop_front()
            .expect("unexpected neutral operation");
        assert_eq!(request, expected);
        reply.validate(&request)?;
        Ok(reply)
    }
}
#[cfg(feature = "async")]
impl AsyncCatalogExecutor for Executor {
    fn invoke_async(&self, request: CatalogRequest) -> DriverFuture<'_, CatalogReply> {
        Box::pin(async move { self.invoke(request) })
    }
}
pub fn synchronous() -> Result<()> {
    let executor = Executor::new();
    assert_eq!(
        DataAcquisition(&executor)
            .measure_voltage_dc(7, None)?
            .value,
        1.25
    );
    let source = RfSignalGenerator(&executor);
    source.configure(30_000_000.0, -30.0)?;
    source.output_enable(true)?;
    assert!(source.output_state_query()?);
    assert_eq!(RfReceiver(&executor).measure_frequency()?.unit, Unit::Hertz);
    assert_eq!(RfReceiver(&executor).measure_power()?.unit, Unit::Dbm);
    executor.complete();
    Ok(())
}
#[cfg(feature = "async")]
pub async fn asynchronous() -> Result<()> {
    let executor = Executor::new();
    assert_eq!(
        AsyncDataAcquisition(&executor)
            .measure_voltage_dc(7, None)
            .await?
            .unit,
        Unit::Volt
    );
    let source = AsyncRfSignalGenerator(&executor);
    source.configure(30_000_000.0, -30.0).await?;
    source.output_enable(true).await?;
    assert!(source.output_state_query().await?);
    assert_eq!(
        AsyncRfReceiver(&executor).measure_frequency().await?.unit,
        Unit::Hertz
    );
    assert_eq!(
        AsyncRfReceiver(&executor).measure_power().await?.unit,
        Unit::Dbm
    );
    executor.complete();
    Ok(())
}
