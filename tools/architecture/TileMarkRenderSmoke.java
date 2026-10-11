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
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;

/** Codex: actual GL, real product menu/session/authority, actual pointer input. */
public final class TileMarkRenderSmoke extends ApplicationAdapter {
    private MahjongGame game;
    private int frames, batches, selectionFrame = -1;
    private boolean skipped, awaitingGroup, discarded, leaving;
    private int discardedId = -1;
    private long originalDeadline = -1;
    private final List<Integer> marked = new ArrayList<>();

    public static void main(String[] args) {
        var config = new Lwjgl3ApplicationConfiguration();
        config.setTitle("Tile mark acceptance");
        config.setWindowedMode(1280, 720);
        config.setInitialVisible(false);
        config.disableAudio(true);
        config.setForegroundFPS(30);
        new Lwjgl3Application(new TileMarkRenderSmoke(), config);
    }

    @Override public void create() { game = new MahjongGame(); game.create(); }
    @Override public void resize(int w, int h) { if (game != null) game.resize(w, h); }
    @Override public void dispose() { if (game != null) game.dispose(); }
    private void check(boolean condition, String text) { if (!condition) throw new AssertionError(text); }
    private void click(GameScreen screen, Rectangle rect) {
        screen.touchDown((int)(rect.x + rect.width / 2), 720 - (int)(rect.y + rect.height / 2), 0, 0);
    }
    private void capture(String name) {
        File directory = new File(System.getProperty("smoke.output"));
        check(directory.isDirectory() || directory.mkdirs(), "Cannot create screenshot directory");
        Pixmap pixels = ScreenUtils.getFrameBufferPixmap(0, 0, 1280, 720);
        try { PixmapIO.writePNG(Gdx.files.absolute(new File(directory, name + ".png").getAbsolutePath()), pixels, -1, true); }
        finally { pixels.dispose(); }
    }

    @Override public void render() {
        game.render();
        frames++;
        check(frames < 2100, "Tile mark GL smoke timed out");
        if (frames == 3) {
            check(game.getScreen() instanceof MenuScreen, "Product entry must open menu");
            ((MenuScreen)game.getScreen()).touchDown(640, 471, 0, 0);
        }
        if (frames == 5) {
            capture("technique-rules");
            ((MenuScreen)game.getScreen()).touchDown(640, 564, 0, 0);
        }
        if (frames == 7) {
            check(game.getScreen() instanceof MenuScreen, "Product entry must open menu");
            ((MenuScreen)game.getScreen()).touchDown(640, 275, 0, 0);
        }
        if (!(game.getScreen() instanceof GameScreen screen)) {
            if (leaving && game.getScreen() instanceof MenuScreen) {
                check(game.getCurrentSession() == null && game.getInProcessServer() == null, "Menu return leaked session/server");
                System.out.println("TILE_MARK_GL_PASS frames=" + frames + " groups=3 fee=30 riverIndex=0");
                Gdx.app.exit();
            }
            return;
        }
        if (leaving) return;
        if (screen.getShufflePreparationPanel().isActive()) {
            if (!skipped) { click(screen, screen.getShufflePreparationPanel().getBtnSkipRect()); skipped = true; }
            return;
        }
        var snapshot = screen.getCurrentSnapshot();
        if (snapshot == null || snapshot.getTileMark() == null || screen.isDiscardPlaybackBusy()) return;
        var mark = snapshot.getTileMark();
        var panel = screen.getTileMarkPanel();
        if (batches < 3) {
            if (awaitingGroup) {
                if (mark.getActiveGroups().size() != batches + 1) return;
                batches++;
                check(mark.getActiveTileCount() == batches * 4, "Mark count mismatch");
                check(snapshot.getCheatPoints().get(snapshot.getViewerSeat()) == 100 - batches * 10, "Fee or duplicate submission mismatch");
                long deadline = snapshot.getClocks().stream().filter(c -> c.getSeat() == snapshot.getViewerSeat()).findFirst().orElseThrow().getDeadline();
                check(deadline == originalDeadline, "Marking granted extra decision time");
                awaitingGroup = false;
                selectionFrame = -1;
                return;
            }
            if (screen.getSession().isPending()) return;
            if (selectionFrame < 0) {
                check(mark.getCanApply(), "Default product room must enable tile marking");
                if (originalDeadline < 0) originalDeadline = snapshot.getClocks().stream()
                    .filter(c -> c.getSeat() == snapshot.getViewerSeat()).findFirst().orElseThrow().getDeadline();
                click(screen, panel.getBtnOpenRect());
                check(panel.getModel().isSelecting(), "Real mark button did not open selection");
                int count = 0;
                for (var slot : screen.refreshHandTileRects()) {
                    int id = slot.getTile().getTileId();
                    if (!mark.getSelectableTileIds().contains(id)) continue;
                    click(screen, slot.getRect());
                    marked.add(id);
                    if (++count == 4) break;
                }
                check(panel.getModel().getSelectedTileIds().size() == 4, "Actual hand clicks did not select four IDs");
                check(snapshot.getView().getDiscards().get(snapshot.getViewerSeat()).isEmpty(), "Selection leaked ordinary discard");
                selectionFrame = frames;
            } else if (frames > selectionFrame + 2) {
                capture("selection-" + batches);
                click(screen, panel.getBtnConfirmRect());
                click(screen, panel.getBtnConfirmRect());
                awaitingGroup = true;
            }
            return;
        }
        check(new HashSet<>(marked).size() == 12, "Marked physical IDs overlap");
        if (!discarded) {
            click(screen, panel.getBtnOpenRect());
            check(!panel.getModel().isSelecting(), "Full quota must not open another selection");
            capture("twelve-active");
            discardedId = marked.get(0);
            var slot = screen.refreshHandTileRects().stream().filter(s -> s.getTile().getTileId() == discardedId).findFirst().orElseThrow();
            click(screen, slot.getRect());
            discarded = true;
            return;
        }
        var tile = mark.getActiveGroups().stream().flatMap(g -> g.getTiles().stream())
            .filter(t -> t.getTileId() == discardedId).findFirst().orElseThrow();
        if (tile.getArea() != TileMarkArea.DISCARD) return;
        var position = tile.getRiverPosition();
        check(position != null && position.getRoundId().equals(snapshot.getRoundId()) && position.getSeat() == snapshot.getViewerSeat()
            && position.getDiscardIndex() == 0, "Authoritative river position mismatch");
        check(snapshot.getView().getDiscards().get(position.getSeat()).get(0).getTileId() == discardedId, "River face mismatch");
        capture("marked-river");
        screen.keyDown(Input.Keys.ESCAPE);
        leaving = true;
    }
}
