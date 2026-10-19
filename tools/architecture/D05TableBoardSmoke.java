import com.badlogic.gdx.ApplicationAdapter;
import com.badlogic.gdx.Gdx;
import com.badlogic.gdx.backends.lwjgl3.Lwjgl3Application;
import com.badlogic.gdx.backends.lwjgl3.Lwjgl3ApplicationConfiguration;
import com.badlogic.gdx.graphics.Pixmap;
import com.badlogic.gdx.graphics.PixmapIO;
import com.badlogic.gdx.utils.ScreenUtils;
import com.majiang.client.api.*;
import com.majiang.presentation.common.TableNavigation;
import com.majiang.presentation.common.UiAssets;
import com.majiang.presentation.common.render.SkinManager;
import com.majiang.presentation.game.SpectatorScreen;
import java.io.File;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CompletableFuture;
import kotlin.Unit;
import kotlin.jvm.functions.Function1;

/**
 * D-05 第二步/第三步真实 GL 冒烟：
 * - 观战共享牌桌渲染（赤牌、副露、四家牌河/牌背）；
 * - 观战独立弃牌动画、全量快照清空重建、换局；
 * - 观战公开结算详情（公开事件 + 已授权 breakdown，无任何玩家按钮）。
 */
public final class D05TableBoardSmoke extends ApplicationAdapter {
    private UiAssets assets;
    private SmokeSpectatorSession session;
    private SpectatorScreen screen;
    private int frame;
    private int shots;
    private final List<String> checks = new ArrayList<>();

    public static void main(String[] args) {
        Lwjgl3ApplicationConfiguration config = new Lwjgl3ApplicationConfiguration();
        config.setTitle("D-05 table board spectator smoke");
        config.setWindowedMode(1280, 720);
        config.setInitialVisible(false);
        config.disableAudio(true);
        config.setForegroundFPS(30);
        new Lwjgl3Application(new D05TableBoardSmoke(), config);
    }

    private static TileView v(SuitView suit, int rank, int copy) {
        int type = switch (suit) {
            case MAN -> rank - 1;
            case SOU -> 9 + rank - 1;
            case PIN -> 18 + rank - 1;
            case WIND -> 27 + rank - 1;
            case DRAGON -> 31 + rank - 1;
        };
        return TileView.Companion.fromTypeIndex(type, copy);
    }

    private static List<TileView> tiles(TileView... tiles) {
        return new ArrayList<>(Arrays.asList(tiles));
    }

    private static PublicBoardView board(
        GamePhaseView phase,
        int roundNumber,
        List<List<TileView>> discards,
        List<List<MeldView>> melds,
        List<Integer> handCounts,
        List<Integer> scores
    ) {
        return new PublicBoardView(
            phase, 1, roundNumber, 0, 0, 0, 0, 70,
            tiles(v(SuitView.PIN, 5, 3)),
            scores,
            Arrays.asList(false, false, false, false),
            discards, melds, handCounts, null, null
        );
    }

    private static PublicSnapshotView snapshot(
        long cursor,
        String roundId,
        PublicBoardView board,
        String settlementRoundId,
        List<GameEventView> settlementEvents
    ) {
        List<Integer> scores = board.getScores();
        PublicSessionSummaryView summary = new PublicSessionSummaryView(
            settlementRoundId != null ? "ROUND_RESULT" : "ROUND_ACTIVE",
            scores, 1, board.getRoundNumber(), 0, 0
        );
        return new PublicSnapshotView(
            "d05-smoke-session", 1L, roundId, 1L, "d05-smoke-epoch", cursor,
            board, summary, Collections.emptyList(), false,
            settlementRoundId, settlementEvents
        );
    }

    private static List<List<TileView>> emptyRivers() {
        return Arrays.asList(
            new ArrayList<>(), new ArrayList<>(), new ArrayList<>(), new ArrayList<>()
        );
    }

