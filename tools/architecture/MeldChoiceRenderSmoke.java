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

/** Real OpenGL and input acceptance for V4 meld option selection and pagination. */
public final class MeldChoiceRenderSmoke extends ApplicationAdapter {
    private MahjongGame game;
    private V4SmokeSession session;
    private GameScreen screen;
    private int frame;
    private LegalActionView chosenChi;

    private static TileView t(SuitView s,int n,int c) { return V4SmokeViews.tile(s,n,c); }
    private static TileView t(SuitView s,int n) { return t(s,n,0); }
    private static List<TileView> ts(SuitView s,String ranks) { return V4SmokeViews.tiles(s,ranks); }

    public static void main(String[] args) {
        Lwjgl3ApplicationConfiguration c = new Lwjgl3ApplicationConfiguration();
        c.setTitle("V4 meld choice acceptance"); c.setWindowedMode(1280,720);
        c.setInitialVisible(false); c.disableAudio(true); c.setForegroundFPS(30);
        new Lwjgl3Application(new MeldChoiceRenderSmoke(),c);
    }

    @Override public void create() {
        game = new MahjongGame(); game.create(); session = new V4SmokeSession();
        publishPon();
        game.changeScreen(new GameScreen(game.getUiAssets(),session,game,null));
        screen = (GameScreen)game.getScreen();
    }

    private void publish(V4SmokeViews.Builder builder, List<MeldPreviewOptionView> meldOptions,
                         LegalActionView... extras) {
        List<ActionOptionView> options = new ArrayList<>();
        List<LegalActionView> actions = new ArrayList<>();
        for (MeldPreviewOptionView option : meldOptions) {
            options.add(new ActionOptionView(option.getOptionId(), option.getAction()));
            actions.add(option.getAction());
        }
        for (int i=0;i<extras.length;i++) {
            options.add(new ActionOptionView("extra-"+i, extras[i]));
            actions.add(extras[i]);
        }
        var preview = new PlayerPresentationPreviewView("smoke-round-0", "meld-decision",0L,
            Collections.emptyList(),Collections.emptyList(),meldOptions);
        session.publish(builder.actions(actions).build(),options,preview);
    }

    private static MeldPreviewOptionView ponOption(TileView called,List<TileView> hand) {
        var action = new LegalActionView.DeclarePon(2,hand);
        List<TileView> tiles = new ArrayList<>(hand); tiles.add(called);
        return new MeldPreviewOptionView("pon",action,MeldTypeView.PON,tiles,hand,called,null,1);
    }
    private static MeldPreviewOptionView openKanOption(TileView called,List<TileView> hand) {
        var action = new LegalActionView.DeclareKan(2,hand,MeldTypeView.OPEN_KAN);
        List<TileView> tiles = new ArrayList<>(hand); tiles.add(called);
        return new MeldPreviewOptionView("open-kan",action,MeldTypeView.OPEN_KAN,tiles,hand,called,null,1);
    }
    private void publishPon() {
        TileView called = t(SuitView.DRAGON,1,3);
        List<TileView> dragon = ts(SuitView.DRAGON,"111");
        List<TileView> closed = new ArrayList<>(dragon);
        closed.addAll(ts(SuitView.MAN,"234")); closed.addAll(ts(SuitView.PIN,"678")); closed.addAll(ts(SuitView.SOU,"2256"));
        publish(V4SmokeViews.player(2).phase(GamePhaseView.RESPONSE_WINDOW).turn(1)
            .closed(closed).lastDiscard(called,1).discard(1,called),
            Arrays.asList(ponOption(called,dragon.subList(0,2)),openKanOption(called,dragon)),
            new LegalActionView.DeclareRon(2),new LegalActionView.Pass(2));
    }

    private static MeldPreviewOptionView chiOption(int index,TileView called,TileView a,TileView b) {
        List<TileView> tiles = Arrays.asList(a,called,b);
        var action = new LegalActionView.DeclareChi(2,new MeldView(MeldTypeView.CHI,tiles,1,called));
        return new MeldPreviewOptionView("chi-"+index,action,MeldTypeView.CHI,tiles,
            Arrays.asList(a,b),called,null,1);
    }
    private void publishChi(boolean pages) {
        TileView called = t(SuitView.MAN,5);
        List<TileView> closed = new ArrayList<>(pages ? ts(SuitView.MAN,"4466") : ts(SuitView.MAN,"3467"));
        List<MeldPreviewOptionView> options = new ArrayList<>();
        if (pages) {
            int index=0;
            for (TileView a:closed) if(a.getRank()==4)
                for(TileView b:closed) if(b.getRank()==6)
                    options.add(chiOption(index++,called,a,b));
        } else {
            options.add(chiOption(0,called,t(SuitView.MAN,3),t(SuitView.MAN,4)));
            options.add(chiOption(1,called,t(SuitView.MAN,4),t(SuitView.MAN,6)));
            options.add(chiOption(2,called,t(SuitView.MAN,6),t(SuitView.MAN,7)));
        }
        closed.addAll(ts(SuitView.PIN,"123")); closed.addAll(ts(SuitView.SOU,"123")); closed.addAll(ts(SuitView.DRAGON,"111"));
        publish(V4SmokeViews.player(2).phase(GamePhaseView.RESPONSE_WINDOW).turn(1)
            .closed(closed).lastDiscard(called,1).discard(1,called),
            options,new LegalActionView.Pass(2));
    }

