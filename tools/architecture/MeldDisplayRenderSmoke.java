import com.badlogic.gdx.ApplicationAdapter;
import com.badlogic.gdx.Gdx;
import com.badlogic.gdx.backends.lwjgl3.Lwjgl3Application;
import com.badlogic.gdx.backends.lwjgl3.Lwjgl3ApplicationConfiguration;
import com.badlogic.gdx.graphics.Pixmap;
import com.badlogic.gdx.graphics.PixmapIO;
import com.badlogic.gdx.utils.ScreenUtils;
import com.majiang.MahjongGame;
import com.majiang.client.api.*;
import com.majiang.presentation.common.render.*;
import com.majiang.presentation.game.GameScreen;
import java.io.File;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.HashSet;
import java.util.List;
import java.util.Set;

/** Real GL check for persistent V4 meld layout, clicks, sorting, skins, and reset. */
public final class MeldDisplayRenderSmoke extends ApplicationAdapter {
    private MahjongGame game;
    private V4SmokeSession session;
    private GameScreen screen;
    private int frame;

    private static MeldView meld(MeldTypeView type) {
        List<TileView> tiles = switch (type) {
            case CHI -> V4SmokeViews.tiles(SuitView.MAN, "234");
            case PON -> V4SmokeViews.tiles(SuitView.PIN, "777");
            case OPEN_KAN -> V4SmokeViews.tiles(SuitView.SOU, "8888");
            case CLOSED_KAN -> V4SmokeViews.tiles(SuitView.DRAGON, "1111");
            case ADDED_KAN -> V4SmokeViews.tiles(SuitView.WIND, "2222");
        };
        return new MeldView(type, tiles, type == MeldTypeView.CLOSED_KAN ? -1 : 1, tiles.get(0));
    }

    private static List<MeldView> fourKans() {
        List<MeldView> out = new ArrayList<>();
        for (SuitView suit : Arrays.asList(SuitView.MAN, SuitView.PIN, SuitView.SOU, SuitView.DRAGON)) {
            List<TileView> tiles = V4SmokeViews.tiles(suit, "1111");
            out.add(new MeldView(MeldTypeView.CLOSED_KAN, tiles, -1, tiles.get(0)));
        }
        return out;
    }

    private static PlayerView view(List<MeldView> melds) {
        var builder = V4SmokeViews.player(2);
        Set<TileView> used = new HashSet<>();
        for (MeldView meld : melds) {
            builder.meld(2, meld);
            used.addAll(meld.getTiles());
        }
        TileView drawn = V4SmokeViews.tile(SuitView.WIND, 4, 3);
        used.add(drawn);
        List<TileView> closed = new ArrayList<>();
        int needed = 13 - 3 * melds.size();
        for (int id = 0; id < 136 && closed.size() < needed; id++) {
            TileView tile = TileView.Companion.fromTileId(id);
            if (!used.contains(tile)) closed.add(tile);
        }
        return builder.closed(closed).drawn(drawn).build();
    }

    public static void main(String[] args) {
        Lwjgl3ApplicationConfiguration c = new Lwjgl3ApplicationConfiguration();
        c.setWindowedMode(1280,720); c.setTitle("V4 meld display acceptance");
        c.setInitialVisible(false); c.disableAudio(true); c.setForegroundFPS(30);
        new Lwjgl3Application(new MeldDisplayRenderSmoke(),c);
    }
    @Override public void create() {
        game = new MahjongGame(new SkinManager(new InMemorySkinPreferences(SkinId.WIKIPEDIA)));
        game.create(); session = new V4SmokeSession();
        session.publish(view(Collections.singletonList(meld(MeldTypeView.CHI))));
        game.changeScreen(new GameScreen(game.getUiAssets(),session,game,null));
        screen = (GameScreen)game.getScreen();
    }
    private void capture(String name) {
        File dir = new File(System.getProperty("smoke.output"));
        if (!dir.isDirectory() && !dir.mkdirs()) throw new AssertionError("Cannot create screenshot directory");
        Pixmap p = ScreenUtils.getFrameBufferPixmap(0,0,1280,720);
        try { PixmapIO.writePNG(Gdx.files.absolute(new File(dir,name).getAbsolutePath()),p,-1,true); }
        finally { p.dispose(); }
    }
    private void click(int x,int y) { screen.touchDown(x,720-y,0,0); }
    private void publish(MeldView... melds) { session.publish(view(Arrays.asList(melds))); }

    @Override public void render() {
        game.render(); frame++;
        if (frame==3) { capture("completed-chi.png"); publish(meld(MeldTypeView.PON)); }
        if (frame==6) { capture("completed-pon.png"); publish(meld(MeldTypeView.OPEN_KAN)); }
        if (frame==9) { capture("completed-open-kan.png"); publish(meld(MeldTypeView.CLOSED_KAN)); }
        if (frame==12) { capture("completed-closed-kan.png"); publish(meld(MeldTypeView.ADDED_KAN)); }
        if (frame==15) { capture("completed-added-kan.png"); publish(meld(MeldTypeView.CHI),meld(MeldTypeView.PON),meld(MeldTypeView.CLOSED_KAN),meld(MeldTypeView.ADDED_KAN)); }
        if (frame==18) { capture("four-melds.png"); session.publish(view(fourKans())); }
        if (frame==21) {
            capture("four-kans.png");
            for (MeldDisplayGroup group : MeldDisplayLayout.INSTANCE.layout(screen.getCurrentView()))
                for (MeldDisplayTile tile : group.getTiles())
                    click((int)(tile.getX()+tile.getWidth()/2),(int)(tile.getY()+tile.getHeight()/2));
            if (!session.submitted.isEmpty()) throw new AssertionError("Meld tile was discarded");
            click(1140,75);
        }
        if (frame==24) { capture("four-kans-sorted.png"); game.getSkinManager().setSkin(SkinId.CLASSIC); publish(meld(MeldTypeView.CHI),meld(MeldTypeView.PON),meld(MeldTypeView.CLOSED_KAN),meld(MeldTypeView.ADDED_KAN)); }
        if (frame==27) { capture("classic-four-melds.png"); game.getSkinManager().setSkin(SkinId.WIKIPEDIA); session.publish(view(Collections.emptyList())); }
        if (frame==30) {
            capture("new-round-no-melds.png");
            if (!MeldDisplayLayout.INSTANCE.layout(screen.getCurrentView()).isEmpty()) throw new AssertionError("Stale melds");
            System.out.println("MELD_DISPLAY_RENDER_PASS frames=30 screenshots=10 accidentalDiscards="+session.submitted.size());
            Gdx.app.exit();
        }
    }
    @Override public void resize(int w,int h) { if(game!=null)game.resize(w,h); }
    @Override public void dispose() { if(game!=null)game.dispose(); }
}
