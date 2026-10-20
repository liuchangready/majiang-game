import com.badlogic.gdx.ApplicationAdapter;
import com.badlogic.gdx.Gdx;
import com.badlogic.gdx.Input;
import com.badlogic.gdx.graphics.Pixmap;
import com.badlogic.gdx.graphics.PixmapIO;
import com.badlogic.gdx.math.Rectangle;
import com.badlogic.gdx.utils.ScreenUtils;
import com.majiang.MahjongGame;
import com.majiang.client.api.GameSnapshotView;
import com.majiang.client.api.LegalActionView;
import com.majiang.client.api.ShuffleTarget;
import com.majiang.client.api.TileView;
import com.majiang.presentation.game.GameScreen;
import com.majiang.presentation.game.ShufflePreparationPanel;
import com.majiang.presentation.menu.MenuScreen;
import java.io.File;

/** Shared real-GL acceptance path through V4 menu, lobby, game session, and table. */
abstract class V4LocalRenderSmoke extends ApplicationAdapter {
    private final String outputPrefix;
    private final String passMarker;
    private final boolean requireShuffleFee;
    protected MahjongGame game;
    protected GameScreen screen;
    private int frames;
    private int clickFrame = -1;
    private int clickedTileId = -1;
    private int commands;
    private boolean menuClicked;
    private boolean shuffleSelected;
    private boolean shuffleConfirmed;
    private boolean leaving;

    V4LocalRenderSmoke(String outputPrefix, String passMarker, boolean requireShuffleFee) {
        this.outputPrefix = outputPrefix;
        this.passMarker = passMarker;
        this.requireShuffleFee = requireShuffleFee;
    }

    @Override public void create() {
        game = new MahjongGame();
        game.create();
        if (!(game.getScreen() instanceof MenuScreen))
            throw new AssertionError("V4 product entry did not open the menu");
    }

    private void capture(String suffix) {
        File directory = new File(System.getProperty("smoke.output"));
        if (!directory.isDirectory() && !directory.mkdirs())
            throw new AssertionError("Cannot create screenshot directory: " + directory);
        Pixmap pixels = ScreenUtils.getFrameBufferPixmap(0, 0, 1280, 720);
        try {
            PixmapIO.writePNG(Gdx.files.absolute(new File(directory, outputPrefix + "-" + suffix + ".png").getAbsolutePath()), pixels, -1, true);
        } finally {
            pixels.dispose();
        }
    }

    private static void click(GameScreen target, Rectangle rect) {
        target.touchDown((int)(rect.x + rect.width / 2f),
            720 - (int)(rect.y + rect.height / 2f), 0, 0);
    }

    @Override public void render() {
        game.render();
        frames++;
        if (frames == 3) {
            capture("menu");
            ((MenuScreen)game.getScreen()).touchDown(640, 275, 0, 0);
            menuClicked = true;
            commands++;
        }
        if (!(game.getScreen() instanceof GameScreen)) {
            if (leaving && game.getScreen() instanceof MenuScreen) {
                if (game.getCurrentSession() != null || game.getInProcessServer() != null)
                    throw new AssertionError("Returning to menu leaked the V4 session or local server");
                System.out.println(passMarker + " frames=" + frames + " commands=" + commands);
                Gdx.app.exit();
                return;
            }
            if (frames > 900) throw new AssertionError("V4 menu/lobby did not enter the player table");
            return;
        }
        screen = (GameScreen)game.getScreen();
        if (game.getCurrentSession() == null || game.getInProcessServer() == null)
            throw new AssertionError("V4 singleplayer table has no lobby session or local server");
        if (frames == 10) capture("table");

        ShufflePreparationPanel panel = screen.getShufflePreparationPanel();
        if (!leaving && panel.isActive()) {
            if (!shuffleSelected) {
                capture("shuffle-selection");
                Rectangle target = panel.getBtnTargets().get(ShuffleTarget.KOKUSHI);
                if (target == null) throw new AssertionError("KOKUSHI selection unavailable");
                click(screen, target);
                click(screen, panel.getBtnConfirmRect());
                shuffleSelected = true;
                commands += 2;
            } else if (panel.getModel().isAuthorityConfirmed() && !shuffleConfirmed) {
                if (panel.getModel().getRemainingPointsPreview() != 70)
                    throw new AssertionError("100-point default must reserve the 30-point unified shuffle fee exactly once");
                capture("shuffle-confirmed");
                shuffleConfirmed = true;
            }
            return;
        }

        GameSnapshotView snapshot = screen.getCurrentSnapshot();
        if (snapshot == null || screen.isDiscardPlaybackBusy()) return;
        if (clickFrame < 0) {
            if (snapshot.getPreparation() != null || snapshot.getView().getMyAllClosedTiles().size() != 14) {
                if (frames > 900) throw new AssertionError("V4 initial dealer hand was not dealt");
                return;
            }
            if (requireShuffleFee) {
                if (!shuffleSelected || !shuffleConfirmed)
                    throw new AssertionError("Shuffle panel flow was bypassed");
                if (snapshot.getCheatPoints().get(0) != 70)
                    throw new AssertionError("Shuffle fee was not applied to authority snapshot");
            }
            for (GameScreen.HandTileSlot slot : screen.refreshHandTileRects()) {
                TileView tile = slot.getTile();
                boolean legal = snapshot.getDecision().getOptions().stream().anyMatch(option ->
                    option.getAction() instanceof LegalActionView.DiscardTile discard &&
                    discard.getTile().getTileId() == tile.getTileId());
                if (!legal) continue;
                capture("dealt");
                clickedTileId = tile.getTileId();
                click(screen, slot.getRect());
                clickFrame = frames;
                commands++;
                break;
            }
            return;
        }
        if (!leaving && frames > clickFrame + 30) {
            var view = snapshot.getView();
            if (view.getMyDiscardHistory().isEmpty()) return;
            int actualId = view.getMyDiscardHistory().get(view.getMyDiscardHistory().size() - 1).getTileId();
            if (actualId != clickedTileId)
                throw new AssertionError("Discard physical ID mismatch: expected " + clickedTileId + " got " + actualId);
            int opponents = 0;
            for (int seat = 1; seat < 4; seat++) opponents += view.getDiscards().get(seat).size();
            if (opponents == 0) return;
            capture("after-discard");
            screen.keyDown(Input.Keys.ESCAPE);
            leaving = true;
        }
        if (frames > 1100) throw new AssertionError("V4 local render smoke remained blocked");
    }

    @Override public void resize(int width, int height) { if (game != null) game.resize(width, height); }
    @Override public void dispose() { if (game != null) game.dispose(); }
}
