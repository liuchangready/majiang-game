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
import com.majiang.presentation.menu.MenuScreen;
import java.io.File;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.List;
import java.util.Map;

/** Actual GL check of waits, bankruptcy settlement, final ranking, offline recovery, and release. */
public final class EndTenpaiRenderSmoke extends ApplicationAdapter {
    private MahjongGame game;
    private GameScreen screen;
    private V4SmokeSession session;
    private int frames;

    public static void main(String[] args) {
        Lwjgl3ApplicationConfiguration cfg = new Lwjgl3ApplicationConfiguration();
        cfg.setWindowedMode(1280,720); cfg.setInitialVisible(false);
        cfg.disableAudio(true); cfg.setForegroundFPS(30);
        new Lwjgl3Application(new EndTenpaiRenderSmoke(),cfg);
    }
    @Override public void create() {
        game = new MahjongGame(); game.create();
        session = new V4SmokeSession(); publishWaiting(false,false);
        screen = new GameScreen(game.getUiAssets(),session,new V4SmokeNavigation(game,session),null);
        game.changeScreen(screen);
    }

    private void publishWaiting(boolean orphans,boolean furiten) {
        List<TileView> closed = new ArrayList<>();
        if (orphans) {
            for (SuitView suit : Arrays.asList(SuitView.MAN,SuitView.PIN,SuitView.SOU)) {
                closed.add(V4SmokeViews.tile(suit,1,0)); closed.add(V4SmokeViews.tile(suit,9,0));
            }
            for(int i=1;i<=4;i++)closed.add(V4SmokeViews.tile(SuitView.WIND,i,0));
            for(int i=1;i<=3;i++)closed.add(V4SmokeViews.tile(SuitView.DRAGON,i,0));
        } else {
            closed.addAll(V4SmokeViews.tiles(SuitView.MAN,"123456"));
            closed.addAll(V4SmokeViews.tiles(SuitView.PIN,"789"));
            closed.addAll(V4SmokeViews.tiles(SuitView.SOU,"2345"));
        }
        List<TileView> waits = orphans ? closed : Arrays.asList(
            V4SmokeViews.tile(SuitView.SOU,2,0),V4SmokeViews.tile(SuitView.SOU,5,0));
        var preview = new PlayerPresentationPreviewView("smoke-round-0","waiting",0L,
            waits,Collections.emptyList(),Collections.emptyList());
        session.publish(V4SmokeViews.player(0).phase(GamePhaseView.RESPONSE_WINDOW).turn(1)
            .closed(closed).furiten(new FuritenStatusView(furiten,false,false)).build(),
            Collections.emptyList(),preview);
    }

    private void publishSettled(boolean details) {
        List<Integer> scores = Arrays.asList(51000,-1000,25000,25000);
        var result = new PublicSessionResultView(scores,Arrays.asList(0,2,3,1),"BANKRUPTCY",Collections.emptyList());
        var progress = new SessionProgressView("FINISHED",scores,1,0,0,0,Collections.emptyList(),result);
        List<GameEventView> events = details ? Arrays.asList(
            new GameEventView.DrawGame(10,DrawReasonView.EXHAUSTIVE,Collections.singletonList(0)),
            new GameEventView.ScoreSettled(10,Map.of(0,3000,1,-1000,2,-1000,3,-1000),scores)) : Collections.emptyList();
        var detail = details ? new SettlementDetailView("流局",null,null,null,
            Collections.singletonList("荒牌流局"),0,0,0,true,"荒牌流局",
            Map.of(0,3000,1,-1000,2,-1000,3,-1000),scores,null) : null;
        session.publish(V4SmokeViews.player(0).phase(GamePhaseView.ROUND_END)
            .scores(scores).build(),Collections.emptyList(),null,events,detail,progress);
    }

    @Override public void render() {
        game.render(); frames++;
        if(frames==5)capture("tenpai-silent");
        if(frames==6)publishWaiting(true,false);
        if(frames==10)capture("tenpai-thirteen");
        if(frames==11)publishWaiting(false,true);
        if(frames==15)capture("tenpai-original-furiten");
        if(frames==16)publishSettled(true);
        if(frames==20) {
            capture("bankruptcy-round-detail");
            screen.touchDown(640,720-168,0,0);
            if(session.nextRounds!=0)throw new AssertionError("Finished match requested next round");
        }
        if(frames==25) {
            capture("bankruptcy-final-ranking");
            session.setConnected(false);
            screen.touchDown(370,720-168,0,0);
            if(!screen.isSettlementShown() || screen.isFinalResultShown())
                throw new AssertionError("Offline final results cannot open local details");
        }
        if(frames==30) {
            screen.touchDown(640,720-168,0,0);
            if(!screen.isFinalResultShown())throw new AssertionError("Offline details cannot return to results");
            publishSettled(false);
        }
        if(frames==35) {
            capture("bankruptcy-recovery");
            if(!screen.isMatchFinished())throw new AssertionError("Session terminal state lost");
            screen.touchDown(640,720-168,0,0);
            if(!(game.getScreen() instanceof MenuScreen) || !session.getClosed())
                throw new AssertionError("Offline return-to-menu failed to close session and leave game");
            System.out.println("END_TENPAI_RENDER_PASS");
            Gdx.app.exit();
        }
    }
    private void capture(String name) {
        File dir=new File(System.getProperty("smoke.output"));
        if(!dir.isDirectory()&&!dir.mkdirs())throw new AssertionError("Cannot create screenshot directory");
        Pixmap image=ScreenUtils.getFrameBufferPixmap(0,0,1280,720);
        try{PixmapIO.writePNG(Gdx.files.absolute(new File(dir,name+".png").getAbsolutePath()),image,-1,true);}
        finally{image.dispose();}
    }
    @Override public void resize(int w,int h){if(game!=null)game.resize(w,h);}
    @Override public void dispose(){if(game!=null)game.dispose();}
}
