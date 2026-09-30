import com.badlogic.gdx.ApplicationAdapter;
import com.badlogic.gdx.Gdx;
import com.badlogic.gdx.backends.lwjgl3.Lwjgl3Application;
import com.badlogic.gdx.backends.lwjgl3.Lwjgl3ApplicationConfiguration;
import com.badlogic.gdx.graphics.Pixmap;
import com.badlogic.gdx.graphics.PixmapIO;
import com.badlogic.gdx.utils.ScreenUtils;
import com.majiang.MahjongGame;
import com.majiang.screen.GameScreen;
import com.majiang.session.LocalGameSession;
import com.majiang.state.ActionResult;
import com.majiang.state.GameAction;

/** Codex-owned real OpenGL smoke; hidden window, no production source changes. */
public final class DesktopRenderSmoke extends ApplicationAdapter {
    private MahjongGame game;
    private LocalGameSession session;
    private int frames;
    private int commands;

    public static void main(String[] args) {
        Lwjgl3ApplicationConfiguration config = new Lwjgl3ApplicationConfiguration();
        config.setTitle("Architecture V2 render smoke");
        config.setWindowedMode(1280, 720);
        config.setInitialVisible(false);
        config.disableAudio(true);
        config.setForegroundFPS(30);
        new Lwjgl3Application(new DesktopRenderSmoke(), config);
    }

    @Override public void create() {
        game = new MahjongGame();
        game.create();
        session = new LocalGameSession();
        game.launchGame(session);
        ((GameScreen) game.getScreen()).startGame();
    }

    @Override public void render() {
        game.render();
        frames++;
        if (frames == 3) capture("desktop-initial.png");
        if (frames % 3 == 0 && !session.legalActions().isEmpty()) {
            GameAction chosen = session.legalActions().get(0);
            ActionResult result = session.submit(chosen);
            if (!(result instanceof ActionResult.Accepted)) {
                throw new AssertionError("UI session rejected its own legal action: " + chosen + " -> " + result);
            }
            commands++;
        }
        if (frames >= 60) {
            capture("desktop-after-play.png");
            if (commands == 0) throw new AssertionError("No playable command in desktop smoke");
            System.out.println("RENDER_SMOKE_PASS frames=" + frames + " commands=" + commands + " phase=" + session.view().getPhase());
            Gdx.app.exit();
        }
    }

    private void capture(String name) {
        Pixmap pixels = ScreenUtils.getFrameBufferPixmap(0, 0, 1280, 720);
        try {
            PixmapIO.writePNG(Gdx.files.absolute(System.getProperty("smoke.output") + "/" + name), pixels, -1, true);
        } finally {
            pixels.dispose();
        }
    }

    @Override public void resize(int width, int height) { if (game != null) game.resize(width, height); }
    @Override public void dispose() { if (game != null) game.dispose(); }
}
