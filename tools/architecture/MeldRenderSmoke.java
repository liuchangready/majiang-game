import com.badlogic.gdx.ApplicationAdapter;
import com.badlogic.gdx.Gdx;
import com.badlogic.gdx.backends.lwjgl3.Lwjgl3Application;
import com.badlogic.gdx.backends.lwjgl3.Lwjgl3ApplicationConfiguration;
import com.badlogic.gdx.graphics.Pixmap;
import com.badlogic.gdx.graphics.PixmapIO;
import com.badlogic.gdx.utils.ScreenUtils;
import com.majiang.MahjongGame;
import com.majiang.client.api.*;
import com.majiang.presentation.common.render.InMemorySkinPreferences;
import com.majiang.presentation.common.render.SkinId;
import com.majiang.presentation.common.render.SkinManager;
import com.majiang.presentation.common.render.MeldDisplayLayout;
import com.majiang.presentation.game.GameScreen;
import java.io.File;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

/** Display-capacity fixture: sixteen public melds intentionally exceed a legal round. */
public final class MeldRenderSmoke extends ApplicationAdapter {
    private MahjongGame game;
    private GameScreen screen;
    private int frames;
    private int scenario;

    public static void main(String[] args) {
        var config = new Lwjgl3ApplicationConfiguration();
        config.setWindowedMode(1280, 720); config.setTitle("V4 opponent meld acceptance");
        config.setInitialVisible(false); config.disableAudio(true); config.setForegroundFPS(30);
        new Lwjgl3Application(new MeldRenderSmoke(), config);
    }

    private static MeldView meld(int seat, int group) {
        MeldTypeView type = new MeldTypeView[]{MeldTypeView.CHI, MeldTypeView.PON,
            MeldTypeView.OPEN_KAN, seat == 2 ? MeldTypeView.ADDED_KAN : MeldTypeView.CLOSED_KAN}[group];
        SuitView suit = new SuitView[]{SuitView.MAN, SuitView.PIN, SuitView.SOU, SuitView.MAN}[seat];
        if (seat == 3 && group > 0) suit = group == 3 ? SuitView.DRAGON : SuitView.WIND;
        int rank = group == 0 ? (seat == 3 ? 1 : 1) : seat == 3 ? (group == 3 ? 1 : group) : 5 + group;
        List<TileView> tiles = new ArrayList<>();
        int count = group < 2 ? 3 : 4;
        for (int i = 0; i < count; i++) {
            tiles.add(V4SmokeViews.tile(suit, group == 0 ? rank + i : rank, group == 0 ? 0 : i));
        }
        return new MeldView(type, tiles, type == MeldTypeView.CLOSED_KAN ? -1 : (seat + 3) % 4, tiles.get(0));
    }

    private void install(int viewer, SkinId skin) {
        game.getSkinManager().setSkin(skin);
        var builder = V4SmokeViews.player(viewer)
            .closed(Collections.singletonList(V4SmokeViews.tile(SuitView.WIND, 4, viewer)))
            .drawn(V4SmokeViews.tile(SuitView.DRAGON, 3, 3));
        for (int seat = 0; seat < 4; seat++) {
            for (int group = 0; group < 4; group++) builder.meld(seat, meld(seat, group));
            for (int i = 0; i < 20; i++)
                builder.discard(seat, V4SmokeViews.tile(SuitView.SOU, i % 9 + 1, i / 9));
        }
        var session = new V4SmokeSession();
        session.publish(builder.build());
        screen = new GameScreen(game.getUiAssets(), session, game, null);
        game.changeScreen(screen);
    }

    @Override public void create() {
        game = new MahjongGame(new SkinManager(new InMemorySkinPreferences(SkinId.WIKIPEDIA)));
        game.create(); install(0, SkinId.WIKIPEDIA);
    }

    @Override public void render() {
        game.render();
        if (++frames % 12 != 0) return;
        if (MeldDisplayLayout.INSTANCE.layoutAll(screen.getCurrentView()).size() != 16)
            throw new AssertionError("Missing public meld groups");
        capture("opponent-melds-" + scenario + ".png");
        scenario++;
        if (scenario == 1) install(2, SkinId.WIKIPEDIA);
        else if (scenario == 2) install(0, SkinId.CLASSIC);
        else {
            System.out.println("OPPONENT_MELDS_RENDER_PASS");
            Gdx.app.exit();
        }
    }

    private void capture(String name) {
        File dir = new File(System.getProperty("smoke.output"));
        if (!dir.isDirectory() && !dir.mkdirs()) throw new AssertionError("Cannot create screenshot directory");
        Pixmap p = ScreenUtils.getFrameBufferPixmap(0,0,1280,720);
        try { PixmapIO.writePNG(Gdx.files.absolute(new File(dir,name).getAbsolutePath()),p,-1,true); }
        finally { p.dispose(); }
    }
    @Override public void resize(int w,int h) { if(game!=null)game.resize(w,h); }
    @Override public void dispose() { if(game!=null)game.dispose(); }
}
