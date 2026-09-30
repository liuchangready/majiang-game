import com.badlogic.gdx.ApplicationAdapter;
import com.badlogic.gdx.Gdx;
import com.badlogic.gdx.backends.lwjgl3.*;
import com.badlogic.gdx.graphics.Pixmap;
import com.badlogic.gdx.graphics.PixmapIO;
import com.badlogic.gdx.utils.ScreenUtils;
import com.majiang.MahjongGame;
import com.majiang.model.*;
import com.majiang.screen.GameScreen;
import com.majiang.session.*;
import com.majiang.rules.FuritenRules;
import com.majiang.state.*;
import java.util.*;
import kotlin.Unit;
import kotlin.jvm.functions.Function1;

/** Codex-owned visual/interaction acceptance: actual OpenGL and actual GameScreen input. */
public final class FuritenRenderSmoke extends ApplicationAdapter {
    private MahjongGame game;
    private PreviewSession session;
    private GameScreen screen;
    private int frame;
    private static Tile t(Suit suit, int rank) { return new Tile(suit, rank, 0); }
    public static void main(String[] args) {
        Lwjgl3ApplicationConfiguration c = new Lwjgl3ApplicationConfiguration();
        c.setTitle("Furiten acceptance"); c.setWindowedMode(1280, 720);
        c.setInitialVisible(false); c.disableAudio(true); c.setForegroundFPS(30);
        new Lwjgl3Application(new FuritenRenderSmoke(), c);
    }
    @Override public void create() {
        game = new MahjongGame(); game.create(); session = new PreviewSession();
        session.update(regular()); game.launchGame(session);
        screen = (GameScreen) game.getScreen(); screen.startGame();
    }
    private static PlayerView regular() {
        GameState s = new GameState(); s.setPhase(GamePhase.DISCARD); s.setCurrentPlayer(2);
        PlayerHand h = s.getPlayers().get(2);
        for (int n=1;n<=6;n++) h.getClosedTiles().add(t(Suit.MAN,n));
        for (int n=7;n<=9;n++) h.getClosedTiles().add(t(Suit.PIN,n));
        for (int n=2;n<=5;n++) h.getClosedTiles().add(t(Suit.SOU,n));
        h.setDrawnTile(t(Suit.SOU,6)); h.getDiscardHistory().add(new Tile(Suit.SOU,5,1));
        return PlayerView.Companion.fromGameState(s,2,Arrays.asList(
            new GameAction.DeclareRiichi(2,t(Suit.SOU,2)),new GameAction.DeclareRiichi(2,t(Suit.SOU,6)),
            new GameAction.DiscardTile(2,t(Suit.SOU,6))),
            FuritenRules.INSTANCE.status(h,h.getClosedTiles(),false,false),h.getDiscardHistory());
    }
    private static PlayerView orphans() { return orphans(false); }
    private static PlayerView orphans(boolean winning) {
        GameState s = new GameState(); s.setPhase(GamePhase.DISCARD); s.setCurrentPlayer(2);
        PlayerHand h = s.getPlayers().get(2);
        for (Suit suit:Arrays.asList(Suit.MAN,Suit.SOU,Suit.PIN)) {
            h.getClosedTiles().add(t(suit,1)); h.getClosedTiles().add(t(suit,9));
        }
        for(int n=1;n<=4;n++) h.getClosedTiles().add(t(Suit.WIND,n));
        for(int n=1;n<=3;n++) h.getClosedTiles().add(t(Suit.DRAGON,n));
        h.setDrawnTile(winning ? new Tile(Suit.WIND,1,1) : t(Suit.MAN,2));
        h.getDiscardHistory().add(new Tile(Suit.WIND,2,1));
        s.getSameTurnFuriten()[2] = true; s.getRiichiFuriten()[2] = true;
        List<GameAction> actions = winning ? Arrays.asList(new GameAction.DeclareTsumo(2),new GameAction.DiscardTile(2,h.getDrawnTile()))
            : Arrays.asList(new GameAction.DeclareRiichi(2,t(Suit.MAN,2)));
        return PlayerView.Companion.fromGameState(s,2,actions,
            FuritenRules.INSTANCE.status(h,h.getClosedTiles(),true,true),h.getDiscardHistory());
    }
    private void click(int x,int y) {
        if (!screen.touchDown(x,720-y,0,0)) throw new AssertionError("Unhandled click: "+x+","+y);
    }
    @Override public void render() {
        game.render(); frame++;
        if (frame==3) click(720,160);
        if (frame==6) click(767,80); // 2 sou -> 3/6 sou waits
        if (frame==9) capture("discard-2s-wait-3s-6s.png");
        if (frame==10) click(1000,80); // drawn 6 sou -> 2/5 sou waits
        if (frame==13) {
            capture("discard-6s-wait-2s-5s.png");
            if(!session.submitted.isEmpty()) throw new AssertionError("Preview submitted a game action");
            Gdx.input.setCursorPosition(767,640);
        }
        if (frame==15) {
            capture("hover-2s-selected-6s.png");
            click(720,160);
            if(!session.submitted.equals(Arrays.asList(new GameAction.DeclareRiichi(2,t(Suit.SOU,6)))))
                throw new AssertionError("Wrong confirmed action: "+session.submitted);
            Gdx.input.setCursorPosition(10,10);
        }
        if (frame==18) { session.update(orphans()); click(720,160); }
        if (frame==21) click(1000,80);
        if (frame==24) capture("orphans-thirteen-waits.png");
        if (frame==27) click(1100,160);
        if(frame==30) {
            if(session.submitted.size()!=1) throw new AssertionError("Cancel submitted an action");
            session.update(orphans(true));
            if(!screen.canHumanTsumo() || screen.canHumanRon()) throw new AssertionError("Wrong furiten win buttons");
        }
        if(frame==33) { capture("furiten-tsumo-choice.png"); click(900,160); }
        if(frame==36) {
            if(session.submitted.size()!=2 || !(session.submitted.get(1) instanceof GameAction.DeclareTsumo))
                throw new AssertionError("Tsumo was not preserved");
            System.out.println("FURITEN_RENDER_PASS frames="+frame+" confirmations=1 tsumo=1 screenshots=5");
            Gdx.app.exit();
        }
    }
    private void capture(String name) {
        Pixmap image = ScreenUtils.getFrameBufferPixmap(0,0,1280,720);
        try { PixmapIO.writePNG(Gdx.files.absolute(System.getProperty("smoke.output")+"/"+name),image,-1,true); }
        finally { image.dispose(); }
    }
    @Override public void resize(int w,int h) { if(game!=null) game.resize(w,h); }
    @Override public void dispose() { if(game!=null) game.dispose(); }
    private static final class PreviewSession implements GameSession {
        private PlayerView snapshot;
        final List<GameAction> submitted=new ArrayList<>();
        private final List<Function1<? super SessionUpdate,Unit>> listeners=new ArrayList<>();
        void update(PlayerView v) {
            snapshot=v;
            for(Function1<? super SessionUpdate,Unit> l:new ArrayList<>(listeners)) l.invoke(new SessionUpdate(v,Collections.emptyList()));
        }
        public int getViewerSeat(){return 2;}
        public PlayerView view(){return snapshot;}
        public List<GameAction> legalActions(){return snapshot.getLegalActions();}
        public ActionResult start(){return new ActionResult.Accepted(Collections.emptyList());}
        public ActionResult submit(GameAction a){
            if(!legalActions().contains(a)) return new ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE);
            submitted.add(a); return new ActionResult.Accepted(Collections.emptyList());
        }
        public ActionResult nextRound(){return new ActionResult.Rejected(RejectionReason.WRONG_PHASE);}
        public AutoCloseable subscribe(Function1<? super SessionUpdate,Unit> l){listeners.add(l);return ()->listeners.remove(l);}
        public void close(){listeners.clear();}
    }
}