    private static List<List<MeldView>> emptyMelds() {
        return Arrays.asList(
            new ArrayList<>(), new ArrayList<>(), new ArrayList<>(), new ArrayList<>()
        );
    }

    private static ScoreBreakdownView breakdown() {
        DoraBreakdownView dora = new DoraBreakdownView(
            0, 0, 0, 0,
            Collections.emptyList(), Collections.emptyList(), Collections.emptyList(),
            Collections.emptyList(), Collections.emptyList()
        );
        FuBreakdownView fu = new FuBreakdownView(
            Collections.<FuLineView>emptyList(), 30, 30, 30, null
        );
        ScoreResultView score = new ScoreResultView(
            30, 2, 1000, "满贯", false, 0, 1000, null, "", "1000", null
        );
        return new ScoreBreakdownView(
            Arrays.asList(new kotlin.Pair<>("立直", 1), new kotlin.Pair<>("平和", 1)),
            0, dora, fu, score, Collections.emptyList(), 0, 0, 2000, false, false, true,
            Collections.emptyList(), null
        );
    }

    @Override
    public void create() {
        assets = UiAssets.Companion.createDefault(new SkinManager());
        List<List<TileView>> initialRivers = emptyRivers();
        initialRivers.get(0).add(v(SuitView.MAN, 5, 3)); // 赤五万
        initialRivers.get(0).add(v(SuitView.PIN, 3, 0));
        initialRivers.get(1).add(v(SuitView.WIND, 1, 0));
        initialRivers.get(2).add(v(SuitView.SOU, 7, 0));
        initialRivers.get(3).add(v(SuitView.DRAGON, 1, 0));

        List<List<MeldView>> melds = emptyMelds();
        melds.get(1).add(new MeldView(
            MeldTypeView.CHI,
            tiles(v(SuitView.PIN, 2, 0), v(SuitView.PIN, 3, 0), v(SuitView.PIN, 4, 0)),
            0,
            v(SuitView.PIN, 2, 0)
        ));
        melds.get(2).add(new MeldView(
            MeldTypeView.PON,
            tiles(v(SuitView.SOU, 8, 0), v(SuitView.SOU, 8, 1), v(SuitView.SOU, 8, 2)),
            1,
            v(SuitView.SOU, 8, 0)
        ));

        PublicBoardView board = board(
            GamePhaseView.DISCARD, 1, initialRivers, melds,
            Arrays.asList(13, 10, 7, 13),
            Arrays.asList(25000, 24000, 26000, 25000)
        );
        session = new SmokeSpectatorSession(snapshot(1, "d05-round-1", board, null, Collections.emptyList()));
        screen = new SpectatorScreen(assets, session, new SmokeNavigation(), null);
        screen.show();
    }

    private void clickWorld(int x, int y) {
        // SpectatorScreen.unproject expects libGDX window coordinates (origin top-left).
        screen.touchDown(x, 720 - y, 0, 0);
    }

    private void capture(String name) {
        File dir = new File(System.getProperty("smoke.output"));
        if (!dir.isDirectory() && !dir.mkdirs()) {
            throw new AssertionError("Cannot create screenshot directory: " + dir);
        }
        Pixmap pixmap = ScreenUtils.getFrameBufferPixmap(0, 0, 1280, 720);
        try {
            PixmapIO.writePNG(Gdx.files.absolute(new File(dir, name + ".png").getAbsolutePath()), pixmap, -1, true);
            shots++;
        } finally {
            pixmap.dispose();
        }
    }

    private void check(boolean condition, String message) {
        if (!condition) throw new AssertionError(message);
        checks.add(message);
    }

