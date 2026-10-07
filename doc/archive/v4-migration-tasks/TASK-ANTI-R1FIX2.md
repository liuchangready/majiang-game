# R1FIX2 — targeted remaining production repairs

Continue your existing R1FIX1 context. Do not reread whole repository or reimplement unrelated modules. Anti owns production repairs below; Codex has fixed test fixtures and runs all builds/tests. Do not change tools/architecture/v4-review or the fixture fixes. No command execution. Preserve current files. Keep delivery concise and do not claim no defects before testing.

## Actual independent results

build/v4-migration/R1FIX1-review.log: 21 tests, 16 pass, 5 fail. All five failures stem from two concrete defects below. Production compiles. Codex fixed UdpTableServerSmokeTest missing Fragmenter args, regression helper fragment reassembly, protocol test fixtures missing protocolVersion=4 and invalid hand-written snapshot, CONFLICT expected error set, and changed stale highest-seq assertion to first-arrival accepted + repeat rejected. These test edits are complete; preserve them.

1. ApiMessageCodec.decodePushEnvelope passes an object containing `full`/`isFullSnapshot` directly into strict ProtocolJson.PlayerUpdateAdapter. It throws `JsonParseException: Unknown field 'full' in PlayerUpdate`; all full PUSHes are dropped. Read and strictly validate ONE canonical explicit Boolean marker, remove envelope fields from a COPY before decoding PlayerUpdate (or use proper envelope wrapper consistently). Do not loosen the domain adapter's unknown-field guard. No cursor-shape fallback. Preserve encodePush(update,isFullSnapshot=...) signatures used by tests.

2. UdpTableServer.close still begins `isClosed.compareAndSet(false,true)` and `ticker.shutdownNow()`. This prevents send(), receiveLoop and retry scheduler from operating BEFORE KICK dispatch. Use existing isClosing CAS to own shutdown; reject new business work while closing, but KEEP socket receiver/send and scheduled KICK retries/ACK handling alive. Initiate terminal notifications while isClosed=false; wait for matching ACK or bounded retry exhaustion; THEN set isClosed, stop ticker, close sessions/socket/executors. No early session.close before reliable KICK completes. Drain budget must cover retry schedule (currently 0/100/300ms plus ack, so 200ms is insufficient; choose bounded ~1000ms). Concurrent/duplicate close calls must be safe, receiver-thread close must not self-join/deadlock. sendKick must finish/clean old entry if replaced and handle scheduling rejection.

3. handleAck currently removes inFlightKicks solely by connectionId. Only matching ackSeq == kick.seq and correct source address/connection may acknowledge that KICK. A delayed ACK for an earlier response must not cancel terminal retries. Client terminal ACK grace must cover retry timings. Reviewer V4DatagramLossAcceptanceTest drops first KICK and first terminal ACK; shutdown must survive both.

4. ReplayGuardTest.reset sets new maxSeen baseline fails: initialMaxSeen=50 then accept(40) must reject, and explicit reset(10) establishes a floor. Keep a reset floor distinct from highest seen; allow unseen reordered packets above that baseline within the 256 window, reject any <= baseline. Do NOT revert highest-only rejection. Also validate positive windowSize.

5. Finish actual resource bounds: UdpClientSession.putInFlight currently evicts only if oldest >15s then inserts even when size already256. Reject/complete excess in-flight admission without growing beyond bound; expire completed/tombstone metadata on injected clock, ensure exceptions remove pending entries and complete futures. This was already required in R1; do not just report bounded caches while code admits more.

Read only the specific relevant files and logs. Implement these corrections and write delivery-ANTI-R1FIX2.md with actual files and any unresolved issues. No R2 work yet.
