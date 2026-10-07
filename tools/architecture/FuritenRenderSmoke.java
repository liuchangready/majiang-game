import com.badlogic.gdx.ApplicationAdapter;
import com.badlogic.gdx.Gdx;
import com.badlogic.gdx.backends.lwjgl3.Lwjgl3Application;
import com.badlogic.gdx.backends.lwjgl3.Lwjgl3ApplicationConfiguration;
import com.badlogic.gdx.graphics.Pixmap;
import com.badlogic.gdx.graphics.PixmapIO;
import com.badlogic.gdx.utils.ScreenUtils;
import com.majiang.MahjongGame;
import com.majiang.client.api.*;
import com.majiang.presentation.game.GameScreen;
import java.io.File;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.List;

/** Real GL check of riichi wait previews, furiten labels, and tsumo control. */
public final class FuritenRenderSmoke extends ApplicationAdapter {
    private MahjongGame game;
    private V4SmokeSession session;
    private GameScreen screen;
    private int frame;
    private static TileView t(SuitView suit, int rank, int copy) { return V4SmokeViews.tile(suit, rank, copy); }
    private static TileView t(SuitView suit, int rank) { return t(suit, rank, 0); }

    public static void main(String[] args) {
        Lwjgl3ApplicationConfiguration c = new Lwjgl3ApplicationConfiguration();
        c.setTitle("V4 furiten acceptance"); c.setWindowedMode(1280, 720);
        c.setInitialVisible(false); c.disableAudio(true); c.setForegroundFPS(30);
        new Lwjgl3Application(new FuritenRenderSmoke(), c);
    }

    @Override public void create() {
        game = new MahjongGame(); game.create();
        session = new V4SmokeSession();
        publishRegular();
        game.changeScreen(new GameScreen(game.getUiAssets(), session, game, null));
        screen = (GameScreen)game.getScreen();
    }

    private void publishRegular() {
        List<TileView> closed = new ArrayList<>();
        for (int n = 1; n <= 6; n++) closed.add(t(SuitView.MAN, n));
        for (int n = 7; n <= 9; n++) closed.add(t(SuitView.PIN, n));
        for (int n = 2; n <= 5; n++) closed.add(t(SuitView.SOU, n));
        TileView drawn = t(SuitView.SOU, 6);
        var riichi2 = new LegalActionView.DeclareRiichi(2, t(SuitView.SOU, 2));
        var riichi6 = new LegalActionView.DeclareRiichi(2, drawn);
        var discard = new LegalActionView.DiscardTile(2, drawn);
        var options = Arrays.asList(new ActionOptionView("riichi-2s", riichi2),
            new ActionOptionView("riichi-6s", riichi6), new ActionOptionView("discard", discard));
        var preview = new PlayerPresentationPreviewView("smoke-round-0", "regular-decision", 0L,
            Collections.emptyList(), Arrays.asList(
                new RiichiPreviewOptionView("riichi-2s", riichi2,
                    Arrays.asList(t(SuitView.SOU, 3), t(SuitView.SOU, 6)), new FuritenStatusView(false, false, false)),
                new RiichiPreviewOptionView("riichi-6s", riichi6,
                    Arrays.asList(t(SuitView.SOU, 2), t(SuitView.SOU, 5)), new FuritenStatusView(true, false, false))),
            Collections.emptyList());
        session.publish(V4SmokeViews.player(2).closed(closed).drawn(drawn)
            .actions(Arrays.asList(riichi2, riichi6, discard))
            .history(Collections.singletonList(t(SuitView.SOU, 5, 1))).build(), options, preview);
    }

