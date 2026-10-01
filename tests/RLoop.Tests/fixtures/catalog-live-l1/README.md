# Live metadata excerpt for L1

Source: `C:\Users\jojoh\Documents\resoloop\.ccg\tasks\resoloop-slimming\s2-live\evidence\snapshot.live.json`.
Captured by Opus on 2026-10-02 (Japan time); original identity timestamp
`2026-10-01T22:53:51.7540969+00:00`. Resonite `2026.9.18.82`, server
ResoniteLink `0.13.1.0`, client YellowDogMan.ResoniteLink `0.13.1`.
The corresponding original catalog is `evidence/catalog.live.json`, mapper 1.

`metadata.live-excerpt.json` retains the original TypeDefinition objects for
SphereMesh, PBS_Metallic and MeshRenderer, plus IPBS_Metallic and the three
IAssetProvider definitions (non-generic, Mesh, Material). Only MeshRenderer's
Mesh member is retained; other members and methods are omitted, and unrelated
type/SyncObject/alias/enum records are omitted. These are SDK-serialized metadata,
not original wire packets. The excerpt is not a complete acquired catalog.

The tests construct mapper-2 identity and hash around this excerpt. Extra
independent SphereMesh/SyncObject definitions used by tests are explicitly
synthetic fake responses, not observations from the server. They test preservation
of confirmed edges without claiming that a fresh server response supplies them.
Expected outcomes are handwritten in `CatalogLiveFixTests.cs`.
