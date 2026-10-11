import com.majiang.client.api.*;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.List;
import java.util.UUID;
import java.util.concurrent.CompletableFuture;
import kotlin.Unit;
import kotlin.jvm.functions.Function1;

/** Client-only, deterministic authority fixture for actual V4 OpenGL screen checks. */
public class V4SmokeSession implements GameSession {
    private GameSnapshotView snapshot;
    private long cursor;
    private int roundCounter;
    private int settlementCounter;
    private boolean closed;
    private String disconnectReason;
    private ConnectionState connectionState = ConnectionState.CONNECTED;
    private final List<Function1<? super GameUpdateView, Unit>> listeners = new ArrayList<>();
    private final List<Function1<? super String, Unit>> disconnectListeners = new ArrayList<>();
    public final List<ActionChoice> submitted = new ArrayList<>();
    public final List<LegalActionView> submittedActions = new ArrayList<>();
    public int nextRounds;

    public V4SmokeSession() {
        publish(emptyView(2));
    }

    public static PlayerView emptyView(int seat) {
        List<List<TileView>> discards = Arrays.asList(Collections.emptyList(), Collections.emptyList(), Collections.emptyList(), Collections.emptyList());
        List<List<MeldView>> melds = Arrays.asList(Collections.emptyList(), Collections.emptyList(), Collections.emptyList(), Collections.emptyList());
        return new PlayerView(seat, GamePhaseView.STACKING, 1, 1, 0, 0, 0, seat, 70,
            Collections.emptyList(), Arrays.asList(25000, 25000, 25000, 25000),
            Arrays.asList(false, false, false, false), discards, melds,
            Arrays.asList(13, 13, 13, 13), Collections.emptyList(), null,
            Collections.emptyList(), null, null, Collections.emptyList(),
            new FuritenStatusView(false, false, false), Collections.emptyList());
    }

    public static List<ActionOptionView> options(LegalActionView... actions) {
        List<ActionOptionView> out = new ArrayList<>();
        for (int i = 0; i < actions.length; i++) {
            out.add(new ActionOptionView("smoke-option-" + i, actions[i]));
        }
        return out;
    }

    public void publish(PlayerView view) {
        publish(view, Collections.emptyList(), null, Collections.emptyList(), null, null);
    }

    public void publish(PlayerView view, List<ActionOptionView> options,
                        PlayerPresentationPreviewView preview) {
        publish(view, options, preview, Collections.emptyList(), null, null);
    }

    public void publish(PlayerView view, List<ActionOptionView> options,
                        PlayerPresentationPreviewView preview, List<GameEventView> events,
                        SettlementDetailView settlement, SessionProgressView progress) {
        long prior = cursor;
        cursor++;
        String roundId = "smoke-round-" + roundCounter;
        String decisionId = "smoke-decision-" + cursor;
        if (preview != null) {
            roundId = preview.getRoundId();
            decisionId = preview.getDecisionId();
        }
        List<LegalActionView> actions = new ArrayList<>();
        for (ActionOptionView option : options) actions.add(option.getAction());
        DecisionView decision = new DecisionView(roundId, decisionId, 0L, actions, options);
        snapshot = new GameSnapshotView("smoke-session", roundId, "smoke-epoch", cursor,
            1L, 1L, view, decision, Collections.emptyList(),
            Arrays.asList(100, 100, 100, 100), progress, Collections.emptyList(), false,
            settlement != null ? "smoke-settlement-" + (++settlementCounter) : null,
            events, settlement, null, preview, null);
        GameUpdateView update = new GameUpdateView(snapshot, prior, events, false);
        for (Function1<? super GameUpdateView, Unit> listener : new ArrayList<>(listeners)) listener.invoke(update);
    }

