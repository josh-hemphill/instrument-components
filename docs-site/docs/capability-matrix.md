# Capability matrix

The SDK catalogs eight classes and 83 operations. Every operation is independently advertised; a class does not imply that all its operations are supported. Device-specific behavior and all legacy model hints are maintained in the five external repositories linked from [Getting started](getting-started.md).

| Capability | Requirement |
|---|---|
| Typed measurement/configuration | Explicit device-owned operation plan or neutral backend implementation |
| Channel addressing | Frozen supported channel IDs; sparse IDs remain sparse |
| PSU ChannelCount | Complete declared physical output inventory |
| Checked Shutdown | Complete inventory and disable coverage for every physical output |
| Native async | Explicit native async connector/backend |
| Worker async/blocking | Dedicated sync-transport worker |
| Temperature Read/Fetch units | Explicit known device unit or prior successful configuration; otherwise Unknown |
| Identity | Explicit operation; passive matching does not query |
| Reset/recovery | Explicit lifecycle operation; invalidates configured unit state |

The old model registry was an unvalidated hint list. All 47 manufacturer/model hints plus one USB hint are migrated as source evidence, including the multifunction 2602B. No hardware-ready support claim follows from matching a model name. Source-derived profiles and exact software transcripts are the initial evidence level; physical bench records follow reviewed previews.
