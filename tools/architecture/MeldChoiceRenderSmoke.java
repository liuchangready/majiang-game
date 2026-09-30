import com.badlogic.gdx.*;
import com.badlogic.gdx.backends.lwjgl3.*;
import com.badlogic.gdx.graphics.*;
import com.badlogic.gdx.utils.ScreenUtils;
import com.majiang.MahjongGame;
import com.majiang.model.*;
import com.majiang.rules.FuritenRules;
import com.majiang.screen.GameScreen;
import com.majiang.session.*;
import com.majiang.state.*;
import java.util.*;
import kotlin.Unit;
import kotlin.jvm.functions.Function1;

/** Codex-owned actual OpenGL/input check. Rules and settlement have separate real-session tests. */
public final class MeldChoiceRenderSmoke extends ApplicationAdapter {
    private MahjongGame game; private PreviewSession session; private GameScreen screen; private int frame;
    private GameAction chosenChi;
    static Tile t(Suit s,int n){return new Tile(s,n,0);}
    static List<Tile> ts(Suit suit,String ranks){
        Map<Integer,Integer> count=new HashMap<>(); List<Tile> out=new ArrayList<>();
        for(char c:ranks.toCharArray()){int n=c-'0',copy=count.getOrDefault(n,0);out.add(new Tile(suit,n,copy));count.put(n,copy+1);}return out;
    }
    static PlayerView view(GameState s,List<GameAction> actions){
        PlayerHand h=s.getPlayers().get(2);
        return PlayerView.Companion.fromGameState(s,2,actions,FuritenRules.INSTANCE.status(h,h.getClosedTiles(),false,false),h.getDiscardHistory());
    }
    static GameState response(Tile called){
        GameState s=new GameState();s.setPhase(GamePhase.RESPONSE_WINDOW);s.setCurrentPlayer(1);
        s.setLastDiscard(called);s.setLastDiscardPlayer(1);s.getPlayers().get(1).getDiscards().add(called);return s;
    }
    static PlayerView pon(){
        Tile called=new Tile(Suit.DRAGON,1,3);GameState s=response(called);PlayerHand h=s.getPlayers().get(2);
        h.getClosedTiles().addAll(ts(Suit.DRAGON,"111"));h.getClosedTiles().addAll(ts(Suit.MAN,"234"));
        h.getClosedTiles().addAll(ts(Suit.PIN,"678"));h.getClosedTiles().addAll(ts(Suit.SOU,"2256"));
        return view(s,Arrays.asList(new GameAction.DeclarePon(2,new ArrayList<>(h.getClosedTiles().subList(0,2))),
            new GameAction.DeclareKan(2,new ArrayList<>(h.getClosedTiles().subList(0,3)),MeldType.OPEN_KAN),
            new GameAction.DeclareRon(2),new GameAction.Pass(2)));
    }
    static PlayerView chi(boolean pages){
        Tile called=t(Suit.MAN,5);GameState s=response(called);PlayerHand h=s.getPlayers().get(2);
        List<GameAction> actions=new ArrayList<>();
        if(pages){
            List<Tile> own=ts(Suit.MAN,"4466");h.getClosedTiles().addAll(own);
            for(Tile a:own)if(a.getRank()==4)for(Tile b:own)if(b.getRank()==6)
                actions.add(new GameAction.DeclareChi(2,new Meld(MeldType.CHI,Arrays.asList(a,called,b),1,called)));
        } else {
            h.getClosedTiles().addAll(ts(Suit.MAN,"3467"));
            for(int start=3;start<=5;start++){
                List<Tile> tiles=new ArrayList<>();for(int n=start;n<start+3;n++)tiles.add(t(Suit.MAN,n));
                actions.add(new GameAction.DeclareChi(2,new Meld(MeldType.CHI,tiles,1,called)));
            }
        }
        h.getClosedTiles().addAll(ts(Suit.PIN,"123"));h.getClosedTiles().addAll(ts(Suit.SOU,"123"));h.getClosedTiles().addAll(ts(Suit.DRAGON,"111"));
        actions.add(new GameAction.Pass(2));return view(s,actions);
    }
    static PlayerView ownKan(){
        GameState s=new GameState();s.setPhase(GamePhase.DISCARD);s.setCurrentPlayer(2);PlayerHand h=s.getPlayers().get(2);
        List<Tile> pon=ts(Suit.PIN,"555"),closed=ts(Suit.MAN,"1111");Tile added=new Tile(Suit.PIN,5,3);
        h.getMelds().add(new Meld(MeldType.PON,pon,0,pon.get(0)));h.getClosedTiles().addAll(closed);
        h.getClosedTiles().addAll(ts(Suit.PIN,"67"));h.getClosedTiles().addAll(ts(Suit.SOU,"234"));h.getClosedTiles().add(t(Suit.WIND,1));h.setDrawnTile(added);
        List<Tile> four=new ArrayList<>(pon);four.add(added);
        return view(s,Arrays.asList(new GameAction.DeclareKan(2,closed,MeldType.CLOSED_KAN),new GameAction.DeclareKan(2,four,MeldType.ADDED_KAN),new GameAction.DiscardTile(2,added)));
    }
    public static void main(String[] args){
        Lwjgl3ApplicationConfiguration c=new Lwjgl3ApplicationConfiguration();c.setTitle("Meld choice acceptance");c.setWindowedMode(1280,720);
        c.setInitialVisible(false);c.disableAudio(true);c.setForegroundFPS(30);new Lwjgl3Application(new MeldChoiceRenderSmoke(),c);
    }
    @Override public void create(){game=new MahjongGame();game.create();session=new PreviewSession();session.update(pon());game.launchGame(session);screen=(GameScreen)game.getScreen();screen.startGame();}
    void click(int x,int y){if(!screen.touchDown(x,720-y,0,0))throw new AssertionError("Unhandled click "+x+","+y);}
    void capture(String name){Pixmap p=ScreenUtils.getFrameBufferPixmap(0,0,1280,720);try{PixmapIO.writePNG(Gdx.files.absolute(System.getProperty("smoke.output")+"/"+name),p,-1,true);}finally{p.dispose();}}
    @Override public void render(){
        game.render();frame++;
        if(frame==3)click(640,160);
        if(frame==6){capture("pon-called-and-win.png");if(!session.submitted.isEmpty())throw new AssertionError("Pon auto submitted");click(1100,160);click(350,160);}
        if(frame==9){capture("open-kan-four-tiles.png");click(720,160);}
        if(frame==12){session.update(chi(false));click(500,160);}
        if(frame==15){capture("three-chi-choices.png");click(780,350);chosenChi=screen.getMeldSelectionModel().getSelectedAction();}
        if(frame==18){Gdx.input.setCursorPosition(270,370);}
        if(frame==21){capture("chi-hover-keeps-selection.png");click(720,160);if(!session.submitted.get(1).equals(chosenChi))throw new AssertionError("Hover replaced selected chi");Gdx.input.setCursorPosition(10,10);}
        if(frame==24){session.update(ownKan());click(350,160);}
        if(frame==27){capture("closed-and-added-kan.png");click(525,350);}
        if(frame==30){capture("added-kan-one-owned-tile.png");click(720,160);}
        if(frame==33){session.update(chi(true));click(500,160);click(960,242);}
        if(frame==36){capture("choice-page-two.png");click(1100,160);}
        if(frame==39){
            if(session.submitted.size()!=3)throw new AssertionError("Wrong confirmation count "+session.submitted);
            System.out.println("MELD_CHOICE_RENDER_PASS frames=39 confirmations=3 screenshots=7");Gdx.app.exit();
        }
    }
    @Override public void resize(int w,int h){if(game!=null)game.resize(w,h);}
    @Override public void dispose(){if(game!=null)game.dispose();}
    private static final class PreviewSession implements GameSession {
        private PlayerView snapshot;final List<GameAction> submitted=new ArrayList<>();
        private final List<Function1<? super SessionUpdate,Unit>> listeners=new ArrayList<>();
        void update(PlayerView v){snapshot=v;for(Function1<? super SessionUpdate,Unit> l:new ArrayList<>(listeners))l.invoke(new SessionUpdate(v,Collections.emptyList()));}
        public int getViewerSeat(){return 2;}public PlayerView view(){return snapshot;}public List<GameAction> legalActions(){return snapshot.getLegalActions();}
        public ActionResult start(){return new ActionResult.Accepted(Collections.emptyList());}
        public ActionResult submit(GameAction a){if(!legalActions().contains(a))return new ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE);submitted.add(a);return new ActionResult.Accepted(Collections.emptyList());}
        public ActionResult nextRound(){return new ActionResult.Rejected(RejectionReason.WRONG_PHASE);}
        public AutoCloseable subscribe(Function1<? super SessionUpdate,Unit> l){listeners.add(l);return ()->listeners.remove(l);}
        public void close(){listeners.clear();}
    }
}
