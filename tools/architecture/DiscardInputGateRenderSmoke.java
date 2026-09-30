import com.badlogic.gdx.*;
import com.badlogic.gdx.backends.lwjgl3.*;
import com.badlogic.gdx.graphics.*;
import com.badlogic.gdx.utils.ScreenUtils;
import com.majiang.MahjongGame;
import com.majiang.model.*;
import com.majiang.render.*;
import com.majiang.screen.GameScreen;
import com.majiang.session.*;
import com.majiang.state.*;
import java.util.*;
import kotlin.Unit;
import kotlin.jvm.functions.Function1;

/** Real GL/input regression: latest logical view is playable while four discard animations remain. */
public final class DiscardInputGateRenderSmoke extends ApplicationAdapter {
    private MahjongGame game;private PreviewSession session;private GameScreen screen;private int frame,stage,completed;
    public static void main(String[] args){Lwjgl3ApplicationConfiguration c=new Lwjgl3ApplicationConfiguration();c.setWindowedMode(1280,720);c.setTitle("Discard input gate acceptance");c.setInitialVisible(false);c.disableAudio(true);c.setForegroundFPS(30);new Lwjgl3Application(new DiscardInputGateRenderSmoke(),c);}
    @Override public void create(){game=new MahjongGame(new SkinManager(new InMemorySkinPreferences(SkinId.WIKIPEDIA)));game.create();session=new PreviewSession();game.launchGame(session);screen=(GameScreen)game.getScreen();screen.startGame();}
    void click(int x,int y){screen.touchDown(x,720-y,0,0);}
    void clickTile(){GameScreen.HandTileSlot slot=screen.refreshHandTileRects().get(0);click((int)(slot.getRect().x+slot.getRect().width/2),80);}
    void capture(String name){Pixmap p=ScreenUtils.getFrameBufferPixmap(0,0,1280,720);try{PixmapIO.writePNG(Gdx.files.absolute(System.getProperty("smoke.output")+"/"+name+".png"),p,-1,true);}finally{p.dispose();}}
    @Override public void render(){
        screen.render(0f);frame++;if(frame%3!=0)return;
        if(stage==0){clickTile();clickTile();if(session.submitted.size()!=1)throw new AssertionError("Double click bypassed before render");if(!screen.isDiscardPlaybackBusy())throw new AssertionError("Missing synchronous input lock");stage++;}
        else if(stage==1){capture("waiting-for-discards");screen.updateDiscardAnimation(0.30f);stage++;}
        else if(stage==2){
            clickTile();click(1140,75);
            if(session.submitted.size()!=1 || screen.isAutoSort())throw new AssertionError("Playback accepted hand/sort input");
            if(completed==0)capture("discard-in-flight-locked");
            screen.updateDiscardAnimation(DiscardAnimation.TOTAL_DURATION+0.01f);completed++;
            if(!screen.isDiscardPlaybackBusy()){if(completed!=4)throw new AssertionError("Did not await all four discards");stage++;}
        }
        else if(stage==3){capture("playback-complete-unlocked");clickTile();if(session.submitted.size()!=2)throw new AssertionError("Input failed to unlock");if(!screen.isDiscardPlaybackBusy())throw new AssertionError("Second turn did not lock again");stage++;}
        else if(stage==4){capture("next-round-of-discards-locked");System.out.println("DISCARD_INPUT_GATE_RENDER_PASS screenshots=4 discardsWaited="+completed+" submissions="+session.submitted.size());Gdx.app.exit();stage++;}
        if(frame>150)throw new AssertionError("Animation gate stuck");
    }
    @Override public void resize(int w,int h){if(game!=null)game.resize(w,h);}
    @Override public void dispose(){if(game!=null)game.dispose();}
    static final class PreviewSession implements GameSession {
        final GameState state=new GameState();final List<GameAction> submitted=new ArrayList<>();final List<Function1<? super SessionUpdate,Unit>> listeners=new ArrayList<>();
        PreviewSession(){state.setPhase(GamePhase.DISCARD);state.setCurrentPlayer(2);PlayerHand h=state.getPlayers().get(2);for(int r=1;r<=9;r++)h.getClosedTiles().add(new Tile(Suit.SOU,r,0));for(int r=1;r<=4;r++)h.getClosedTiles().add(new Tile(Suit.PIN,r,0));h.setDrawnTile(new Tile(Suit.PIN,5,0));}
        public int getViewerSeat(){return 2;}
        public PlayerView view(){return PlayerView.Companion.fromGameState(state,2,legalActions(),new com.majiang.rules.FuritenStatus(false,false,false),Collections.emptyList());}
        public List<GameAction> legalActions(){List<GameAction> out=new ArrayList<>();for(Tile t:state.getPlayers().get(2).getAllClosedTiles())out.add(new GameAction.DiscardTile(2,t));return out;}
        public ActionResult start(){return new ActionResult.Accepted(Collections.emptyList());}
        public ActionResult submit(GameAction a){
            if(!legalActions().contains(a))return new ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE);
            submitted.add(a);PlayerHand h=state.getPlayers().get(2);Tile tile=((GameAction.DiscardTile)a).getTile();
            if(!tile.equals(h.getDrawnTile())){h.getClosedTiles().remove(tile);h.getClosedTiles().add(h.getDrawnTile());}
            h.setDrawnTile(new Tile(Suit.PIN,5+submitted.size(),1));h.getDiscards().add(tile);
            for(int seat:new int[]{0,1,3})state.getPlayers().get(seat).getDiscards().add(new Tile(Suit.MAN,seat+1,submitted.size()-1));
            PlayerView snapshot=view();for(Function1<? super SessionUpdate,Unit> listener:new ArrayList<>(listeners))listener.invoke(new SessionUpdate(snapshot,Collections.emptyList()));
            return new ActionResult.Accepted(Collections.emptyList());
        }
        public ActionResult nextRound(){return new ActionResult.Rejected(RejectionReason.WRONG_PHASE);}
        public AutoCloseable subscribe(Function1<? super SessionUpdate,Unit> listener){listeners.add(listener);return ()->listeners.remove(listener);}
        public void close(){listeners.clear();}
    }
}
