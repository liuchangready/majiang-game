import com.badlogic.gdx.*;
import com.badlogic.gdx.backends.lwjgl3.*;
import com.badlogic.gdx.graphics.*;
import com.badlogic.gdx.utils.ScreenUtils;
import com.majiang.MahjongGame;
import com.majiang.model.*;
import com.majiang.render.*;
import com.majiang.rules.FuritenRules;
import com.majiang.screen.GameScreen;
import com.majiang.session.*;
import com.majiang.state.*;
import java.util.*;
import kotlin.Unit;
import kotlin.jvm.functions.Function1;

/** Codex-owned real GL check for persistent melds. No production or domain mutations. */
public final class MeldDisplayRenderSmoke extends ApplicationAdapter {
    private MahjongGame game;private PreviewSession session;private GameScreen screen;private int frame;
    static List<Tile> ts(Suit suit,String ranks){Map<Integer,Integer> n=new HashMap<>();List<Tile> out=new ArrayList<>();for(char c:ranks.toCharArray()){int r=c-'0',i=n.getOrDefault(r,0);out.add(new Tile(suit,r,i));n.put(r,i+1);}return out;}
    static Meld meld(MeldType type){
        List<Tile> tiles;
        switch(type){case CHI:tiles=ts(Suit.MAN,"234");break;case PON:tiles=ts(Suit.PIN,"777");break;case OPEN_KAN:tiles=ts(Suit.SOU,"8888");break;case CLOSED_KAN:tiles=ts(Suit.DRAGON,"1111");break;default:tiles=ts(Suit.WIND,"2222");}
        return new Meld(type,tiles,type==MeldType.CLOSED_KAN?-1:1,tiles.get(0));
    }
    static List<Meld> fourKans(){List<Meld> out=new ArrayList<>();for(Suit suit:Arrays.asList(Suit.MAN,Suit.PIN,Suit.SOU,Suit.DRAGON)){List<Tile> tiles=ts(suit,"1111");out.add(new Meld(MeldType.CLOSED_KAN,tiles,-1,tiles.get(0)));}return out;}
    static PlayerView view(List<Meld> melds){
        GameState s=new GameState();s.setPhase(GamePhase.DISCARD);s.setCurrentPlayer(2);PlayerHand h=s.getPlayers().get(2);h.getMelds().addAll(melds);
        Set<Tile> used=new HashSet<>();for(Meld m:melds)used.addAll(m.getTiles());
        Tile drawn=new Tile(Suit.WIND,4,3);used.add(drawn);h.setDrawnTile(drawn);
        List<Tile> available=new ArrayList<>();for(Suit suit:Suit.values())for(int rank=1;rank<=(suit==Suit.WIND?4:suit==Suit.DRAGON?3:9);rank++)for(int copy=0;copy<4;copy++){Tile t=new Tile(suit,rank,copy);if(!used.contains(t))available.add(t);}
        h.getClosedTiles().addAll(available.subList(0,13-3*melds.size()));
        List<GameAction> actions=new ArrayList<>();for(Tile t:h.getAllClosedTiles())actions.add(new GameAction.DiscardTile(2,t));
        return PlayerView.Companion.fromGameState(s,2,actions,FuritenRules.INSTANCE.status(h,h.getClosedTiles(),false,false),h.getDiscardHistory());
    }
    public static void main(String[] args){Lwjgl3ApplicationConfiguration c=new Lwjgl3ApplicationConfiguration();c.setWindowedMode(1280,720);c.setTitle("Meld display acceptance");c.setInitialVisible(false);c.disableAudio(true);c.setForegroundFPS(30);new Lwjgl3Application(new MeldDisplayRenderSmoke(),c);}
    @Override public void create(){game=new MahjongGame(new SkinManager(new InMemorySkinPreferences(SkinId.WIKIPEDIA)));game.create();session=new PreviewSession();session.update(view(Arrays.asList(meld(MeldType.CHI))));game.launchGame(session);screen=(GameScreen)game.getScreen();screen.startGame();}
    void capture(String name){Pixmap p=ScreenUtils.getFrameBufferPixmap(0,0,1280,720);try{PixmapIO.writePNG(Gdx.files.absolute(System.getProperty("smoke.output")+"/"+name),p,-1,true);}finally{p.dispose();}}
    void click(int x,int y){screen.touchDown(x,720-y,0,0);}
    @Override public void render(){
        game.render();frame++;
        if(frame==3){capture("completed-chi.png");session.update(view(Arrays.asList(meld(MeldType.PON))));}
        if(frame==6){capture("completed-pon.png");session.update(view(Arrays.asList(meld(MeldType.OPEN_KAN))));}
        if(frame==9){capture("completed-open-kan.png");session.update(view(Arrays.asList(meld(MeldType.CLOSED_KAN))));}
        if(frame==12){capture("completed-closed-kan.png");session.update(view(Arrays.asList(meld(MeldType.ADDED_KAN))));}
        if(frame==15){capture("completed-added-kan.png");session.update(view(Arrays.asList(meld(MeldType.CHI),meld(MeldType.PON),meld(MeldType.CLOSED_KAN),meld(MeldType.ADDED_KAN))));}
        if(frame==18){capture("four-melds.png");session.update(view(fourKans()));}
        if(frame==21){
            capture("four-kans.png");
            for(MeldDisplayGroup g:screen.displayedMelds())for(MeldDisplayTile t:g.getTiles())click((int)(t.getX()+t.getWidth()/2),(int)(t.getY()+t.getHeight()/2));
            if(!session.submitted.isEmpty())throw new AssertionError("Meld tile was discarded");
            click(1140,75);
        }
        if(frame==24){capture("four-kans-sorted.png");game.getSkinManager().setSkin(SkinId.CLASSIC);session.update(view(Arrays.asList(meld(MeldType.CHI),meld(MeldType.PON),meld(MeldType.CLOSED_KAN),meld(MeldType.ADDED_KAN))));}
        if(frame==27){capture("classic-four-melds.png");game.getSkinManager().setSkin(SkinId.WIKIPEDIA);session.update(view(Collections.emptyList()));}
        if(frame==30){capture("new-round-no-melds.png");if(!screen.displayedMelds().isEmpty())throw new AssertionError("Stale melds");System.out.println("MELD_DISPLAY_RENDER_PASS frames=30 screenshots=10 accidentalDiscards="+session.submitted.size());Gdx.app.exit();}
    }
    @Override public void resize(int w,int h){if(game!=null)game.resize(w,h);}
    @Override public void dispose(){if(game!=null)game.dispose();}
    private static final class PreviewSession implements GameSession {
        private PlayerView snapshot;final List<GameAction> submitted=new ArrayList<>();private final List<Function1<? super SessionUpdate,Unit>> listeners=new ArrayList<>();
        void update(PlayerView v){snapshot=v;for(Function1<? super SessionUpdate,Unit> l:new ArrayList<>(listeners))l.invoke(new SessionUpdate(v,Collections.emptyList()));}
        public int getViewerSeat(){return 2;}public PlayerView view(){return snapshot;}public List<GameAction> legalActions(){return snapshot.getLegalActions();}
        public ActionResult start(){return new ActionResult.Accepted(Collections.emptyList());}
        public ActionResult submit(GameAction a){if(!legalActions().contains(a))return new ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE);submitted.add(a);return new ActionResult.Accepted(Collections.emptyList());}
        public ActionResult nextRound(){return new ActionResult.Rejected(RejectionReason.WRONG_PHASE);}
        public AutoCloseable subscribe(Function1<? super SessionUpdate,Unit> l){listeners.add(l);return ()->listeners.remove(l);}
        public void close(){listeners.clear();}
    }
}
