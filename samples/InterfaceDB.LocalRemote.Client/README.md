# Local/remote and peer composition examples

Compiled examples for the MIT `InterfaceDB.Transport.Repositories` package.
`ExampleComposition.cs` combines a local SegmentCore cache with the online TCP repository.
`PeerExampleComposition.cs` shows the peer relay composition.

The shared contract and connection examples are in `../InterfaceDB.TcpRepository.Client`.
See [managed setups](../../documentation/server-managed-setups.md) for new server-managed
applications and [peer replication](../../documentation/peer-replicated-repository.md).
Server data policies and TLS/JWT enforcement remain required where configured.
