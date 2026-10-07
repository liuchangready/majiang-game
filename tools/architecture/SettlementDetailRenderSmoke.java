import com.badlogic.gdx.ApplicationAdapter;
import com.badlogic.gdx.Gdx;
import com.badlogic.gdx.backends.lwjgl3.Lwjgl3Application;
import com.badlogic.gdx.backends.lwjgl3.Lwjgl3ApplicationConfiguration;
import com.badlogic.gdx.graphics.Color;
import com.badlogic.gdx.graphics.Pixmap;
import com.badlogic.gdx.graphics.PixmapIO;
import com.badlogic.gdx.graphics.g2d.BitmapFont;
import com.badlogic.gdx.graphics.g2d.GlyphLayout;
import com.badlogic.gdx.utils.Align;
import com.badlogic.gdx.utils.ScreenUtils;
import com.majiang.MahjongGame;
import com.majiang.client.api.*;
import com.majiang.model.PlayerHand;
import com.majiang.model.Suit;
import com.majiang.model.Tile;
import com.majiang.presentation.common.SettlementDetailRenderer;
import com.majiang.presentation.common.SettlementDetailRow;
import com.majiang.presentation.game.GameScreen;
import com.majiang.rules.*;
import com.majiang.server.mapping.EngineProtocolMapper;
import java.io.File;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CompletableFuture;
import kotlin.Pair;

/** Real GL/input acceptance for V4 settlement tabs, pagination, font bounds, and next round. */
public final class SettlementDetailRenderSmoke extends ApplicationAdapter {
    private MahjongGame game;
    private PreviewSession session;
    private GameScreen screen;
    private int frame,stage,shots;
    private SettlementDetailRenderer fontProbe;

    private static Tile t(Suit suit,int rank,int copy) { return new Tile(suit,rank,copy); }
    private static TileView v(SuitView suit,int rank,int copy) { return V4SmokeViews.tile(suit,rank,copy); }
    private static List<Tile> ts(Suit suit,String ranks) {
        List<Tile> out=new ArrayList<>(); int[] copies=new int[10];
        for(char c:ranks.toCharArray()) { int rank=c-'0'; out.add(t(suit,rank,copies[rank]++)); }
        return out;
    }
    private static ScoreBreakdownView toView(ScoreBreakdown breakdown) {
        return ProtocolViewMapper.INSTANCE.mapScoreBreakdown(
            EngineProtocolMapper.INSTANCE.toProtocolScoreBreakdown(breakdown));
    }

    private static ScoreBreakdownView realBreakdown() {
        PlayerHand hand=new PlayerHand();
        hand.getClosedTiles().addAll(ts(Suit.MAN,"23"));
        hand.getClosedTiles().addAll(ts(Suit.PIN,"234567"));
        hand.getClosedTiles().addAll(ts(Suit.SOU,"23488"));
        var context=new YakuEvaluator.WinContext(t(Suit.MAN,1,0),true,true,2,1,
            true,false,false,false,false,false,false,false,false);
        ScoreBreakdown domain=new WinningScoreEvaluator().explain(hand,context,
            Collections.singletonList(t(Suit.MAN,9,0)),Collections.singletonList(t(Suit.SOU,7,0)),
            false,2,2,null);
        if(domain==null)throw new AssertionError("Real score fixture did not produce a breakdown");
        return toView(domain);
    }

    private static ScoreBreakdownView crowded() {
        List<Pair<String,Integer>> yaku=Arrays.asList(
            new Pair<>("立直",1),new Pair<>("一发",1),new Pair<>("门前清自摸和",1),
            new Pair<>("平和",1),new Pair<>("断幺九",1),new Pair<>("一杯口",1),new Pair<>("清一色",6));
        List<DoraIndicatorHitView> indicators=new ArrayList<>(),ura=new ArrayList<>();
        List<TileView> indicatorTiles=new ArrayList<>(),uraTiles=new ArrayList<>();
        for(int i=0;i<5;i++) {
            List<TileView> a=Arrays.asList(v(SuitView.MAN,5,0),v(SuitView.MAN,5,3));
            List<TileView> u=Arrays.asList(v(SuitView.PIN,5,0),v(SuitView.PIN,5,3));
            indicators.add(new DoraIndicatorHitView(v(SuitView.MAN,4,i%4),v(SuitView.MAN,5,0),a));
            ura.add(new DoraIndicatorHitView(v(SuitView.PIN,4,i%4),v(SuitView.PIN,5,0),u));
            indicatorTiles.addAll(a); uraTiles.addAll(u);
        }
        var dora=new DoraBreakdownView(10,10,3,23,indicatorTiles,uraTiles,
            Arrays.asList(v(SuitView.MAN,5,3),v(SuitView.PIN,5,3),v(SuitView.SOU,5,3)),indicators,ura);
        var fu=new FuBreakdownView(Collections.singletonList(new FuLineView("平和自摸",20)),20,20,20,"平和自摸20符");
        var score=new ScoreResultView(20,35,2000,"数え役满",false,0,2000,null,"", "2000");
        return new ScoreBreakdownView(yaku,0,dora,fu,score,
            Collections.singletonList("自摸：庄家付16000、闲家各付8000"),600,2000,34600,true,false,true);
    }
    private static ScoreBreakdownView yakuman() {
        ScoreBreakdownView b=crowded();
        var score=new ScoreResultView(30,0,8000,"双倍役满",true,2,8000,null,"", "8000");
        return new ScoreBreakdownView(Arrays.asList(new Pair<>("大三元",0),new Pair<>("字一色",0)),
            2,b.getDora(),b.getFu(),score,
            Collections.singletonList("自摸：庄家付32000、闲家各付16000"),600,2000,66600,true,false,true);
    }
    private static ScoreBreakdownView manganRon() {
        PlayerHand hand=new PlayerHand();
        hand.getClosedTiles().addAll(ts(Suit.MAN,"2345"));
        hand.getClosedTiles().addAll(ts(Suit.PIN,"234"));
        hand.getClosedTiles().addAll(ts(Suit.SOU,"234777"));
        var context=new YakuEvaluator.WinContext(t(Suit.MAN,5,1),false,true,2,1,
            true,false,false,false,false,false,false,false,false);
        ScoreBreakdown domain=new WinningScoreEvaluator().explain(hand,context,
            Collections.emptyList(),Collections.emptyList(),false,2,2,1);
        if(domain==null || domain.getScore().getFu()!=40 || domain.getScore().getHan()!=4 || domain.getTotalPoints()!=10600)
            throw new AssertionError("Mangan hand fixture changed");
        return toView(domain);
    }

