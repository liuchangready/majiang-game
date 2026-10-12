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
import com.majiang.presentation.game.OpponentHandPresentation;
import com.majiang.presentation.game.RiverHintPresentation;
import java.io.File;
import java.util.*;

/** Codex visual fixture: twelve identified opponent faces, all river seats, expiry cleanup. */
public final class TileMarkVisibilitySmoke extends ApplicationAdapter {
    private MahjongGame game;
    private V4SmokeSession session;
    private GameScreen screen;
    private int frames, scenario;
    private boolean capturedClosed;

    public static void main(String[] args) {
        var config = new Lwjgl3ApplicationConfiguration();
        config.setWindowedMode(Integer.getInteger("smoke.width",1280),Integer.getInteger("smoke.height",720));
        config.setInitialVisible(false);
        config.disableAudio(true); config.setForegroundFPS(30);
        new Lwjgl3Application(new TileMarkVisibilitySmoke(), config);
    }
    @Override public void create() {
        game = new MahjongGame(); game.create(); session = new V4SmokeSession();
        publish(0);
        screen = new GameScreen(game.getUiAssets(), session, game, null);
        game.changeScreen(screen);
    }
    private void publish(int which) {
        var builder = V4SmokeViews.player(0).closed(V4SmokeViews.tiles(SuitView.PIN,"123456789"));
        List<MarkedTileView> marked = new ArrayList<>();
        for (int id = 0; id < 12; id++) {
            var tile = TileView.Companion.fromTypeIndex(id / 4, id % 4);
            if (which == 3 && id < 4) {
                for (int n = 0; n < 6; n++) builder.discard(id, TileView.Companion.fromTypeIndex(10 + id * 2 + n / 4, n % 4));
                builder.discard(id, tile);
                marked.add(new MarkedTileView(id, tile, TileMarkArea.DISCARD, id, new RiverPositionView("visual-round",id,6)));
            } else if (which == 5) {
                marked.add(new MarkedTileView(id,tile,id < 3 ? TileMarkArea.EXPOSED_MELD : TileMarkArea.OPPONENT_HAND,2));
            } else if (which == 4 && id != 7) {
                marked.add(new MarkedTileView(id,tile,TileMarkArea.EXPOSED_MELD,id / 4));
            } else if (which >= 3) {
                marked.add(new MarkedTileView(id,tile,TileMarkArea.NOT_VISIBLE));
            } else {
                marked.add(new MarkedTileView(id,tile,TileMarkArea.OPPONENT_HAND,which + 1));
            }
        }
        if (which == 4) {
            for (int seat = 0; seat < 3; seat++) {
                List<TileView> tiles = new ArrayList<>();
                for (int copy = 0; copy < (seat == 1 ? 3 : 4); copy++) tiles.add(TileView.Companion.fromTypeIndex(seat,copy));
                var type = seat == 0 ? MeldTypeView.OPEN_KAN : seat == 1 ? MeldTypeView.PON : MeldTypeView.ADDED_KAN;
                builder.meld(seat,new MeldView(type,tiles,(seat + 3) % 4,tiles.get(0)));
            }
            List<TileView> hidden = new ArrayList<>();
            for (int copy = 0; copy < 4; copy++) hidden.add(TileView.Companion.fromTypeIndex(4,copy));
            builder.meld(3,new MeldView(MeldTypeView.CLOSED_KAN,hidden,-1,hidden.get(0)));
        }
        if (which == 5) {
            var pon = List.of(TileView.Companion.fromTypeIndex(0,0),TileView.Companion.fromTypeIndex(0,1),TileView.Companion.fromTypeIndex(0,2));
            builder.meld(2,new MeldView(MeldTypeView.PON,pon,1,pon.get(0)));
        }
        List<TileMarkGroupView> groups = new ArrayList<>();
        for (int i = 0; i < 3; i++) groups.add(new TileMarkGroupView("group-" + i,"created-" + i,i+1,i+4,i,i==0,marked.subList(i*4,i*4+4)));
        TileMarkView mark = which == 6 ? null : new TileMarkView(false,"TILE_MARK_CAPACITY_EXCEEDED",10,12,12,List.of(),"visual-decision",0,1,groups);
        var snapshot = new GameSnapshotView("visual-session","visual-round","visual-epoch",which+1,
            1L,1L,builder.build(),new DecisionView("visual-round","visual-decision",0,List.of(),List.of()),
            List.of(),List.of(70,100,100,100),null,List.of(),false,null,List.of(),null,null,null,mark,null);
        session.publishSnapshot(snapshot,List.of());
    }
    private void capture(String name) {
        File dir = new File(System.getProperty("smoke.output"));
        if (!dir.isDirectory() && !dir.mkdirs()) throw new AssertionError("Cannot create screenshot directory");
        Pixmap pixels = ScreenUtils.getFrameBufferPixmap(0,0,Gdx.graphics.getBackBufferWidth(),Gdx.graphics.getBackBufferHeight());
        try { PixmapIO.writePNG(Gdx.files.absolute(new File(dir,name+".png").getAbsolutePath()),pixels,-1,true); }
        finally { pixels.dispose(); }
    }
    @Override public void render() {
        screen.render(0f);
        if (++frames % 5 != 0) return;
        if (!capturedClosed) {
            capture("visibility-" + scenario);
            var toggle = screen.getRiverHintPanel().getBtnToggleRect();
            screen.touchDown((int)((toggle.x+toggle.width/2)*Gdx.graphics.getWidth()/1280f),
                (int)((720-toggle.y-toggle.height/2)*Gdx.graphics.getHeight()/720f),0,0);
            if (!screen.getRiverHintPanel().isOpen()) throw new AssertionError("Hint button must remain reachable");
            capturedClosed = true;
            return;
        }
        capture("visibility-" + scenario + "-hint");
        var snapshot = screen.getCurrentSnapshot();
        var rows = OpponentHandPresentation.INSTANCE.build(snapshot);
        if (rows.size() != 3) throw new AssertionError("All three opponent hands must be displayed");
        int faces = 0;
        for (var row : rows) {
            if (row.getSlots().size() != snapshot.getView().getHandTileCounts().get(row.getSeat()))
                throw new AssertionError("Marked faces must replace backs, never add tiles");
            faces += (int) row.getSlots().stream().filter(slot -> slot.getTile() != null).count();
        }
        int expected = scenario < 3 ? 12 : scenario == 5 ? 9 : 0;
        if (faces != expected) throw new AssertionError("Authorized opponent face count " + faces + " != " + expected);
        var hint = RiverHintPresentation.INSTANCE.build(snapshot);
        if (hint.getHands().size() != 4 || !Objects.equals(hint.getWallRemaining(), 70))
            throw new AssertionError("Hint must expose four winds and authoritative drawable total");
        if (scenario == 6) {
            System.out.println("TILE_MARK_VISIBILITY_GL_PASS scenarios=7");
            Gdx.app.exit(); return;
        }
        screen.getRiverHintPanel().close();
        capturedClosed = false;
        publish(++scenario);
        screen.fastForwardDiscardAnimations();
    }
    @Override public void resize(int w,int h) { if (game != null) game.resize(w,h); }
    @Override public void dispose() { if (game != null) game.dispose(); }
}
