import com.badlogic.gdx.ApplicationAdapter;
import com.badlogic.gdx.Gdx;
import com.badlogic.gdx.Input;
import com.badlogic.gdx.backends.lwjgl3.Lwjgl3Application;
import com.badlogic.gdx.backends.lwjgl3.Lwjgl3ApplicationConfiguration;
import com.badlogic.gdx.graphics.Pixmap;
import com.badlogic.gdx.graphics.PixmapIO;
import com.badlogic.gdx.math.Rectangle;
import com.badlogic.gdx.utils.ScreenUtils;
import com.majiang.MahjongGame;
import com.majiang.client.api.*;
import com.majiang.presentation.game.GameScreen;
import com.majiang.presentation.menu.MenuScreen;
import java.io.File;
import java.util.UUID;

/** Real GL plus product composition, authority, private push and actual pointer input. */
public final class SleeveBloomRenderSmoke extends ApplicationAdapter {
    private MahjongGame game;
    private int frames, offeredFrame = -1, keptFrame = -1, markFrame = -1, disconnectFrame = -1, beforePoints, expectedTile, completed;
    private long originalDeadline;
    private boolean skipped, activated, chosen, leaving, checkedMarkModal;
    private final boolean disconnectProbe = Boolean.getBoolean("smoke.disconnect");

