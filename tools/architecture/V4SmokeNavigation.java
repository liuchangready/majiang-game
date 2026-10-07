import com.majiang.MahjongGame;
import com.majiang.client.api.LobbySession;
import com.majiang.client.api.Receipt;
import com.majiang.client.api.ReceiptStatus;
import com.majiang.presentation.common.TableNavigation;
import com.majiang.presentation.menu.MenuScreen;
import java.util.UUID;
import java.util.concurrent.CompletableFuture;

/** Fixture navigation preserving V4 screen ownership and close-before-menu semantics. */
public final class V4SmokeNavigation implements TableNavigation {
    private final MahjongGame game;
    private final V4SmokeSession session;

    public V4SmokeNavigation(MahjongGame game, V4SmokeSession session) {
        this.game = game;
        this.session = session;
    }
    @Override public CompletableFuture<LobbySession> openLocalLobby() { return game.openLocalLobby(); }
    @Override public CompletableFuture<LobbySession> openRemoteLobby(String host, int port) { return game.openRemoteLobby(host,port); }
    @Override public void showPlayerTable() { throw new AssertionError("Fake session has no composition-root binding"); }
    @Override public void showSpectatorTable() { throw new AssertionError("Fake session has no spectator binding"); }
    @Override public CompletableFuture<Receipt> leaveToMenu() {
        session.close();
        game.changeScreen(new MenuScreen(game.getUiAssets(),game,null));
        return CompletableFuture.completedFuture(new Receipt(UUID.randomUUID().toString(), ReceiptStatus.APPLIED, null, null));
    }
    @Override public void exitGame() { game.exitGame(); }
}
