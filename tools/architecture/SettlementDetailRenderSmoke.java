import com.badlogic.gdx.*;
import com.badlogic.gdx.backends.lwjgl3.*;
import com.badlogic.gdx.graphics.*;
import com.badlogic.gdx.graphics.g2d.*;
import com.badlogic.gdx.utils.Align;
import com.badlogic.gdx.utils.ScreenUtils;
import com.majiang.MahjongGame;
import com.majiang.event.GameEvent;
import com.majiang.model.*;
import com.majiang.render.*;
import com.majiang.rules.*;
import com.majiang.screen.*;
import com.majiang.session.*;
import com.majiang.state.*;
import java.util.*;
import kotlin.Pair;
import kotlin.Unit;
import kotlin.jvm.functions.Function1;

/** Codex-owned real OpenGL and real input acceptance, independent from Anti's implementation. */
public final class SettlementDetailRenderSmoke extends ApplicationAdapter {
    private MahjongGame game; private PreviewSession session; private GameScreen screen; private int frame, stage, shots;
    private SettlementDetailRenderer fontProbe;
    static Tile t(Suit s,int r,int c){return new Tile(s,r,c);}
    static List<Tile> ts(Suit s,String ranks){Map<Integer,Integer> n=new HashMap<>();List<Tile> out=new ArrayList<>();for(char c:ranks.toCharArray()){int r=c-'0',i=n.getOrDefault(r,0);out.add(t(s,r,i));n.put(r,i+1);}return out;}
    static ScoreBreakdown realBreakdown(){
        PlayerHand h=new PlayerHand();h.getClosedTiles().addAll(ts(Suit.MAN,"23"));h.getClosedTiles().addAll(ts(Suit.PIN,"234567"));h.getClosedTiles().addAll(ts(Suit.SOU,"23488"));
        YakuEvaluator.WinContext c=new YakuEvaluator.WinContext(t(Suit.MAN,1,0),true,true,2,1,true,false,false,false,false,false,false,false,false);
        return new WinningScoreEvaluator().explain(h,c,Arrays.asList(t(Suit.MAN,9,0)),Arrays.asList(t(Suit.SOU,7,0)),false,2,2,null);
    }
    static ScoreBreakdown crowded(){
        List<Pair<String,Integer>> ys=Arrays.asList(new Pair<>("立直",1),new Pair<>("一发",1),new Pair<>("门前清自摸和",1),new Pair<>("平和",1),new Pair<>("断幺九",1),new Pair<>("一杯口",1),new Pair<>("清一色",6));
        List<DoraIndicatorHit> a=new ArrayList<>(),u=new ArrayList<>();List<Tile> ah=new ArrayList<>(),uh=new ArrayList<>();
        for(int i=0;i<5;i++){List<Tile> hits=Arrays.asList(t(Suit.MAN,5,0),t(Suit.MAN,5,3));a.add(new DoraIndicatorHit(t(Suit.MAN,4,i%4),t(Suit.MAN,5,0),hits));u.add(new DoraIndicatorHit(t(Suit.PIN,4,i%4),t(Suit.PIN,5,0),Arrays.asList(t(Suit.PIN,5,0),t(Suit.PIN,5,3))));ah.addAll(hits);uh.addAll(u.get(i).getTiles());}
        DoraBreakdown d=new DoraBreakdown(10,10,3,23,ah,uh,Arrays.asList(t(Suit.MAN,5,3),t(Suit.PIN,5,3),t(Suit.SOU,5,3)),a,u);
        FuBreakdown f=new FuBreakdown(Arrays.asList(new FuLine("平和自摸",20)),20,20,20,"平和自摸20符");
        return new ScoreBreakdown(ys,0,d,f,new ScoreCalculator().calculate(20,35,0),Arrays.asList("自摸：庄家付16000、闲家各付8000"),600,2000,34600,true,false,true);
    }
    static ScoreBreakdown yakuman(){ScoreBreakdown b=crowded();return new ScoreBreakdown(Arrays.asList(new Pair<>("大三元",0),new Pair<>("字一色",0)),2,b.getDora(),b.getFu(),new ScoreCalculator().calculate(30,0,2),Arrays.asList("自摸：庄家付32000、闲家各付16000"),600,2000,66600,true,false,true);}
    static ScoreBreakdown manganRon(){
        PlayerHand h=new PlayerHand();h.getClosedTiles().addAll(ts(Suit.MAN,"2345"));h.getClosedTiles().addAll(ts(Suit.PIN,"234"));h.getClosedTiles().addAll(ts(Suit.SOU,"234777"));
        YakuEvaluator.WinContext c=new YakuEvaluator.WinContext(t(Suit.MAN,5,1),false,true,2,1,true,false,false,false,false,false,false,false,false);
        ScoreBreakdown b=new WinningScoreEvaluator().explain(h,c,Collections.emptyList(),Collections.emptyList(),false,2,2,1);
        if(b.getScore().getFu()!=40 || b.getScore().getHan()!=4 || b.getTotalPoints()!=10600)throw new AssertionError("Mangan hand fixture");return b;
    }
    static PlayerView view(){GameState s=new GameState();s.setPhase(GamePhase.ROUND_END);return PlayerView.Companion.fromGameState(s,2,Collections.emptyList(),new FuritenStatus(false,false,false),Collections.emptyList());}
    public static void main(String[] args){Lwjgl3ApplicationConfiguration c=new Lwjgl3ApplicationConfiguration();c.setWindowedMode(1280,720);c.setTitle("Settlement detail acceptance");c.setInitialVisible(false);c.disableAudio(true);c.setForegroundFPS(30);new Lwjgl3Application(new SettlementDetailRenderSmoke(),c);}
    @Override public void create(){game=new MahjongGame(new SkinManager(new InMemorySkinPreferences(SkinId.WIKIPEDIA)));game.create();session=new PreviewSession();game.launchGame(session);screen=(GameScreen)game.getScreen();screen.startGame();fontProbe=new SettlementDetailRenderer();emit(realBreakdown());}
    void checkTextBounds(){
        BitmapFont f=fontProbe.getOrInitChineseFont();if(f==null)throw new AssertionError("Chinese font failed to load");
        for(SettlementDetailRow row:screen.visibleSettlementRows()){
            for(char c:row.getText().toCharArray())if(c>127&&f.getData().getGlyph(c)==null)throw new AssertionError("Missing glyph: "+c);
            float width=840-(row.getTiles().isEmpty()?0:row.getTiles().size()*27+26);
            GlyphLayout l=new GlyphLayout(f,row.getText(),Color.WHITE,width,Align.left,true);
            if(l.height>40)throw new AssertionError("Row cannot fit in 42px cell: "+row.getText()+" height="+l.height);
        }
    }
    void checkWrapBoundary(){
        BitmapFont font=fontProbe.getOrInitChineseFont();
        try {
            java.lang.reflect.Method method=SettlementDetailRenderer.class.getDeclaredMethod("wrapRowText",BitmapFont.class,String.class,float.class);method.setAccessible(true);
            for(float width:new float[]{840,650,626}){
                String value="测";while(new GlyphLayout(font,value).width<=width)value+="测";
                @SuppressWarnings("unchecked") List<String> lines=(List<String>)method.invoke(fontProbe,font,value,width);
                if(!String.join("",lines).equals(value))throw new AssertionError("Wrap lost text");
                for(String line:lines)if(new GlyphLayout(font,line).width>width)throw new AssertionError("Wrap exceeds reserved text area");
            }
        } catch(ReflectiveOperationException e){throw new AssertionError("Last-character wrapping boundary",e);}
    }
    void emit(ScoreBreakdown b){
        int winner=b==null?2000:b.getTotalPoints();
        Pair<Integer,Integer> pay=b==null?new Pair<>(1000,500):b.getScore().nonDealerTsumoPayment();
        int honba=b==null?0:b.getHonbaAddition()/3;
        Map<Integer,Integer> payments=Map.of(0,-pay.getFirst()-honba,1,-pay.getSecond()-honba,2,winner,3,-pay.getSecond()-honba);
        int[] scores=new int[4];for(int i=0;i<4;i++)scores[i]=25000+payments.get(i);
        session.update(Arrays.asList(new GameEvent.TsumoWin(12,2,t(Suit.MAN,1,0),Arrays.asList("立直 1番","平和 1番"),b==null?2:b.getScore().getHan(),b==null?30:b.getScore().getFu(),b==null?2000:b.getScore().totalPoints(false,true),b),new GameEvent.ScoreSettled(12,payments,scores)));
    }
    void click(int x,int y){screen.touchDown(x,720-y,0,0);}
    void emitRon(){ScoreBreakdown b=manganRon();session.update(Arrays.asList(new GameEvent.RonWin(12,2,1,t(Suit.MAN,5,1),Arrays.asList("立直 1番","三色同顺 2番","断幺九 1番"),4,40,8000,b),new GameEvent.ScoreSettled(12,Map.of(0,0,1,-8600,2,10600,3,0),new int[]{25000,16400,35600,25000})));}
    void emitLegacyLong(){List<String> lines=new ArrayList<>();for(int i=1;i<=14;i++)lines.add("旧记录役种"+i+" 1番");session.update(Arrays.asList(new GameEvent.TsumoWin(12,2,t(Suit.MAN,1,0),lines,14,30,32000,null)));}
    void capture(String name){checkTextBounds();Pixmap p=ScreenUtils.getFrameBufferPixmap(0,0,1280,720);try{PixmapIO.writePNG(Gdx.files.absolute(System.getProperty("smoke.output")+"/"+name+".png"),p,-1,true);shots++;}finally{p.dispose();}}
    @Override public void render(){game.render();frame++;if(frame%3!=0)return;
        switch(stage){
            case 0:capture("real-yaku");click(410,530);stage++;break;
            case 1:capture("real-dora");click(590,530);stage++;break;
            case 2:capture("real-calc-"+screen.getSettlementPage());if(screen.getSettlementPage()+1<screen.getSettlementPageCount())click(980,210);else{emit(crowded());stage++;}break;
            case 3:capture("crowded-yaku-"+screen.getSettlementPage());if(screen.getSettlementPage()+1<screen.getSettlementPageCount())click(980,210);else{click(410,530);stage++;}break;
            case 4:capture("crowded-dora-"+screen.getSettlementPage());if(screen.getSettlementPage()+1<screen.getSettlementPageCount())click(980,210);else{click(590,530);stage++;}break;
            case 5:capture("crowded-calc-"+screen.getSettlementPage());if(screen.getSettlementPage()+1<screen.getSettlementPageCount())click(980,210);else{emit(yakuman());stage++;}break;
            case 6:capture("yakuman-yaku");click(410,530);stage++;break;
            case 7:capture("yakuman-dora");click(590,530);stage++;break;
            case 8:capture("yakuman-calc-"+screen.getSettlementPage());if(screen.getSettlementPage()+1<screen.getSettlementPageCount())click(980,210);else{emit(null);stage++;}break;
            case 9:capture("legacy-fallback");emitRon();click(590,530);stage++;break;
            case 10:capture("mangan-ron-calc-"+screen.getSettlementPage());if(screen.getSettlementPage()+1<screen.getSettlementPageCount())click(980,210);else{click(410,530);stage++;}break;
            case 11:capture("zero-dora");checkWrapBoundary();emitLegacyLong();stage++;break;
            case 12:capture("legacy-long-"+screen.getSettlementPage());if(screen.getSettlementPage()+1<screen.getSettlementPageCount())click(850,275);else{if(screen.getSettlementPageCount()!=3)throw new AssertionError("Legacy entries were lost");if(!session.submitted.isEmpty())throw new AssertionError("Navigation submitted domain action");click(640,165);if(screen.isSettlementShown())throw new AssertionError("Next round blocked");System.out.println("SETTLEMENT_DETAIL_RENDER_PASS screenshots="+shots+" nextRounds="+session.nextRounds);Gdx.app.exit();stage++;}break;
        }
        if(frame>450)throw new AssertionError("Paging did not finish");
    }
    @Override public void resize(int w,int h){if(game!=null)game.resize(w,h);}
    @Override public void dispose(){if(fontProbe!=null)fontProbe.dispose();if(game!=null)game.dispose();}
    private static final class PreviewSession implements GameSession {
        private final PlayerView snapshot=SettlementDetailRenderSmoke.view();final List<GameAction> submitted=new ArrayList<>();private final List<Function1<? super SessionUpdate,Unit>> listeners=new ArrayList<>();int nextRounds;
        void update(List<GameEvent> events){for(Function1<? super SessionUpdate,Unit> l:new ArrayList<>(listeners))l.invoke(new SessionUpdate(snapshot,events));}
        public int getViewerSeat(){return 2;}public PlayerView view(){return snapshot;}public List<GameAction> legalActions(){return snapshot.getLegalActions();}
        public ActionResult start(){return new ActionResult.Accepted(Collections.emptyList());}
        public ActionResult submit(GameAction a){submitted.add(a);return new ActionResult.Accepted(Collections.emptyList());}
        public ActionResult nextRound(){nextRounds++;return new ActionResult.Accepted(Collections.emptyList());}
        public AutoCloseable subscribe(Function1<? super SessionUpdate,Unit> l){listeners.add(l);return ()->listeners.remove(l);}
        public void close(){listeners.clear();}
    }
}