    private void publishOrphans(boolean winning) {
        List<TileView> closed = new ArrayList<>();
        for (SuitView suit : Arrays.asList(SuitView.MAN, SuitView.SOU, SuitView.PIN)) {
            closed.add(t(suit, 1)); closed.add(t(suit, 9));
        }
        for (int n = 1; n <= 4; n++) closed.add(t(SuitView.WIND, n));
        for (int n = 1; n <= 3; n++) closed.add(t(SuitView.DRAGON, n));
        TileView drawn = winning ? t(SuitView.WIND, 1, 1) : t(SuitView.MAN, 2);
        var status = new FuritenStatusView(true, true, true);
        List<ActionOptionView> options;
        PlayerPresentationPreviewView preview;
        if (winning) {
            var tsumo = new LegalActionView.DeclareTsumo(2);
            options = Arrays.asList(new ActionOptionView("tsumo", tsumo),
                new ActionOptionView("discard", new LegalActionView.DiscardTile(2, drawn)));
            preview = new PlayerPresentationPreviewView("smoke-round-0", "orphan-winning", 0L,
                Collections.emptyList(), Collections.emptyList(), Collections.emptyList());
        } else {
            var riichi = new LegalActionView.DeclareRiichi(2, drawn);
            options = Collections.singletonList(new ActionOptionView("orphan-riichi", riichi));
            preview = new PlayerPresentationPreviewView("smoke-round-0", "orphan-decision", 0L,
                Collections.emptyList(), Collections.singletonList(new RiichiPreviewOptionView(
                    "orphan-riichi", riichi, closed, status)), Collections.emptyList());
        }
        List<LegalActionView> actions = new ArrayList<>();
        for (ActionOptionView option : options) actions.add(option.getAction());
        session.publish(V4SmokeViews.player(2).closed(closed).drawn(drawn)
            .actions(actions).furiten(status)
            .history(Collections.singletonList(t(SuitView.WIND, 2, 1))).build(), options, preview);
    }

    private void click(int x, int y) {
        if (!screen.touchDown(x, 720-y, 0, 0)) throw new AssertionError("Unhandled click: " + x + "," + y);
    }
    @Override public void render() {
        game.render(); frame++;
        if (frame == 3) click(720, 160);
        if (frame == 6) click(767, 80);
        if (frame == 9) capture("discard-2s-wait-3s-6s.png");
        if (frame == 10) click(1000, 80);
        if (frame == 13) {
            capture("discard-6s-wait-2s-5s.png");
            if (!session.submitted.isEmpty()) throw new AssertionError("Preview submitted a game action");
            Gdx.input.setCursorPosition(767, 640);
        }
        if (frame == 15) {
            capture("hover-2s-selected-6s.png");
            click(720, 160);
            Gdx.input.setCursorPosition(10, 10);
        }
        if (frame == 16 && !session.submittedActions.equals(Collections.singletonList(
            new LegalActionView.DeclareRiichi(2, t(SuitView.SOU, 6)))))
            throw new AssertionError("Wrong confirmed action: " + session.submittedActions);
        if (frame == 18) publishOrphans(false);
        if (frame == 20) click(720, 160);
        if (frame == 22) click(1000, 80);
        if (frame == 25) capture("orphans-thirteen-waits.png");
        if (frame == 27) click(1100, 160);
        if (frame == 30) {
            if (session.submitted.size() != 1) throw new AssertionError("Cancel submitted an action");
            publishOrphans(true);
        }
        if (frame == 33) {
            if (!screen.canHumanTsumo() || screen.canHumanRon()) throw new AssertionError("Wrong furiten win buttons");
            capture("furiten-tsumo-choice.png");
            click(900, 160);
        }
        if (frame == 36) {
            if (session.submittedActions.size() != 2 ||
                !(session.submittedActions.get(1) instanceof LegalActionView.DeclareTsumo))
                throw new AssertionError("Tsumo was not preserved");
            System.out.println("FURITEN_RENDER_PASS frames=" + frame + " confirmations=1 tsumo=1 screenshots=5");
            Gdx.app.exit();
        }
    }
    private void capture(String name) {
        File dir = new File(System.getProperty("smoke.output"));
        if (!dir.isDirectory() && !dir.mkdirs()) throw new AssertionError("Cannot create screenshot directory");
        Pixmap image = ScreenUtils.getFrameBufferPixmap(0,0,1280,720);
        try { PixmapIO.writePNG(Gdx.files.absolute(new File(dir,name).getAbsolutePath()),image,-1,true); }
        finally { image.dispose(); }
    }
    @Override public void resize(int w,int h) { if (game != null) game.resize(w,h); }
    @Override public void dispose() { if (game != null) game.dispose(); }
}