    private void publishOwnKan() {
        List<TileView> pon = ts(SuitView.PIN,"555");
        List<TileView> closedKan = ts(SuitView.MAN,"1111");
        TileView added = t(SuitView.PIN,5,3);
        List<TileView> four = new ArrayList<>(pon); four.add(added);
        var closedAction = new LegalActionView.DeclareKan(2,closedKan,MeldTypeView.CLOSED_KAN);
        var addedAction = new LegalActionView.DeclareKan(2,four,MeldTypeView.ADDED_KAN);
        var closedPreview = new MeldPreviewOptionView("closed-kan",closedAction,MeldTypeView.CLOSED_KAN,
            closedKan,closedKan,null,null,null);
        var addedPreview = new MeldPreviewOptionView("added-kan",addedAction,MeldTypeView.ADDED_KAN,
            four,Collections.singletonList(added),pon.get(0),added,0);
        List<TileView> closed = new ArrayList<>(closedKan);
        closed.addAll(ts(SuitView.PIN,"67")); closed.addAll(ts(SuitView.SOU,"234")); closed.add(t(SuitView.WIND,1));
        publish(V4SmokeViews.player(2).closed(closed).drawn(added)
            .meld(2,new MeldView(MeldTypeView.PON,pon,0,pon.get(0))),
            Arrays.asList(closedPreview,addedPreview),new LegalActionView.DiscardTile(2,added));
    }

    private void click(int x,int y) {
        if(!screen.touchDown(x,720-y,0,0)) throw new AssertionError("Unhandled click "+x+","+y);
    }
    private void capture(String name) {
        File dir = new File(System.getProperty("smoke.output"));
        if (!dir.isDirectory() && !dir.mkdirs()) throw new AssertionError("Cannot create screenshot directory");
        Pixmap p=ScreenUtils.getFrameBufferPixmap(0,0,1280,720);
        try { PixmapIO.writePNG(Gdx.files.absolute(new File(dir,name).getAbsolutePath()),p,-1,true); }
        finally { p.dispose(); }
    }
    @Override public void render() {
        game.render(); frame++;
        if(frame==3)click(640,160);
        if(frame==6){capture("pon-called-and-win.png");if(!session.submitted.isEmpty())throw new AssertionError("Pon auto submitted");click(1100,160);click(350,160);}
        if(frame==9){capture("open-kan-four-tiles.png");click(720,160);}
        if(frame==11)publishChi(false);
        if(frame==12){
            // The replacement fixture has a new physical discard. Its playback must finish
            // before the response controls can accept a CHI selection.
            screen.fastForwardDiscardAnimations();
            if(!screen.canHumanChi())throw new AssertionError("CHI option not available after playback");
            click(500,160);
        }
        if(frame==15){
            capture("three-chi-choices.png");
            if(screen.getMeldSelectionModel().getVisibleOptions().size()!=3)
                throw new AssertionError("Expected three CHI candidates");
            click(780,350);
            chosenChi=screen.getMeldSelectionModel().getSelectedAction();
            if(chosenChi==null)throw new AssertionError("CHI candidate click did not select a physical option");
        }
        if(frame==18)Gdx.input.setCursorPosition(270,370);
        if(frame==21){capture("chi-hover-keeps-selection.png");click(720,160);Gdx.input.setCursorPosition(10,10);}
        if(frame==22){
            if(session.submittedActions.size()!=2 || !session.submittedActions.get(1).equals(chosenChi))
                throw new AssertionError("Hover replaced selected CHI or confirmation did not submit: "+session.submittedActions);
        }
        if(frame==23)publishOwnKan();
        if(frame==24)click(350,160);
        if(frame==27){capture("closed-and-added-kan.png");click(525,350);}
        if(frame==30){capture("added-kan-one-owned-tile.png");click(720,160);}
        if(frame==32)publishChi(true);
        if(frame==33){
            screen.fastForwardDiscardAnimations();
            if(!screen.canHumanChi())throw new AssertionError("Paged CHI option not available after playback");
            click(500,160);click(960,242);
        }
        if(frame==36){capture("choice-page-two.png");click(1100,160);}
        if(frame==39){
            if(session.submitted.size()!=3)throw new AssertionError("Wrong confirmation count "+session.submittedActions);
            System.out.println("MELD_CHOICE_RENDER_PASS frames=39 confirmations=3 screenshots=7");Gdx.app.exit();
        }
    }
    @Override public void resize(int w,int h){if(game!=null)game.resize(w,h);}
    @Override public void dispose(){if(game!=null)game.dispose();}
}