    private void publishAnimationUpdate() {
        List<List<TileView>> rivers = emptyRivers();
        rivers.get(0).add(v(SuitView.MAN, 5, 3));
        rivers.get(0).add(v(SuitView.PIN, 3, 0));
        rivers.get(1).add(v(SuitView.WIND, 1, 0));
        rivers.get(1).add(v(SuitView.SOU, 4, 0));
        rivers.get(2).add(v(SuitView.SOU, 7, 0));
        rivers.get(3).add(v(SuitView.DRAGON, 1, 0));
        PublicBoardView board = board(
            GamePhaseView.DISCARD, 1, rivers, emptyMelds(),
            Arrays.asList(13, 10, 7, 13),
            Arrays.asList(25000, 24000, 26000, 25000)
        );
        session.publish(snapshot(2, "d05-round-1", board, null, Collections.emptyList()),
            Collections.singletonList(new GameEventView.TileDiscarded(1, 1, v(SuitView.SOU, 4, 0), false, false)),
            false);
    }

    private void publishFullReset() {
        List<List<TileView>> rivers = emptyRivers();
        rivers.get(0).add(v(SuitView.SOU, 9, 0));
        PublicBoardView board = board(
            GamePhaseView.DISCARD, 2, rivers, emptyMelds(),
            Arrays.asList(13, 13, 13, 13),
            Arrays.asList(26000, 25000, 24000, 25000)
        );
        session.publish(snapshot(9, "d05-round-2", board, null, Collections.emptyList()),
            Collections.singletonList(new GameEventView.TileDiscarded(1, 0, v(SuitView.SOU, 9, 0), false, false)),
            true);
    }

    private void publishSettlement() {
        TileView winTile = v(SuitView.MAN, 1, 0);
        ScoreBreakdownView bd = breakdown();
        List<String> yaku = Arrays.asList("立直", "平和");
        Map<Integer, Integer> payments = new LinkedHashMap<>();
        payments.put(0, -700);
        payments.put(1, -700);
        payments.put(2, 2000);
        payments.put(3, -600);
        List<Integer> scores = Arrays.asList(24300, 24300, 27000, 24400);
        GameEventView.TsumoWin tsumo = new GameEventView.TsumoWin(12, 2, winTile, yaku, 2, 30, 2000, bd);
        GameEventView.ScoreSettled settled = new GameEventView.ScoreSettled(12, payments, scores);
        PublicBoardView board = board(
            GamePhaseView.ROUND_END, 2, emptyRivers(), emptyMelds(),
            Arrays.asList(13, 13, 13, 13), scores
        );
        session.publish(
            snapshot(11, "d05-round-2", board, "d05-round-2", Arrays.asList(tsumo, settled)),
            Arrays.asList(tsumo, settled), false
        );
    }

    @Override
    public void render() {
        screen.render(Gdx.graphics.getDeltaTime());
        frame++;
        if (frame == 3) {
            capture("spectator-table-red-meld");
        } else if (frame == 5) {
            publishAnimationUpdate();
        } else if (frame == 8) {
            check(screen.isDiscardPlaybackBusy(), "spectator discard animation is active");
            capture("spectator-discard-flight");
        } else if (frame == 25) {
            publishFullReset();
        } else if (frame == 27) {
            check(!screen.isDiscardPlaybackBusy(), "full snapshot cleared spectator animation queue");
            check(screen.getCurrentSnapshot().getCursor() == 9L, "full snapshot cursor adopted");
            capture("spectator-full-reset");
        } else if (frame == 30) {
            publishSettlement();
        } else if (frame == 32) {
            check(screen.isSettlementShown(), "spectator public settlement is visible");
            capture("spectator-settlement-yaku");
        } else if (frame == 34) {
            clickWorld(410, 530);
        } else if (frame == 36) {
            check(screen.getSettlementSection() == com.majiang.presentation.common.SettlementSection.DORA,
                "spectator dora tab is navigable");
            capture("spectator-settlement-dora");
        } else if (frame == 38) {
            clickWorld(590, 530);
        } else if (frame == 40) {
            check(screen.getSettlementSection() == com.majiang.presentation.common.SettlementSection.CALC,
                "spectator calc tab is navigable");
            capture("spectator-settlement-calc");
        } else if (frame == 42) {
            // No next-round button exists for spectators; clicking its player-screen area must not submit anything.
            clickWorld(640, 165);
            check(screen.isSettlementShown(), "spectator settlement has no next-round action");
        } else if (frame == 44) {
            System.out.println("D05_TABLEBOARD_SMOKE_PASS screenshots=" + shots +
                " checks=" + checks.size() + " frames=" + frame);
            Gdx.app.exit();
        } else if (frame > 300) {
            throw new AssertionError("D-05 spectator GL smoke did not finish");
        }
    }

