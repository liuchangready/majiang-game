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
import com.majiang.presentation.game.BeamSwapScreenContractTest;
import com.majiang.presentation.testing.FakeGameSession;
import com.majiang.presentation.testing.FakeTableNavigation;
import java.io.File;
import java.util.concurrent.CompletableFuture;

/** Real OpenGL maximum-layout/input probe with an explicit client snapshot fixture.
 * Authority/network end-to-end validation lives in BeamSwapRenderSmoke and module tests. */
public final class BeamSwapLayoutSmoke extends ApplicationAdapter {
    private MahjongGame game;
    private GameScreen screen;
    private FakeGameSession fake;
    private BeamSwapScreenContractTest.BeamSession session;
    private int frames;
    public static void main(String[] args) {
        var config = new Lwjgl3ApplicationConfiguration();
        config.setTitle("Beam swap maximum layout acceptance");
        int width = Integer.getInteger("smoke.width", 1280);
        config.setWindowedMode(width, width * 720 / 1280);
        config.setInitialVisible(false);
        config.disableAudio(true);
        config.setForegroundFPS(30);
        new Lwjgl3Application(new BeamSwapLayoutSmoke(), config);
    }
    private void check(boolean value, String text) { if (!value) throw new AssertionError(text); }
    private void click(Rectangle rect) {
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
    @Override public void create() {
        game = new MahjongGame();
        game.create();
        var fixture = new BeamSwapScreenContractTest().snapshot(true);
        fake = new FakeGameSession(fixture, null);
        session = new BeamSwapScreenContractTest.BeamSession(fake);
        screen = new GameScreen(game.getUiAssets(), session, new FakeTableNavigation(), null);
        game.changeScreen(screen);
        screen.startGame();
    }
    @Override public void resize(int w, int h) { if (game != null) game.resize(w, h); }
    @Override public void dispose() { if (game != null) game.dispose(); }
    @Override public void render() {
        game.render();
        var panel = screen.getBeamSwapPanel();
        frames++;
        check(frames < 150, "Maximum layout smoke timed out");
        if (frames == 3) {
            check(panel.getModel().isChoosing(), "Missing modal");
            check(panel.getTargetCardRects().size() == 13, "Expected 13 candidates");
            check(panel.getHandCardRects().size() == 13, "Expected 13 original concealed tiles");
            check(!panel.getHandCardRects().containsKey(13), "Drawn tile is selectable");
            capture("maximum-unselected");
            click(screen.refreshHandTileRects().get(0).getRect());
            click(screen.getBtnSortRect());
            check(fake.getSubmittedChoices().isEmpty(), "Table input leaked through modal");
            check(!screen.isAutoSort(), "Sort input leaked through modal");
            var target = panel.getTargetCardRects().entrySet().stream().reduce((a,b) -> b).orElseThrow();
            click(target.getValue());
            check(target.getKey().equals(panel.getModel().getSelectedTargetSlot()), "Last target cannot be selected");
            click(panel.getHandCardRects().get(12));
            check(panel.getModel().getCanConfirm(), "Last hand tile cannot complete double selection");
        }
        if (frames == 6) {
            capture("maximum-selected");
            fake.setSubmitResult(new CompletableFuture<>());
            click(panel.getBtnConfirmRect());
            click(panel.getBtnConfirmRect());
            screen.keyDown(Input.Keys.ESCAPE);
            check(session.getChoices().size() == 1, "Duplicate/in-flight submission");
            check(session.getChoices().get(0).getAction() == BeamSwapAction.SWAP, "Wrong request branch");
            check(session.getChoices().get(0).getHandTileId() == 12, "Wrong hand tile");
            System.out.println("BEAM_SWAP_LAYOUT_GL_PASS candidates=13 hand=13 marked=1 frames=" + frames);
            Gdx.app.exit();
        }
    }
}
