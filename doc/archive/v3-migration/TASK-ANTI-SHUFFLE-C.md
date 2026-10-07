# C：客户端与真实网络洗牌操作

Read SHUFFLE-CONTRACT.md. Own client/src/main/** and server/src/main/** ONLY. A stackingtypes and B engineinterfaces can be absent initially; implement against frozen signatures. No core/engine/tests/build/shell.

Add GameClient.submitShufflePlan(request) same asynchronous pending/error/close semantics as choose; precheck connected, latestpreparation SELECTING,notconfirmed,requestround/currentowncontrolEpoch,affordablecost. Authority revalidates; don'tgeneratewallclient. Submitting waitsreceipt nonblocking, latecallbacksaftercloseignored; use existing pending paths rather than duplicated race-proneflags.

Add SessionAccess method override in NetworkSessionAccess, new typed ClientMessage SubmitShufflePlan (request id correlation distinct/consistent with request.requestId); dispatch onserver aftervalidate activeconnectiongeneration/token. Forward to bound LocalSession method, preserve ActionReceiptstatus/reason. Extend explicit ProtocolJson adapters: snapshot.preparation optionalnull, ShuffleSelection enumsets, request/status strictallowedfields/knownenums, positiveboundedclockvalues, invalidpayloadreject through Future/connection handling nothanging. Don't serialize internalseed/wall/otherseatplans. Readonly oldsnapshots withoutprep accepted; if bumpversion, updatesingleprotocolconstantconsistently existingversion3tests usecurrentversion? Prefer additive same3 with AccessCapabilities optional? State feature compatibility honestly; no silentdropclientrequest.

ServerRoom uses configured TableHost samecapability locally/remotely; preserve config.shuffleConfig, no constructor default silentlydisabling. Deepcopysets andnetworkvalidation don'ttrustGson constructors. Report concise delivery-ANTI-SHUFFLE-C.md, no testclaims.