    @Override
    public void resize(int width, int height) {
        if (screen != null) screen.resize(width, height);
    }

    @Override
    public void dispose() {
        if (screen != null) screen.dispose();
        if (assets != null) assets.dispose();
    }

    private static final class SmokeNavigation implements TableNavigation {
        @Override public CompletableFuture<LobbySession> openLocalLobby() {
            return CompletableFuture.completedFuture(null);
        }
        @Override public CompletableFuture<LobbySession> openRemoteLobby(String host, int port) {
            return CompletableFuture.completedFuture(null);
        }
        @Override public void showPlayerTable() {}
        @Override public void showSpectatorTable() {}
        @Override public CompletableFuture<Receipt> leaveToMenu() {
            return CompletableFuture.completedFuture(null);
        }
        @Override public void exitGame() { Gdx.app.exit(); }
    }

    private static final class SmokeSpectatorSession implements SpectatorSession {
        private PublicSnapshotView snapshot;
        private boolean closed;
        private final List<Function1<? super PublicUpdateView, Unit>> listeners = new ArrayList<>();

        private SmokeSpectatorSession(PublicSnapshotView initial) {
            this.snapshot = initial;
        }

        private void publish(PublicSnapshotView next, List<GameEventView> events, boolean full) {
            long from = full ? next.getCursor() : next.getCursor() - 1L;
            snapshot = next;
            PublicUpdateView update = new PublicUpdateView(next, from, events, full);
            for (Function1<? super PublicUpdateView, Unit> listener : new ArrayList<>(listeners)) {
                listener.invoke(update);
            }
        }

        @Override public boolean getClosed() { return closed; }
        @Override public String getDisconnectReason() { return closed ? "CLOSED" : null; }
        @Override public ConnectionState getConnectionState() {
            return closed ? ConnectionState.CLOSED : ConnectionState.CONNECTED;
        }
        @Override public PublicSnapshotView getCurrentPublicSnapshot() { return snapshot; }
        @Override public AutoCloseable onStateChange(Function1<? super ConnectionState, Unit> listener) {
            return () -> {};
        }
        @Override public CompletableFuture<PublicSnapshotView> publicSnapshot() {
            return CompletableFuture.completedFuture(snapshot);
        }
        @Override public CompletableFuture<PublicSnapshotView> resumePublic(PublicCheckpoint checkpoint) {
            return CompletableFuture.completedFuture(snapshot);
        }
        @Override public CompletableFuture<List<GameEventView>> queryPublicRoundResult(String roundId) {
            return CompletableFuture.completedFuture(snapshot.getSettlementEvents());
        }
        @Override public AutoCloseable onPublicUpdate(Function1<? super PublicUpdateView, Unit> listener) {
            listeners.add(listener);
            return () -> listeners.remove(listener);
        }
        @Override public AutoCloseable onDisconnect(Function1<? super String, Unit> listener) {
            return () -> {};
        }
        @Override public CompletableFuture<RoomInfo> getRoomInfo() {
            return CompletableFuture.completedFuture(new RoomInfo(
                "d05-smoke-session", RoomLifecycle.PLAYING, 0,
                Collections.emptyList(), new RoomConfig()
            ));
        }
        @Override public AutoCloseable onRoomUpdate(Function1<? super RoomInfo, Unit> listener) {
            return () -> {};
        }
        @Override public CompletableFuture<Receipt> leaveRoom() {
            return CompletableFuture.completedFuture(
                new Receipt("leave", ReceiptStatus.APPLIED, null, null)
            );
        }
        @Override public void close() { closed = true; listeners.clear(); }
    }
}