    public void publishSnapshot(GameSnapshotView next, List<GameEventView> events) {
        long prior = snapshot == null ? 0L : snapshot.getCursor();
        snapshot = next;
        cursor = next.getCursor();
        GameUpdateView update = new GameUpdateView(next, prior, events, false);
        for (Function1<? super GameUpdateView, Unit> listener : new ArrayList<>(listeners)) listener.invoke(update);
    }

    @Override public boolean getClosed() { return closed; }
    @Override public String getDisconnectReason() { return disconnectReason; }
    @Override public ConnectionState getConnectionState() { return closed ? ConnectionState.CLOSED : connectionState; }
    public void setConnected(boolean connected) {
        if (!closed) connectionState = connected ? ConnectionState.CONNECTED : ConnectionState.RECONNECTING;
    }
    @Override public boolean isPending() { return false; }
    @Override public GameSnapshotView getCurrentSnapshot() { return snapshot; }
    @Override public CompletableFuture<GameSnapshotView> snapshot() { return CompletableFuture.completedFuture(snapshot); }
    @Override public CompletableFuture<DecisionView> decision() { return CompletableFuture.completedFuture(snapshot.getDecision()); }
    @Override public CompletableFuture<Receipt> submit(ActionChoice choice) {
        for (ActionOptionView option : snapshot.getDecision().getOptions()) {
            if (option.getOptionId().equals(choice.getOptionId()) &&
                snapshot.getDecision().getDecisionId().equals(choice.getDecisionId()) &&
                snapshot.getRoundId().equals(choice.getRoundId())) {
                submitted.add(choice);
                submittedActions.add(option.getAction());
                return CompletableFuture.completedFuture(new Receipt(choice.getRequestId(), ReceiptStatus.APPLIED, cursor, null));
            }
        }
        return CompletableFuture.completedFuture(new Receipt(choice.getRequestId(), ReceiptStatus.REJECTED, null, "ACTION_NOT_AVAILABLE"));
    }
    @Override public CompletableFuture<Receipt> submitShufflePlan(ShufflePlan plan) {
        return CompletableFuture.completedFuture(new Receipt(plan.getRequestId(), ReceiptStatus.REJECTED, null, "UNSUPPORTED_ACTION"));
    }
    @Override public CompletableFuture<Receipt> start() {
        return CompletableFuture.completedFuture(new Receipt(UUID.randomUUID().toString(), ReceiptStatus.APPLIED, cursor, null));
    }
    @Override public CompletableFuture<Receipt> nextRound() {
        nextRounds++;
        roundCounter++;
        return CompletableFuture.completedFuture(new Receipt(UUID.randomUUID().toString(), ReceiptStatus.APPLIED, cursor, null));
    }
    @Override public CompletableFuture<GameSnapshotView> resume(long requestedCursor) {
        return CompletableFuture.completedFuture(snapshot);
    }
    @Override public CompletableFuture<Receipt> claimControl() {
        return CompletableFuture.completedFuture(new Receipt(UUID.randomUUID().toString(), ReceiptStatus.APPLIED, cursor, null));
    }
    @Override public CompletableFuture<Receipt> releaseControl() {
        return CompletableFuture.completedFuture(new Receipt(UUID.randomUUID().toString(), ReceiptStatus.APPLIED, cursor, null));
    }
    @Override public CompletableFuture<List<GameEventView>> queryRoundResult(String roundId) {
        return CompletableFuture.completedFuture(snapshot.getSettlementEvents());
    }
    @Override public AutoCloseable onUpdate(Function1<? super GameUpdateView, Unit> listener) {
        listeners.add(listener);
        return () -> listeners.remove(listener);
    }
    @Override public AutoCloseable onDisconnect(Function1<? super String, Unit> listener) {
        disconnectListeners.add(listener);
        return () -> disconnectListeners.remove(listener);
    }
    @Override public AutoCloseable onStateChange(Function1<? super ConnectionState, Unit> listener) {
        return () -> {};
    }
    @Override public void close() {
        closed = true;
        disconnectReason = "CLOSED";
        listeners.clear();
        disconnectListeners.clear();
    }
}