    public static void main(String[] args) {
        Lwjgl3ApplicationConfiguration c=new Lwjgl3ApplicationConfiguration();
        c.setWindowedMode(1280,720);c.setTitle("V4 settlement detail acceptance");
        c.setInitialVisible(false);c.disableAudio(true);c.setForegroundFPS(30);
        new Lwjgl3Application(new SettlementDetailRenderSmoke(),c);
    }
    @Override public void create() {
        game=new MahjongGame();game.create();session=new PreviewSession();
        game.changeScreen(new GameScreen(game.getUiAssets(),session,game,null));
        screen=(GameScreen)game.getScreen();fontProbe=new SettlementDetailRenderer();emit(realBreakdown());
    }
    private void checkTextBounds() {
        BitmapFont font=fontProbe.getOrInitChineseFont();
        if(font==null)throw new AssertionError("Chinese font failed to load");
        for(SettlementDetailRow row:screen.visibleSettlementRows()) {
            for(char c:row.getText().toCharArray())
                if(c>127&&font.getData().getGlyph(c)==null)throw new AssertionError("Missing glyph: "+c);
            float width=840-(row.getTiles().isEmpty()?0:row.getTiles().size()*27+26);
            GlyphLayout layout=new GlyphLayout(font,row.getText(),Color.WHITE,width,Align.left,true);
            if(layout.height>40)throw new AssertionError("Row cannot fit in 42px cell: "+row.getText());
        }
    }
    private void checkWrapBoundary() {
        BitmapFont font=fontProbe.getOrInitChineseFont();
        for(float width:new float[]{840,650,626}) {
            String value="测";
            while(new GlyphLayout(font,value).width<=width)value+="测";
            List<String> lines=fontProbe.wrapRowText(font,value,width);
            if(!String.join("",lines).equals(value))throw new AssertionError("Wrap lost text");
            for(String line:lines)
                if(new GlyphLayout(font,line).width>width)throw new AssertionError("Wrap exceeds reserved text area");
        }
    }
    private void emit(ScoreBreakdownView breakdown) {
        List<String> yaku=Arrays.asList("立直 1番","平和 1番");
        int han=breakdown==null?2:breakdown.getScore().getHan();
        int fu=breakdown==null?30:breakdown.getScore().getFu();
        int score=breakdown==null?2000:breakdown.getTotalPoints();
        Map<Integer,Integer> payments=Map.of(0,-1000,1,-500,2,2000,3,-500);
        List<Integer> scores=Arrays.asList(24000,24500,27000,24500);
        var detail=new SettlementDetailView("自摸",2,null,v(SuitView.MAN,1,0),yaku,
            han,fu,score,false,"",payments,scores,breakdown);
        List<GameEventView> events=Arrays.asList(new GameEventView.TsumoWin(12,2,v(SuitView.MAN,1,0),yaku,han,fu,score,breakdown),
            new GameEventView.ScoreSettled(12,payments,scores));
        session.publish(V4SmokeViews.player(2).phase(GamePhaseView.ROUND_END).scores(scores).build(),
            Collections.emptyList(),null,events,detail,null);
    }
    private void emitRon() {
        ScoreBreakdownView breakdown=manganRon();
        List<String> yaku=Arrays.asList("立直 1番","三色同顺 2番","断幺九 1番");
        Map<Integer,Integer> payments=Map.of(0,0,1,-8600,2,10600,3,0);
        List<Integer> scores=Arrays.asList(25000,16400,35600,25000);
        var detail=new SettlementDetailView("荣和",2,1,v(SuitView.MAN,5,1),yaku,
            4,40,8000,false,"",payments,scores,breakdown);
        List<GameEventView> events=Arrays.asList(new GameEventView.RonWin(12,2,1,v(SuitView.MAN,5,1),yaku,4,40,8000,breakdown),
            new GameEventView.ScoreSettled(12,payments,scores));
        session.publish(V4SmokeViews.player(2).phase(GamePhaseView.ROUND_END).scores(scores).build(),
            Collections.emptyList(),null,events,detail,null);
    }
    private void emitLegacyLong() {
        List<String> yaku=new ArrayList<>();
        for(int i=1;i<=14;i++)yaku.add("旧记录役种"+i+" 1番");
        var detail=new SettlementDetailView("自摸",2,null,v(SuitView.MAN,1,0),yaku,
            14,30,32000,false,"",Collections.emptyMap(),Collections.emptyList(),null);
        session.publish(V4SmokeViews.player(2).phase(GamePhaseView.ROUND_END).build(),
            Collections.emptyList(),null,Collections.singletonList(
                new GameEventView.TsumoWin(12,2,v(SuitView.MAN,1,0),yaku,14,30,32000,null)),detail,null);
    }
    private void click(int x,int y){screen.touchDown(x,720-y,0,0);}
    private void capture(String name) {
        checkTextBounds();File dir=new File(System.getProperty("smoke.output"));
        if(!dir.isDirectory()&&!dir.mkdirs())throw new AssertionError("Cannot create screenshot directory");
        Pixmap p=ScreenUtils.getFrameBufferPixmap(0,0,1280,720);
        try{PixmapIO.writePNG(Gdx.files.absolute(new File(dir,name+".png").getAbsolutePath()),p,-1,true);shots++;}
        finally{p.dispose();}
    }
    @Override public void render() {
        game.render();frame++;if(frame%3!=0)return;
        switch(stage) {
            case 0:capture("real-yaku");click(410,530);stage++;break;
            case 1:capture("real-dora");click(590,530);stage++;break;
            case 2:capture("real-calc-"+screen.getSettlementPage());if(screen.getSettlementPage()+1<screen.getSettlementPageCount())click(980,210);else{emit(crowded());stage++;}break;
            case 3:capture("crowded-yaku-"+screen.getSettlementPage());if(screen.getSettlementPage()+1<screen.getSettlementPageCount())click(980,210);else{click(410,530);stage++;}break;
            case 4:capture("crowded-dora-"+screen.getSettlementPage());if(screen.getSettlementPage()+1<screen.getSettlementPageCount())click(980,210);else{click(590,530);stage++;}break;
            case 5:capture("crowded-calc-"+screen.getSettlementPage());if(screen.getSettlementPage()+1<screen.getSettlementPageCount())click(980,210);else{emit(yakuman());stage++;}break;
            case 6:capture("yakuman-yaku");click(410,530);stage++;break;
            case 7:capture("yakuman-dora");click(590,530);stage++;break;
            case 8:capture("yakuman-calc-"+screen.getSettlementPage());if(screen.getSettlementPage()+1<screen.getSettlementPageCount())click(980,210);else{emit(null);stage++;}break;
            case 9:capture("legacy-fallback");emitRon();stage++;break;
            case 10:click(590,530);stage++;break;
            case 11:capture("mangan-ron-calc-"+screen.getSettlementPage());if(screen.getSettlementPage()+1<screen.getSettlementPageCount())click(980,210);else{click(410,530);stage++;}break;
            case 12:capture("zero-dora");checkWrapBoundary();emitLegacyLong();stage++;break;
            case 13:capture("legacy-long-"+screen.getSettlementPage());if(screen.getSettlementPage()+1<screen.getSettlementPageCount())click(850,275);else{if(screen.getSettlementPageCount()!=3)throw new AssertionError("Legacy entries were lost");if(!session.submitted.isEmpty())throw new AssertionError("Navigation submitted an action");click(640,165);stage++;}break;
            case 14:if(screen.isSettlementShown())throw new AssertionError("Next round blocked");System.out.println("SETTLEMENT_DETAIL_RENDER_PASS screenshots="+shots+" nextRounds="+session.nextRounds);Gdx.app.exit();stage++;break;
        }
        if(frame>450)throw new AssertionError("Paging did not finish");
    }
    @Override public void resize(int w,int h){if(game!=null)game.resize(w,h);}
    @Override public void dispose(){if(fontProbe!=null)fontProbe.dispose();if(game!=null)game.dispose();}

    private static final class PreviewSession extends V4SmokeSession {
        PreviewSession(){publish(V4SmokeViews.player(2).phase(GamePhaseView.ROUND_END).build());}
        @Override public CompletableFuture<Receipt> nextRound() {
            CompletableFuture<Receipt> result=super.nextRound();
            publish(V4SmokeViews.player(2).phase(GamePhaseView.STACKING).build(),
                Collections.emptyList(),null,Collections.singletonList(new GameEventView.StackingStarted(0)),null,null);
            return result;
        }
    }
}
