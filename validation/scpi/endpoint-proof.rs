// Shared executable routing proof: one ID, two different connection inventories.
pub mod endpoint_proof {
    use instrument_api::{catalog::*, *};
    use instrument_runtime::*;
    use instrument_scpi::*;
    use instrument_testkit::*;
    use std::{collections::BTreeSet, sync::Arc};
    #[derive(Clone)]
    struct Connections {
        known: Script,
        unknown: Script,
    }
    impl Connector for Connections {
        fn open(&self, context: &OpenContext<'_>) -> Result<Box<dyn MessageIo>> {
            Connector::open(
                if context.candidate.endpoint.address == "known" {
                    &self.known
                } else {
                    &self.unknown
                },
                context,
            )
        }
    }
    #[cfg(feature = "async")]
    impl AsyncConnector for Connections {
        fn open<'a>(
            &'a self,
            context: &'a OpenContext<'_>,
        ) -> DriverFuture<'a, Box<dyn AsyncMessageIo>> {
            AsyncConnector::open(
                if context.candidate.endpoint.address == "known" {
                    &self.known
                } else {
                    &self.unknown
                },
                context,
            )
        }
    }
    fn setup() -> Result<(Profile, Profile, Connections)> {
        let original = super::generated_profiles::profiles()?.remove(0);
        // A known-empty domain cannot advertise any channel-bearing operation.
        let mut checked=0;
        for plan in original.plans().filter(|p|p.operation.metadata().parameters.iter().any(|a|a.kind==ParameterKind::Channel)) {
            let result=Profile::new("synthetic.empty-channels",Some(vec![]),None,vec![plan.clone()]);
            assert_eq!(result.err().unwrap().kind,ErrorKind::DriverContractViolation);checked+=1;
        }
        assert!(checked>0);
        assert_eq!(original.bind_inventory(&InventoryBinding{channels:Some(vec![]),outputs:None},Some(vec![])).err().unwrap().kind,ErrorKind::DriverContractViolation);
        // Empty channel sets are valid when operations have no channel argument.
        let dmm=original.plans().find(|p|p.operation==CatalogOperation::DmmMeasureVoltageDc).unwrap().clone();
        Profile::new("synthetic.outputless-dmm",Some(vec![]),Some(vec![]),vec![dmm])?;
        let count=original.plans().find(|p|p.operation==CatalogOperation::DcPowerSupplyChannelCount).unwrap().clone();
        Profile::new("synthetic.empty-count",Some(vec![]),Some(vec![]),vec![count])?;
        let unknown = original.bind_inventory(
            &InventoryBinding {
                channels: Some(vec![1]),
                outputs: None,
            },
            Some(vec![]),
        )?;
        let known = original.bind_inventory(
            &InventoryBinding {
                channels: Some(vec![1, 3]),
                outputs: Some(vec![
                    Output::DcPowerSupply(1),
                    Output::DcPowerSupply(3),
                    Output::FunctionGenerator,
                ]),
            },
            Some(vec![]),
        )?;
        let io = Connections {
            known: Script::new([
                ScriptStep::Write(":TEST:DcPowerSupplyOutputEnable 1,OFF".into()),
                ScriptStep::Write(":TEST:DcPowerSupplyOutputEnable 3,OFF".into()),
                ScriptStep::Write(":TEST:FunctionGeneratorOutputEnable OFF".into()),
                ScriptStep::Close(Ok(())),
            ]),
            unknown: Script::new([
                ScriptStep::Query(":TEST:DcPowerSupplyReadVoltage? 1".into(), Ok("2.2".into())),
                ScriptStep::Close(Ok(())),
            ]),
        };
        Ok((unknown, known, io))
    }
    fn candidate(address: &str) -> Candidate {
        Candidate {
            endpoint: Endpoint {
                scheme: " ScPi ".into(),
                address: address.into(),
            },
            identity: Identity::default(),
        }
    }
    fn required() -> BTreeSet<Operation> {
        [
            CatalogOperation::Shutdown,
            CatalogOperation::DcPowerSupplyChannelCount,
        ]
        .into_iter()
        .map(Operation::Catalog)
        .collect()
    }
    fn selection() -> Selection {
        Selection {
            allow_standard_scpi: true,
            ..Selection::default()
        }
    }
    pub fn sync() -> Result<()> {
        let (unknown, known, io) = setup()?;
        let factory = ScpiFactory::new(unknown.clone(), "scpi", Arc::new(io.clone()))
            .with_endpoint_profiles(vec![(candidate("known").endpoint, known.clone())])?;
        let registry = DriverRegistry::new([Arc::new(factory) as Arc<dyn DriverFactory>])?;
        assert_eq!(
            registry
                .open(
                    &candidate("unknown"),
                    &selection(),
                    &required(),
                    ExecutionMode::Sync,
                    ConnectionOwnership::Owned
                )
                .err()
                .unwrap()
                .kind,
            ErrorKind::UnsupportedOperation
        );
        assert_eq!(io.unknown.opens(), 0);
        let a = registry.open(
            &candidate("known"),
            &selection(),
            &required(),
            ExecutionMode::Sync,
            ConnectionOwnership::Owned,
        )?;
        let b = registry.open(
            &candidate("unknown"),
            &selection(),
            &BTreeSet::new(),
            ExecutionMode::Sync,
            ConnectionOwnership::Owned,
        )?;
        assert_eq!(a.evidence().driver_id, b.evidence().driver_id);
        assert_eq!(
            a.invoke(CatalogRequest::DcPowerSupplyChannelCount)?,
            CatalogReply::Count(2)
        );
        assert_eq!(
            b.invoke(CatalogRequest::DcPowerSupplyChannelCount)
                .unwrap_err()
                .kind,
            ErrorKind::UnsupportedOperation
        );
        assert_eq!(DcPowerSupply(&b).read_voltage(1)?.value, 2.2);
        a.close(true)?;
        b.close(false)?;
        io.known.assert_complete()?;
        io.unknown.assert_complete()?;
        assert_eq!(io.known.opens(), 1);
        assert_eq!(io.unknown.opens(), 1);
        let fail = Script::new([]);
        assert!(
            ScpiFactory::new(unknown.clone(), "scpi", Arc::new(fail.clone()))
                .with_endpoint_profiles(vec![
                    (candidate("known").endpoint, known.clone()),
                    (
                        Endpoint {
                            scheme: "scpi".into(),
                            address: "known".into()
                        },
                        known.clone()
                    )
                ])
                .is_err()
        );
        let other = Profile::new(
            "synthetic.other",
            None,
            Some(vec![]),
            vec![Plan::new(
                CatalogOperation::Reset,
                vec![Step::write("*RST")],
            )],
        )?;
        assert!(ScpiFactory::new(unknown, "scpi", Arc::new(fail.clone()))
            .with_endpoint_profiles(vec![(candidate("known").endpoint, other)])
            .is_err());
        assert_eq!(fail.opens(), 0);
        Ok(())
    }
    #[cfg(feature = "async")]
    pub async fn asynchronous() -> Result<()> {
        for variant in 0..4 {
            let mode=if variant==3 {0}else{variant};
            let (unknown, known, io) = setup()?;
            let factory: Arc<dyn DriverFactory> = if variant == 0 {
                Arc::new(
                    AsyncScpiFactory::new(unknown, "scpi", Arc::new(io.clone()))
                        .with_endpoint_profiles(vec![(candidate("known").endpoint, known)])?,
                )
            } else {
                Arc::new(
                    ScpiFactory::new(unknown, "scpi", Arc::new(io.clone()))
                        .with_native_connector(Arc::new(io.clone()))
                        .with_endpoint_profiles(vec![(candidate("known").endpoint, known)])?,
                )
            };
            let registry = DriverRegistry::new([factory])?;
            if mode == 0 {
                assert_eq!(
                    registry
                        .open_native(
                            &candidate("unknown"),
                            &selection(),
                            &required(),
                            ConnectionOwnership::Owned
                        )
                        .await
                        .err()
                        .unwrap()
                        .kind,
                    ErrorKind::UnsupportedOperation
                );
                assert_eq!(io.unknown.opens(), 0);
                let a = registry
                    .open_native(
                        &candidate("known"),
                        &selection(),
                        &required(),
                        ConnectionOwnership::Owned,
                    )
                    .await?;
                let b = registry
                    .open_native(
                        &candidate("unknown"),
                        &selection(),
                        &BTreeSet::new(),
                        ConnectionOwnership::Owned,
                    )
                    .await?;
                assert_eq!(
                    a.invoke_async(CatalogRequest::DcPowerSupplyChannelCount)
                        .await?,
                    CatalogReply::Count(2)
                );
                assert_eq!(
                    b.invoke_async(CatalogRequest::DcPowerSupplyChannelCount)
                        .await
                        .unwrap_err()
                        .kind,
                    ErrorKind::UnsupportedOperation
                );
                assert_eq!(AsyncDcPowerSupply(&b).read_voltage(1).await?.value, 2.2);
                a.close(true).await?;
                b.close(false).await?;
            } else {
                assert_eq!(
                    registry
                        .open_worker(
                            &candidate("unknown"),
                            &selection(),
                            &required(),
                            ConnectionOwnership::Owned
                        )
                        .await
                        .err()
                        .unwrap()
                        .kind,
                    ErrorKind::UnsupportedOperation
                );
                assert_eq!(io.unknown.opens(), 0);
                let a = registry
                    .open_worker(
                        &candidate("known"),
                        &selection(),
                        &required(),
                        ConnectionOwnership::Owned,
                    )
                    .await?;
                let b = registry
                    .open_worker(
                        &candidate("unknown"),
                        &selection(),
                        &BTreeSet::new(),
                        ConnectionOwnership::Owned,
                    )
                    .await?;
                if mode == 1 {
                    assert_eq!(
                        a.invoke_async(CatalogRequest::DcPowerSupplyChannelCount)
                            .await?,
                        CatalogReply::Count(2)
                    );
                    assert_eq!(
                        b.invoke_async(CatalogRequest::DcPowerSupplyChannelCount)
                            .await
                            .unwrap_err()
                            .kind,
                        ErrorKind::UnsupportedOperation
                    );
                    assert_eq!(AsyncDcPowerSupply(&b).read_voltage(1).await?.value, 2.2);
                } else {
                    assert_eq!(
                        a.invoke(CatalogRequest::DcPowerSupplyChannelCount)?,
                        CatalogReply::Count(2)
                    );
                    assert_eq!(
                        b.invoke(CatalogRequest::DcPowerSupplyChannelCount)
                            .unwrap_err()
                            .kind,
                        ErrorKind::UnsupportedOperation
                    );
                    assert_eq!(DcPowerSupply(&b).read_voltage(1)?.value, 2.2);
                }
                a.close(true).await?;
                b.close(false).await?;
                io.known.assert_thread_affinity()?;
                io.unknown.assert_thread_affinity()?;
            }
            io.known.assert_complete()?;
            io.unknown.assert_complete()?;
            assert_eq!(io.known.opens(), 1);
            assert_eq!(io.unknown.opens(), 1);
        }
        Ok(())
    }
}