    public static void main(String[] args) {
        var config = new Lwjgl3ApplicationConfiguration();
        config.setTitle("Sleeve bloom acceptance");
        int width = Integer.getInteger("smoke.width", 1280);
        config.setWindowedMode(width, width * 720 / 1280);
        config.setInitialVisible(false);
        config.disableAudio(true);
        config.setForegroundFPS(30);
        new Lwjgl3Application(new SleeveBloomRenderSmoke(), config);
    }
    @Override public void create() { game = new MahjongGame(); game.create(); }
    @Override public void resize(int w, int h) { if (game != null) game.resize(w, h); }
    @Override public void dispose() { if (game != null) game.dispose(); }
    private void check(boolean value, String text) { if (!value) throw new AssertionError(text); }
    private void click(GameScreen screen, Rectangle rect) {
        screen.touchDown((int)((rect.x + rect.width / 2) * Gdx.graphics.getWidth() / 1280f),
            (int)((720f - rect.y - rect.height / 2) * Gdx.graphics.getHeight() / 720f), 0, 0);
    }
    private void capture(String name) {
        File dir = new File(System.getProperty("smoke.output"));
        check(dir.isDirectory() || dir.mkdirs(), "Cannot create screenshots");
        Pixmap pixels = ScreenUtils.getFrameBufferPixmap(0, 0, Gdx.graphics.getBackBufferWidth(), Gdx.graphics.getBackBufferHeight());
        try { PixmapIO.writePNG(Gdx.files.absolute(new File(dir, name + ".png").getAbsolutePath()), pixels, -1, true); }
        finally { pixels.dispose(); }
    }
    private void checkDisabledFill(Rectangle rect, String reason) {
        int x = (int)((rect.x + 8) * Gdx.graphics.getWidth() / 1280f);
        int y = (int)((rect.y + 8) * Gdx.graphics.getHeight() / 720f);
        Pixmap pixel = ScreenUtils.getFrameBufferPixmap(x, y, 1, 1);
        try {
            int rgba = pixel.getPixel(0, 0);
            int r = (rgba >>> 24) & 255, g = (rgba >>> 16) & 255, b = (rgba >>> 8) & 255;
            check(Math.max(r, Math.max(g, b)) < 160 && Math.max(r, Math.max(g, b)) - Math.min(r, Math.min(g, b)) < 40,
                reason + " rgb=" + r + "," + g + "," + b);
        } finally { pixel.dispose(); }
    }
    @Override public void render() {
        game.render();
        check(++frames < 2100, "Sleeve bloom GL smoke timed out");
        if (frames == 3) {
            check(game.getScreen() instanceof MenuScreen, "Menu missing");
            ((MenuScreen)game.getScreen()).touchDown(Gdx.graphics.getWidth() / 2, 471 * Gdx.graphics.getHeight() / 720, 0, 0);
        }
        if (frames == 5) {
            capture("technique-rules");
            ((MenuScreen)game.getScreen()).keyDown(Input.Keys.ESCAPE);
        }
        if (frames == 7) {
            check(game.getScreen() instanceof MenuScreen, "Menu missing before start");
            ((MenuScreen)game.getScreen()).touchDown(Gdx.graphics.getWidth() / 2, 275 * Gdx.graphics.getHeight() / 720, 0, 0);
        }
        if (!(game.getScreen() instanceof GameScreen screen)) {
            if (leaving && game.getScreen() instanceof MenuScreen) {
                check(game.getCurrentSession() == null && game.getInProcessServer() == null, "Resources leaked on return");
                System.out.println((disconnectProbe ? "SLEEVE_BLOOM_DISCONNECT_GL_PASS fee=0" : "SLEEVE_BLOOM_GL_PASS choices=2 fee=20") + " frames=" + frames);
                Gdx.app.exit();
            }
            return;
        }
        if (leaving) return;
        if (disconnectFrame >= 0) {
            if (frames < disconnectFrame + 2) return;
            check(screen.getSession().getClosed(), "Real session did not close");
            check(!screen.getSleeveBloomPanel().getModel().getCanActivate(), "Disconnected model still allows activation");
            capture("disconnected-entry");
            checkDisabledFill(screen.getSleeveBloomPanel().getBtnOpenRect(), "Bloom must look disabled after disconnect");
            check(screen.getCurrentSnapshot().getCheatPoints().get(screen.getCurrentSnapshot().getViewerSeat()) == beforePoints,
                "Disconnect probe changed cheat points");
            screen.keyDown(Input.Keys.ESCAPE);
            leaving = true;
            return;
        }
        if (screen.getShufflePreparationPanel().isActive()) {
            if (!skipped) { click(screen, screen.getShufflePreparationPanel().getBtnSkipRect()); skipped = true; }
            return;
        }
        var snapshot = screen.getCurrentSnapshot();
        if (snapshot == null || screen.isDiscardPlaybackBusy() || screen.getSession().isPending()) return;
        var bloom = snapshot.getSleeveBloom();
        check(bloom != null, "Default product config must enable bloom like tile mark");
        var panel = screen.getSleeveBloomPanel();
        if (activated) {
            var offer = bloom.getPendingOffer();
            if (!chosen) {
                if (offer == null) return;
                check(panel.getModel().isChoosing(), "Private offer did not open real panel");
                check(snapshot.getCheatPoints().get(snapshot.getViewerSeat()) == beforePoints - 10, "Incorrect fee");
                check(offer.getDeadlineMillis() == originalDeadline, "Activation gifted time");
                if (offeredFrame < 0) {
                    offeredFrame = frames;
                    expectedTile = (completed == 0 ? offer.getSecondTile() : offer.getFirstTile()).getTileId();
                    System.out.println("SLEEVE_BLOOM_OFFER " + completed + " first=" + offer.getFirstTile() + " second=" + offer.getSecondTile());
                    click(screen, completed == 0 ? panel.getSecondCardRect() : panel.getFirstCardRect());
                    check(panel.getModel().getSelected() == (completed == 0 ? SleeveBloomKeep.SECOND : SleeveBloomKeep.FIRST), "Card selection failed");
                    return;
                }
                if (frames < offeredFrame + 3) return;
                capture("offer-" + completed);
                checkDisabledFill(screen.getTileMarkPanel().getBtnOpenRect(), "Tile mark must look disabled during bloom choice");
                click(screen, completed == 0 ? panel.getBtnConfirmRect() : panel.getBtnCancelRect());
                click(screen, completed == 0 ? panel.getBtnConfirmRect() : panel.getBtnCancelRect());
                chosen = true;
                return;
            }
            if (offer != null) return;
            check(snapshot.getView().getMyDrawnTile() != null && snapshot.getView().getMyDrawnTile().getTileId() == expectedTile, "Kept tile mismatch");
            check(snapshot.getCheatPoints().get(snapshot.getViewerSeat()) == beforePoints - 10, "Choice charged twice");
            check(snapshot.getClocks().stream().filter(c -> c.getSeat() == snapshot.getViewerSeat()).findFirst().orElseThrow().getDeadline() == originalDeadline,
                "Choice gifted time");
            // Snapshot callbacks may run between drawing and this probe. Capture after
            // two further frames so the screenshot represents the retained tile.
            if (keptFrame < 0) { keptFrame = frames; return; }
            if (frames < keptFrame + 2) return;
            var drawnSlot = screen.refreshHandTileRects().stream().filter(s -> s.isDrawn()).findFirst().orElseThrow();
            check(drawnSlot.getTile().equals(snapshot.getView().getMyDrawnTile()), "Rendered drawn slot does not match authority");
            System.out.println("SLEEVE_BLOOM_KEPT " + completed + " drawn=" + drawnSlot.getTile());
            capture("kept-" + completed);
            completed++;
            activated = chosen = false;
            offeredFrame = -1;
            keptFrame = -1;
            if (completed == 2) { screen.keyDown(Input.Keys.ESCAPE); leaving = true; return; }
        }
        if (bloom.getCanApply()) {
            if (disconnectProbe) {
                beforePoints = snapshot.getCheatPoints().get(snapshot.getViewerSeat());
                screen.getSession().close();
                disconnectFrame = frames;
                return;
            }
            if (!checkedMarkModal) {
                if (markFrame < 0) {
                    click(screen, screen.getTileMarkPanel().getBtnOpenRect());
                    check(screen.getTileMarkPanel().getModel().isSelecting(), "Tile mark modal did not open");
                    markFrame = frames;
                    return;
                }
                if (frames < markFrame + 2) return;
                capture("other-modal");
                checkDisabledFill(panel.getBtnOpenRect(), "Bloom must look disabled during tile mark selection");
                screen.keyDown(Input.Keys.ESCAPE);
                checkedMarkModal = true;
                return;
            }
            beforePoints = snapshot.getCheatPoints().get(snapshot.getViewerSeat());
            originalDeadline = snapshot.getClocks().stream().filter(c -> c.getSeat() == snapshot.getViewerSeat()).findFirst().orElseThrow().getDeadline();
            click(screen, panel.getBtnOpenRect());
            click(screen, panel.getBtnOpenRect());
            activated = true;
            return;
        }
        // Reach the first ordinary human draw through the actual production round.
        var decision = snapshot.getDecision();
        var pass = decision.getOptions().stream().filter(o -> o.getAction() instanceof LegalActionView.Pass).findFirst();
        if (pass.isPresent()) {
            screen.getSession().submit(new ActionChoice(UUID.randomUUID().toString(), snapshot.getRoundId(), decision.getDecisionId(),
                decision.getControlEpoch(), pass.get().getOptionId()));
            return;
        }
        var discard = decision.getOptions().stream().filter(o -> o.getAction() instanceof LegalActionView.DiscardTile).findFirst();
        if (discard.isPresent()) {
            int tileId = ((LegalActionView.DiscardTile)discard.get().getAction()).getTile().getTileId();
            var slot = screen.refreshHandTileRects().stream().filter(s -> s.getTile().getTileId() == tileId).findFirst().orElseThrow();
            click(screen, slot.getRect());
        }
    }
}
