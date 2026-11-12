use instrument::prelude::*;

fn candidate(scheme: &str) -> Candidate {
    Candidate {
        endpoint: Endpoint {
            scheme: scheme.into(),
            address: "fixture".into(),
        },
        identity: Identity::default(),
    }
}
#[test]
fn shared_catalog_keys_use_trimmed_ascii_case_only() {
    let data: serde_json::Value =
        serde_json::from_str(include_str!("catalog-identity-cases.json")).unwrap();
    for row in data["cases"].as_array().unwrap() {
        let left = candidate(row["left"].as_str().unwrap());
        let right = candidate(row["right"].as_str().unwrap());
        let both = DeviceCatalog::new(
            DriverRegistry::new([]).unwrap(),
            vec![left.clone(), right.clone()],
        );
        let single =
            DeviceCatalog::new(DriverRegistry::new([]).unwrap(), vec![left.clone()]).unwrap();
        if row["duplicate"].as_bool().unwrap() {
            assert_eq!(both.err().unwrap().kind, ErrorKind::InvalidArgument);
            assert_eq!(single.device(&right.endpoint).unwrap().candidate(), &left);
        } else {
            let both = both.unwrap();
            assert_eq!(both.candidates().len(), 2);
            assert_eq!(both.device(&right.endpoint).unwrap().candidate(), &right);
            assert_eq!(
                single.device(&right.endpoint).err().unwrap().kind,
                ErrorKind::DriverNotFound
            );
        }
    }
}
