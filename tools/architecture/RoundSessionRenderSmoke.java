import com.badlogic.gdx.*;
import com.badlogic.gdx.backends.lwjgl3.*;
import com.badlogic.gdx.graphics.*;
import com.badlogic.gdx.utils.ScreenUtils;
import com.majiang.MahjongGame;
import com.majiang.render.*;
import com.majiang.screen.GameScreen;

/** Actual GL acceptance of the default client path, with no fake session. */
public final class RoundSessionRenderSmoke extends ApplicationAdapter {
    private MahjongGame game;
    private GameScreen screen;
    private int frames;
    private boolean clicked;

    public static void main(String[] args) {
        Lwjgl3ApplicationConfiguration config = new Lwjgl3ApplicationConfiguration();
        config.setWindowedMode(1280, 720);
        config.setTitle("Round Session Client acceptance");
        config.setInitialVisible(false);
        config.disableAudio(true);
        config.setForegroundFPS(30);
        new Lwjgl3Application(new RoundSessionRenderSmoke(), config);
    }

    @Override public void create() {
        game = new MahjongGame(new SkinManager(new InMemorySkinPreferences(SkinId.WIKIPEDIA)));
        game.create();
        screen = new GameScreen(game, (TileSkin) null);
        game.setScreen(screen);
        screen.startGame();
        if (screen.getSession().view().getMyAllClosedTiles().size() != 14)
            throw new AssertionError("Default endpoint did not deal the dealer hand");
    }

    private void capture(String name) {
        Pixmap pixmap = ScreenUtils.getFrameBufferPixmap(0, 0, 1280, 720);
        try {
            PixmapIO.writePNG(Gdx.files.absolute(System.getProperty("smoke.output") + "/" + name + ".png"), pixmap, -1, true);
        } finally { pixmap.dispose(); }
    }

    @Override public void render() {
        game.render();
        frames++;
        if (!clicked && frames > 30 && !screen.isDiscardPlaybackBusy()) {
            capture("default-client-dealt");
            GameScreen.HandTileSlot slot = screen.refreshHandTileRects().get(0);
            screen.touchDown((int) (slot.getRect().x + slot.getRect().width / 2), 640, 0, 0);
            if (screen.getSession().view().getMyDiscardHistory().isEmpty())
                throw new AssertionError("Default client input did not commit a discard");
            clicked = true;
        }
        if (clicked && frames > 120 && !screen.isDiscardPlaybackBusy()) {
            int opponentDiscards = screen.getEndpoint().view().getDiscards().subList(1, 4)
                .stream().mapToInt(java.util.List::size).sum();
            if (opponentDiscards == 0)
                throw new AssertionError("Default local host did not schedule an AI turn after player discard");
            capture("default-client-after-discard");
            System.out.println("ROUND_SESSION_CLIENT_RENDER_PASS endpoint=" + screen.getEndpoint().getClass().getName());
            Gdx.app.exit();
        }
        if (frames > 450) throw new AssertionError("Default client remained blocked");
    }

    @Override public void resize(int width, int height) { if (game != null) game.resize(width, height); }
    @Override public void dispose() { if (game != null) game.dispose(); }
}
