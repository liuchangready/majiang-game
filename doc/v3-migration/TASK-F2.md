# F2: Async UI lifecycle review fixes

Own core/MahjongGame.kt, presentation/**, screen/** only. F finished. Engine files may migrate shortly, UI stays core. No test edits. Read AsyncScreenAcceptanceTest and tools/architecture/V3RenderSmoke.java.

Concrete defects:
1. V3EndpointCompat still calls future.get(200ms) in start/submit/nextRound. Forbidden on UI. Make its mutation methods explicitly unsupported/rejected with zero side effects; all production UI must use async client. Existing explicit legacy PlayerEndpoint seam remains synchronous. Do not enqueue a network command and report a fake synchronous failure/success.
2. exitToMenu currently closes client but leaves MahjongGame.localHost alive. Close owned local host and dispose screen resources exactly once; route scene replacement through composition root disposing prior screen. Passing another LocalSession is not evidence it belongs to current host (`localHost != null` check is wrong). Track actual owned access identity when launching local; remote or unrelated access closes previous host.
3. Every future callback touching Screen state (connect/start/submit/nextRound/requestControl, toast/model changes) must dispatch on render/UI executor and be invalidated after dispose/exit. GameClient dispatch only covers its listeners; receipt futures complete on socket reader. Avoid late callbacks altering a new screen; loading handshake must not send start before successful connect or lose failed-start retry capability.
4. Current V3GameClientAdapter.isConnected = snapshot != null stays true after network disconnect, and takeover inferred from missing options can be wrong. Expose honest available client state; client transport improvement N3 will report close. Use client.access/closed/lifecycle only if actual API supported; do not fake connection success. Show disconnected/error and disable input. No need to guess AI status from turn ownership; server-visible control state integration can be added after endpoint supports it.
5. Review current private GameScreen constructor plus GameScreenClient secondary constructor identical signatures (nullable/default doesn't distinguish JVM signature). Ensure compile correctness using real constructor design.

Don't claim tests/compile passed without execution. Deliver F2, Codex runs actual regression/GL.
