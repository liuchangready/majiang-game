import com.badlogic.gdx.ApplicationAdapter;
import com.badlogic.gdx.Gdx;
import com.badlogic.gdx.Input;
import com.badlogic.gdx.backends.lwjgl3.Lwjgl3Application;
import com.badlogic.gdx.backends.lwjgl3.Lwjgl3ApplicationConfiguration;
import com.badlogic.gdx.graphics.Pixmap;
import com.badlogic.gdx.graphics.PixmapIO;
import com.badlogic.gdx.math.Rectangle;
import com.badlogic.gdx.math.Vector3;
import com.badlogic.gdx.utils.ScreenUtils;
import com.majiang.MahjongGame;
import com.majiang.client.api.*;
import com.majiang.presentation.game.GameScreen;
import com.majiang.presentation.game.RiverHintPresentation;
import java.io.File;
import java.util.*;

/** Codex independent real GL/input acceptance for the four-wind hint panel. */
public final class TileMarkHintSmoke extends ApplicationAdapter {
    private MahjongGame game;
    private V4SmokeSession session;
    private GameScreen screen;
    private int frames, step;
    private long cursor;
    private int selectedRiverId;

    public static void main(String[] args) {
        var config = new Lwjgl3ApplicationConfiguration();
        config.setWindowedMode(Integer.getInteger("smoke.width",1280),Integer.getInteger("smoke.height",720));
        config.setInitialVisible(false);
        config.disableAudio(true); config.setForegroundFPS(30);
        new Lwjgl3Application(new TileMarkHintSmoke(), config);
    }
    @Override public void create() {
        game = new MahjongGame(); game.create(); session = new V4SmokeSession();
        publish(true, 12, 0);
        screen = new GameScreen(game.getUiAssets(), session, game, null);
        game.changeScreen(screen);
    }
    private static TileView tile(int id) { return TileView.Companion.fromTypeIndex(id / 4,id % 4); }
    private void publish(boolean marked, int wall, int dealer) {
        var hand = new ArrayList<TileView>();
        for (int id=64;id<77;id++) hand.add(tile(id));
        var builder = V4SmokeViews.player(0).closed(hand).wall(wall).dealer(dealer);
        var available = new ArrayList<TileView>();
        for (int id=1;id<136;id++) if (id != 19 && (id<64 || id>=77)) available.add(tile(id));
        var riverMark = available.get(3*18+6);
        for(int seat=0;seat<4;seat++) for(int n=0;n<18;n++) builder.discard(seat,available.get(seat*18+n));
        var tiles = List.of(
            new MarkedTileView(64,tile(64),TileMarkArea.OWN_HAND,0),
            new MarkedTileView(0,tile(0),TileMarkArea.OPPONENT_HAND,1),
            new MarkedTileView(19,tile(19),TileMarkArea.OPPONENT_HAND,2),
            new MarkedTileView(riverMark.getTileId(),riverMark,TileMarkArea.DISCARD,3,new RiverPositionView("hint-round",3,6)));
        var group = new TileMarkGroupView("hint-group","hint-round",1,4,3,false,tiles);
        var mark = marked ? new TileMarkView(false,"NOT_YOUR_TURN",10,4,12,List.of(),"hint-decision",0,1,List.of(group)) : null;
        var action = new LegalActionView.DiscardTile(0,hand.get(0));
        builder.actions(List.of(action));
        var options = V4SmokeSession.options(action);
        session.publishSnapshot(new GameSnapshotView("hint-session","hint-round","hint-epoch",++cursor,
            1L,1L,builder.build(),new DecisionView("hint-round","hint-decision",0,List.of(action),options),
            List.of(),List.of(90,100,100,100),null,List.of(),false,null,List.of(),null,null,null,mark,null),List.of());
    }
    private void check(boolean condition,String message) { if(!condition) throw new AssertionError(message); }
    private Object panel() {
        try { return screen.getClass().getMethod("getRiverHintPanel").invoke(screen); }
        catch(ReflectiveOperationException e) { throw new AssertionError("Production hint panel missing",e); }
    }
    private Object property(String getter) {
        try { return panel().getClass().getMethod(getter).invoke(panel()); }
        catch(ReflectiveOperationException e) { throw new AssertionError("Hint property missing: "+getter,e); }
    }
    private boolean open() { return (Boolean) property("isOpen"); }
    @SuppressWarnings("unchecked")
    private List<kotlin.Pair<Rectangle,Integer>> riverTargets() {
        try {
            var field = panel().getClass().getDeclaredField("riverTileClickTargets");
            field.setAccessible(true);
            return (List<kotlin.Pair<Rectangle,Integer>>)field.get(panel());
        } catch(ReflectiveOperationException e) { throw new AssertionError("River input targets missing",e); }
    }
    private void click(Rectangle rect) {
        screen.touchDown(Math.round((rect.x+rect.width/2)*Gdx.graphics.getWidth()/1280f),
            Math.round((720-rect.y-rect.height/2)*Gdx.graphics.getHeight()/720f),0,0);
    }
    private int px(float x) { return Math.round(x*Gdx.graphics.getWidth()/1280f); }
    private int py(float y) { return Math.round((720-y)*Gdx.graphics.getHeight()/720f); }
    private void dragRiver(float fromY, float toY) {
        screen.touchDown(px(1120),py(fromY),0,0);
        screen.touchDragged(px(1120),py(toY),0);
        screen.touchUp(px(1120),py(toY),0,0);
    }
    private void capture(String name) {
        File dir = new File(System.getProperty("smoke.output"));
        check(dir.isDirectory() || dir.mkdirs(),"Cannot create screenshot directory");
        Pixmap pixels = ScreenUtils.getFrameBufferPixmap(0,0,Gdx.graphics.getBackBufferWidth(),Gdx.graphics.getBackBufferHeight());
        try { PixmapIO.writePNG(Gdx.files.absolute(new File(dir,name+".png").getAbsolutePath()),pixels,-1,true); }
        finally { pixels.dispose(); }
    }
    @Override public void render() {
        screen.render(0f);
        check(++frames < 180,"Hint GL acceptance timed out");
        if(frames%5!=0)return;
        switch(step++) {
            case 0 -> {
                check(!open(),"Hint must start closed"); capture("hint-closed");
                click((Rectangle)property("getBtnToggleRect"));
                check(open(),"Real pointer did not open hint");
            }
            case 1 -> {
                capture("hint-open");
                var hint=RiverHintPresentation.INSTANCE.build(screen.getCurrentSnapshot());
                check(Objects.equals(hint.getWallRemaining(),12),"Drawable total wrong");
                check(hint.getHands().stream().mapToLong(row->row.getSlots().stream().filter(s->s.getTile()!=null).count()).sum()==3,"Only marked hand faces allowed including own row");
                check(session.submitted.isEmpty(),"Opening hint submitted game action");
                check(!riverTargets().isEmpty(),"Public river tiles must be selectable");
                var first = riverTargets().get(0);
                selectedRiverId = first.getSecond();
                click(first.getFirst());
            }
            case 2 -> {
                check(Objects.equals(property("getSelectedTileId"),selectedRiverId),"Public river focus was incorrectly cleared on update");
                capture("hint-public-river-focus");
                dragRiver(160,360);
            }
            case 3 -> {
                capture("hint-dragged-bottom");
                int bottomId=screen.getCurrentSnapshot().getView().getDiscards().get(3).get(17).getTileId();
                check(riverTargets().stream().anyMatch(t->t.getSecond()==bottomId),"Touch drag must reach North river last tile");
                check(session.submitted.isEmpty(),"River drag leaked a game action");
                dragRiver(360,160);
            }
            case 4 -> {
                screen.mouseMoved(px(1120),py(300));
                screen.scrolled(0,12);
            }
            case 5 -> {
                capture("hint-scrolled-bottom");
                int bottomId=screen.getCurrentSnapshot().getView().getDiscards().get(3).get(17).getTileId();
                check(riverTargets().stream().anyMatch(t->t.getSecond()==bottomId),"Scrolling down must reach North river last tile");
                click((Rectangle)property("getBtnToggleRect"));
                check(!open(),"Same button must close the hint");
                click((Rectangle)property("getBtnToggleRect"));
                screen.keyDown(Input.Keys.ESCAPE);
                check(!open() && game.getScreen()==screen,"Escape should close hint before leaving game");
                click((Rectangle)property("getBtnToggleRect"));
                publish(false,0,2);
            }
            case 6 -> {
                capture("hint-expired-rotated-zero");
                var hint=RiverHintPresentation.INSTANCE.build(screen.getCurrentSnapshot());
                check(Objects.equals(hint.getWallRemaining(),0),"Zero must remain zero");
                check(hint.getHands().get(0).getSeat()==2,"East must follow dealer");
                check(hint.getHands().stream().allMatch(row->row.getSlots().stream().allMatch(s->s.getTile()==null)),"Expired marks leaked faces");
                check(session.submitted.isEmpty(),"Hint interaction leaked gameplay input");
                var slot=screen.refreshHandTileRects().stream().filter(s->s.getTile().getTileId()==64).findFirst().orElseThrow();
                var rect=slot.getRect();
                var pos=screen.getTableCamera().project(new Vector3(rect.x+rect.width/2,rect.y+rect.height/2,0));
                screen.touchDown(Math.round(pos.x),Gdx.graphics.getHeight()-Math.round(pos.y),0,0);
            }
            case 7 -> {
                check(session.submitted.size()==1,"Scaled visible hand click must submit exactly one action");
                check(session.submittedActions.get(0) instanceof LegalActionView.DiscardTile discard && discard.getTile().getTileId()==64,
                    "Scaled hand input must address the exact visible entity");
                System.out.println("TILE_MARK_HINT_GL_PASS fourWinds=true expiry=true wallZero=true escape=true riverFocus=true scroll=true drag=true scaledDiscard=true");
                Gdx.app.exit();
            }
        }
    }
    @Override public void resize(int w,int h) { if(game!=null)game.resize(w,h); }
    @Override public void dispose() { if(game!=null)game.dispose(); }
}
