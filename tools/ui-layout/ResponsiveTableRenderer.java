import com.badlogic.gdx.Gdx;
import com.badlogic.gdx.graphics.Color;
import com.badlogic.gdx.graphics.GL20;
import com.badlogic.gdx.graphics.Pixmap;
import com.badlogic.gdx.graphics.Texture;
import com.badlogic.gdx.graphics.g2d.BitmapFont;
import com.badlogic.gdx.graphics.g2d.GlyphLayout;
import com.badlogic.gdx.graphics.g2d.NinePatch;
import com.badlogic.gdx.graphics.g2d.SpriteBatch;
import com.badlogic.gdx.graphics.g2d.TextureRegion;
import com.badlogic.gdx.graphics.g2d.freetype.FreeTypeFontGenerator;
import com.badlogic.gdx.graphics.glutils.ShapeRenderer;
import com.badlogic.gdx.math.Matrix4;
import com.badlogic.gdx.utils.Disposable;
import com.majiang.client.api.ShuffleTarget;
import com.majiang.presentation.common.render.TableVisualAssets;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.Collections;
import java.util.IdentityHashMap;

/** Reference renderer: real production PNGs + native FreeType glyphs + real GL draws. */
public final class ResponsiveTableRenderer implements Disposable {
    public record TextMeasure(String text,ResponsiveTableLayout.Rect allowed,float width,float height) {}
    final TableVisualAssets art=TableVisualAssets.Companion.load();
    final SpriteBatch batch=new SpriteBatch();
    final ShapeRenderer shape=new ShapeRenderer();
    final FreeTypeFontGenerator generator=new FreeTypeFontGenerator(Gdx.files.internal("fonts/NotoSansSC-Regular.ttf"));
    final Map<Integer,BitmapFont> fonts=new LinkedHashMap<>();
    final Map<String,Texture> tiles=new LinkedHashMap<>();
    final List<TextMeasure> textMeasures=new ArrayList<>();
    private final Texture whiteTexture=makeWhite();
    private final TextureRegion whitePixel=new TextureRegion(whiteTexture);
    private final GlyphLayout glyph=new GlyphLayout();
    private final Matrix4 projection=new Matrix4();
    private ResponsiveTableLayout.View view;
    private float ratio=1;
    private static final Color INK=Color.valueOf("fae8b5"), GOLD=Color.valueOf("f4d080"), JADE=Color.valueOf("a3e7b2");
    private static final String[] NAMES={"清一色（万）","清一色（条）","清一色（筒）","大三元","国士无双","大四喜"};
    private static final String[][] FACES={{"man1","man2","man3","man4","man5"},{"sou1","sou2","sou3","sou4","sou5"},{"pin1","pin2","pin3","pin4","pin5"},{"dragon1","dragon2","dragon3"},{"man1","man9","sou1","sou9","wind1"},{"wind1","wind2","wind3","wind4"}};
    private static Texture makeWhite() {
        Pixmap p=new Pixmap(1,1,Pixmap.Format.RGBA8888);p.setColor(Color.WHITE);p.fill();Texture t=new Texture(p);p.dispose();return t;
    }
    public ResponsiveTableRenderer() {
        // Four-to-eightfold shrink of intricate portrait/filigree artwork needs prefiltering.
        // This changes this standalone instance only; original PNGs and production code stay intact.
        Set<Texture> unique=Collections.newSetFromMap(new IdentityHashMap<>());
        unique.add(art.getEnvironment().getTexture());unique.add(art.getBoard().getTexture());unique.add(art.getCompass().getTexture());
        for(var r:art.getPortraitsByAbsoluteSeat().values())unique.add(r.getTexture());
        for(var r:art.getTargetScenes().values())unique.add(r.getTexture());
        unique.add(art.getPanelMain().getTexture());unique.add(art.getButtonPrimary().getTexture());
        for(var t:unique){t.bind();Gdx.gl.glGenerateMipmap(GL20.GL_TEXTURE_2D);t.setFilter(Texture.TextureFilter.MipMapLinearLinear,Texture.TextureFilter.Linear);}
    }

    public void begin(ResponsiveTableLayout.View view,float ratio) {
        this.view=view;this.ratio=ratio;
        projection.setToOrtho2D(0,0,view.width(),view.height());
        batch.setProjectionMatrix(projection);shape.setProjectionMatrix(projection);
    }
    private void draw(TextureRegion region,ResponsiveTableLayout.Rect physical) {
        batch.draw(region,physical.x(),view.height()-physical.bottom(),physical.w(),physical.h());
    }
    private void sprite(TextureRegion region,ResponsiveTableLayout.Rect local) { draw(region,view.screen(local)); }
    private void patch(NinePatch patch,ResponsiveTableLayout.Rect local) {
        // Draw in stage coordinates so fixed border dimensions scale with the rest of the UI.
        Matrix4 before=new Matrix4(batch.getTransformMatrix());
        batch.setTransformMatrix(new Matrix4().translate(view.stage().x(),view.height()-view.stage().bottom(),0).scale(view.scale(),view.scale(),1));
        patch.draw(batch,local.x(),720-local.bottom(),local.w(),local.h());
        batch.setTransformMatrix(before);
    }
    private void solid(ResponsiveTableLayout.Rect local,Color c) {
        Color prior=new Color(batch.getColor());batch.setColor(c);sprite(whitePixel,local);batch.setColor(prior);
    }
    private BitmapFont font(int logicalSize) {
        int px=Math.max(8,Math.round(logicalSize*view.scale()*ratio));
        BitmapFont font=fonts.computeIfAbsent(px,k->{
            var p=new FreeTypeFontGenerator.FreeTypeFontParameter();p.size=k;p.incremental=true;
            p.characters=FreeTypeFontGenerator.DEFAULT_CHARS;p.minFilter=Texture.TextureFilter.Linear;p.magFilter=Texture.TextureFilter.Linear;
            p.borderWidth=Math.max(.3f,k*.025f);p.borderColor=Color.WHITE;
            return generator.generateFont(p);
        });
        font.getData().setScale(1/ratio);return font;
    }
    private void text(String value,ResponsiveTableLayout.Rect local,int size,Color color) {
        var r=view.screen(local);BitmapFont font=font(size);glyph.setText(font,value);
        float embolden=size>=23?.45f*view.scale():0;
        textMeasures.add(new TextMeasure(value,r,glyph.width+embolden,glyph.height));
        float x=r.x()+(r.w()-glyph.width)/2,y=view.height()-(r.y()+(r.h()-glyph.height)/2);
        font.setColor(0.02f,0.025f,0.015f,0.92f);font.draw(batch,value,x+Math.max(0.5f,view.scale()),y-Math.max(0.5f,view.scale()));
        font.setColor(color);font.draw(batch,value,x,y);
        if(embolden>0)font.draw(batch,value,x+embolden,y);
    }
    private static ResponsiveTableLayout.Rect sub(ResponsiveTableLayout.Rect r,float x,float y,float w,float h) {
        return new ResponsiveTableLayout.Rect(r.x()+x,r.y()+y,w,h);
    }
    private void checkbox(ResponsiveTableLayout.Rect r,boolean on) {
        solid(r,Color.valueOf("e4ba61"));solid(sub(r,1.5f,1.5f,r.w()-3,r.h()-3),Color.valueOf("09261e"));
        if(on)text("✓",r,23,GOLD);
    }
    private void eye(float cx,float cy) {
        // The eye icon is code-native geometry; the textured card/board art remains bitmap artwork.
        var r=view.screen(new ResponsiveTableLayout.Rect(cx-16,cy-9,32,18));float s=view.scale();
        batch.end();shape.begin(ShapeRenderer.ShapeType.Filled);shape.setColor(GOLD);
        shape.ellipse(r.x(),view.height()-r.bottom(),r.w(),r.h(),48);
        shape.setColor(Color.valueOf("09271f"));shape.ellipse(r.x()+3*s,view.height()-r.bottom()+2.2f*s,r.w()-6*s,r.h()-4.4f*s,48);
        shape.setColor(GOLD);shape.circle(r.cx(),view.height()-r.cy(),4*s,24);shape.end();batch.begin();
    }
    private void tile(String name,float x,float y,float w,float h,float angle) {
        Texture texture=tiles.computeIfAbsent(name,k->{var t=new Texture(Gdx.files.internal("skins/wikipedia/"+k+".png"));t.setFilter(Texture.TextureFilter.Linear,Texture.TextureFilter.Linear);return t;});
        var r=view.screen(new ResponsiveTableLayout.Rect(x,y,w,h));
        batch.draw(new TextureRegion(texture),r.x(),view.height()-r.bottom(),r.w()/2,r.h()/2,r.w(),r.h(),1,1,-angle);
    }
    private void scissorStage(boolean on) {
        if(!on) {Gdx.gl.glDisable(GL20.GL_SCISSOR_TEST);return;}
        var r=view.stage();
        Gdx.gl.glEnable(GL20.GL_SCISSOR_TEST);
        Gdx.gl.glScissor(Math.round(r.x()*ratio),Math.round((view.height()-r.bottom())*ratio),Math.round(r.w()*ratio),Math.round(r.h()*ratio));
    }

    public void render(ResponsiveTableLayout.View view,ResponsiveTableLayout.Plan plan,ResponsiveTableLayout.Draft draft,float ratio,boolean bounds) {
        begin(view,ratio);textMeasures.clear();
        Gdx.gl.glDisable(GL20.GL_SCISSOR_TEST);Gdx.gl.glClearColor(.015f,.025f,.022f,1);Gdx.gl.glClear(GL20.GL_COLOR_BUFFER_BIT);
        batch.setColor(Color.WHITE);batch.begin();
        TextureRegion environment=art.getEnvironment();float coverScale=Math.max((float)view.width()/environment.getRegionWidth(),(float)view.height()/environment.getRegionHeight());
        float ew=environment.getRegionWidth()*coverScale,eh=environment.getRegionHeight()*coverScale;
        draw(environment,new ResponsiveTableLayout.Rect((view.width()-ew)/2,(view.height()-eh)/2,ew,eh));batch.end();
        if(!view.drawable())return;
        scissorStage(true);batch.begin();
        for(var region:plan.regions()) {
            var r=region.rect();String asset=region.asset();batch.setColor(Color.WHITE);
            switch(asset) {
                case "board" -> sprite(art.getBoard(),r);
                case "compass" -> {
                    sprite(art.getCompass(),r);
                    text("西",sub(r,r.w()*.38f,r.h()*.12f,r.w()*.24f,r.h()*.1f),19,GOLD);
                    text("南",sub(r,r.w()*.77f,r.h()*.43f,r.w()*.18f,r.h()*.1f),19,GOLD);
                    text("北",sub(r,r.w()*.05f,r.h()*.43f,r.w()*.18f,r.h()*.1f),19,GOLD);
                    if(plan.page()!=ResponsiveTableLayout.Page.PLAYING) {
                        text("东",sub(r,r.w()*.38f,r.h()*.75f,r.w()*.24f,r.h()*.1f),19,GOLD);
                        text("东一局",sub(r,r.w()*.22f,r.h()*.38f,r.w()*.56f,r.h()*.16f),26,GOLD);
                        text("准备开局",sub(r,r.w()*.18f,r.h()*.55f,r.w()*.64f,r.h()*.12f),19,INK);
                    } else {
                        text("东一局",sub(r,40,62,108,30),23,GOLD);
                        text("本场0 · 供托0",sub(r,28,92,132,24),16,INK);
                        text("余69",sub(r,42,116,104,21),16,JADE);
                        tile("sou3",r.x()+82,r.y()+142,24,31,0);
                    }
                }
                case "portrait" -> sprite(art.getPortraitsByAbsoluteSeat().get(region.seat()),r);
                case "plate" -> {
                    patch(art.getPanelPlate(),r);
                    String[] wind={"东","南","西","北"};
                    text("P"+region.seat()+" · "+wind[region.seat()]+(region.seat()==0?" · 庄":""),sub(r,7,5,r.w()-14,20),17,INK);
                    text("25000",sub(r,8,27,r.w()-16,26),21,GOLD);
                }
                case "tray" -> patch(art.getPanelHint(),r);
                case "hint" -> {patch(art.getPanelHint(),r);text("◆ 准备完成后发牌 ◆",sub(r,20,8,r.w()-40,34),23,INK);}
                case "panel" -> patch(art.getPanelMain(),r);
                case "inner" -> {
                    solid(r,Color.valueOf("cb9f47"));solid(sub(r,1,1,r.w()-2,r.h()-2),Color.valueOf("09271f"));
                }
                case "card" -> patch(art.getPanelCard(),r);
                case "primary","secondary" -> {
                    boolean primary=asset.equals("primary");
                    if(region.id().equals("confirm") && draft.target<0)batch.setColor(.58f,.61f,.57f,1);
                    if(region.id().equals("swap") || region.id().equals("sleeve"))batch.setColor(.65f,.68f,.63f,1);
                    patch(primary?art.getButtonPrimary():art.getButtonSecondary(),r);batch.setColor(Color.WHITE);
                }
                default -> {
                    if(asset.startsWith("target-")) {
                        int index=Integer.parseInt(asset.substring(7));
                        sprite(art.getTargetScenes().get(ShuffleTarget.values()[index]),r);
                        String[] fan=FACES[index];float start=r.cx()-fan.length*22/2f;
                        float lift=draft.target==index?4:0;
                        for(int j=0;j<fan.length;j++)tile(fan[j],start+j*22,r.y()+24-lift,31,46,(j-(fan.length-1)/2f)*7);
                        text(NAMES[index],sub(r,10,83,r.w()-20,27),21,INK);
                        if(draft.target==index) {
                            checkbox(sub(r,r.w()-33,9,24,24),true);
                            solid(sub(r,3,2,r.w()-6,2),GOLD);solid(sub(r,3,r.h()-4,r.w()-6,2),GOLD);
                            solid(sub(r,3,2,2,r.h()-4),GOLD);solid(sub(r,r.w()-5,2,2,r.h()-4),GOLD);
                        }
                    }
                }
            }
        }
        // Overlay dynamic labels after the nine-slice skins; no text is baked into PNG art.
        if(plan.page()==ResponsiveTableLayout.Page.ENTRY) {
            var r=plan.get("dock").rect();text("本局准备",sub(r,20,20,272,48),34,GOLD);
            text("剩余时间 10 秒",plan.get("timer").rect(),21,INK);
            r=plan.get("open-target").rect();tile("pin3",r.x()+18,r.y()+34,62,80,-12);
            text("偷天换日",sub(r,95,22,164,40),27,GOLD);text("◉ 30 点",sub(r,103,70,148,34),23,GOLD);
            text("选择后再挑牌型",sub(r,16,122,240,26),18,INK);
            r=plan.get("look").rect();eye(r.x()+27,r.cy());text("看破 · 10点",sub(r,54,16,162,32),21,INK);checkbox(sub(r,232,20,24,24),draft.look);
            r=plan.get("summary").rect();text(draft.look?"当前方案：看破":"当前方案：不使用千术",sub(r,6,5,260,26),19,JADE);text("预计消耗 "+draft.cost()+"点",sub(r,6,34,260,24),17,GOLD);
            r=plan.get("start").rect();text(draft.look?"确认看破 · 10点":"开始本局 · 0点",sub(r,16,10,r.w()-32,r.h()-20),25,INK);
            r=plan.get("dock").rect();text("剩余出千点 100点",sub(r,20,586,272,24),16,INK);
        } else if(plan.page()==ResponsiveTableLayout.Page.DRAWER) {
            var r=plan.get("drawer").rect();text("偷天换日 · 选择目标",sub(r,20,17,412,43),28,GOLD);
            text("影响起手及后续摸牌，不保证成型",sub(r,20,70,520,26),18,INK);
            text("‹ 返回",plan.get("back").rect(),19,INK);
            r=plan.get("look").rect();checkbox(sub(r,12,17,24,24),draft.look);eye(r.x()+66,r.cy());text("看破 · 10点",sub(r,91,14,150,30),20,INK);
            text("本次消耗 "+draft.cost()+"点",plan.get("cost").rect(),22,GOLD);
            text("直接开局 · 0点",plan.get("skip").rect(),23,INK);
            text(draft.target<0?"先选择一个目标":"确认施术 · "+draft.cost()+"点",plan.get("confirm").rect(),23,INK);
        } else if(plan.page()==ResponsiveTableLayout.Page.RECEIPT) {
            var r=plan.get("receipt").rect();text("方案已确认",sub(r,20,26,264,48),32,GOLD);
            text(draft.target>=0?NAMES[draft.target]+" · "+draft.cost()+"点":"不使用千术 · 0点",sub(r,16,103,272,36),23,JADE);
            text("等待其他玩家完成准备",sub(r,16,166,272,28),19,INK);
            text("准备完成后发牌",sub(r,16,210,272,26),17,INK);
        } else {
            String[] hand={"man1","man2","man3","pin3","pin4","pin5","sou2","sou3","sou4","dragon1","wind1","wind1","wind2","wind3"};
            for(int i=0;i<hand.length;i++)tile(hand[i],220+i*58,610,52,64,0);
            for(int i=0;i<13;i++) {
                solid(new ResponsiveTableLayout.Rect(446+i*30,18,26,36),Color.valueOf("dfba6a"));solid(new ResponsiveTableLayout.Rect(447+i*30,19,24,34),Color.valueOf("125645"));
                solid(new ResponsiveTableLayout.Rect(246,203+i*26,31,22),Color.valueOf("dfba6a"));solid(new ResponsiveTableLayout.Rect(247,204+i*26,29,20),Color.valueOf("125645"));
                solid(new ResponsiveTableLayout.Rect(990,203+i*26,31,22),Color.valueOf("dfba6a"));solid(new ResponsiveTableLayout.Rect(991,204+i*26,29,20),Color.valueOf("125645"));
            }
            Map<String,String> names=Map.of("river","牌河提示","mark","落汗 · 10","swap","偷梁换柱 · 10","sleeve","袖里藏花 · 10","sort","理牌：关","auto","托管");
            for(var c:plan.controls())text(names.get(c.id()),c.rect(),19,INK);
        }
        batch.end();scissorStage(false);
        if(bounds)drawBounds(plan);
    }

    public void drawBounds(ResponsiveTableLayout.Plan plan) {
        shape.begin(ShapeRenderer.ShapeType.Line);
        shape.setColor(Color.CYAN);var stage=view.stage();shape.rect(stage.x(),view.height()-stage.bottom(),stage.w(),stage.h());
        for(var r:plan.regions()) {
            if(r.asset().equals("inner") || r.id().equals("summary"))continue;
            var box=view.screen(r.rect());shape.setColor(r.interactive()?Color.YELLOW:r.asset().equals("portrait")?Color.PINK:Color.CYAN);
            shape.rect(box.x(),view.height()-box.bottom(),box.w(),box.h());
        }
        shape.end();
        batch.begin();
        var before=view;view=ResponsiveTableLayout.view(before.width(),before.height(),ResponsiveTableLayout.Insets.ZERO);
        // Diagnostic label is outside the product UI semantics, present only in *-bounds.png.
        float oldRatio=ratio;BitmapFont f=font(16);f.setColor(Color.WHITE);
        f.draw(batch,"STAGE / SAFE AREA | yellow: input | pink: portrait | cyan: art",12,view.height()-12);
        view=before;ratio=oldRatio;batch.end();
    }
    /** GPU ID pass follows the exact same projection/clip, independent of CPU hit testing. */
    public void renderPick(ResponsiveTableLayout.View view,ResponsiveTableLayout.Plan plan,float ratio) {
        begin(view,ratio);Gdx.gl.glDisable(GL20.GL_SCISSOR_TEST);Gdx.gl.glClearColor(0,0,0,0);Gdx.gl.glClear(GL20.GL_COLOR_BUFFER_BIT);
        scissorStage(true);batch.disableBlending();batch.begin();
        List<ResponsiveTableLayout.Region> controls=plan.controls();
        for(int i=0;i<controls.size();i++)solid(controls.get(i).rect(),new Color((i+1)/255f,0,0,1));
        batch.end();batch.enableBlending();scissorStage(false);
    }
    public TextureRegion imageFor(ResponsiveTableLayout.Region r) {
        return switch(r.asset()) {case "board"->art.getBoard();case "compass"->art.getCompass();case "portrait"->art.getPortraitsByAbsoluteSeat().get(r.seat());default->null;};
    }
    /** Isolated image render for readback of its alpha silhouette. */
    public void renderImageMask(ResponsiveTableLayout.View view,ResponsiveTableLayout.Region r,float ratio) {
        begin(view,ratio);Gdx.gl.glDisable(GL20.GL_SCISSOR_TEST);Gdx.gl.glClearColor(0,0,0,0);Gdx.gl.glClear(GL20.GL_COLOR_BUFFER_BIT);
        batch.setBlendFunctionSeparate(GL20.GL_SRC_ALPHA,GL20.GL_ONE_MINUS_SRC_ALPHA,GL20.GL_ONE,GL20.GL_ONE_MINUS_SRC_ALPHA);
        batch.begin();batch.setColor(Color.WHITE);sprite(imageFor(r),r.rect());batch.end();
        batch.setBlendFunction(GL20.GL_SRC_ALPHA,GL20.GL_ONE_MINUS_SRC_ALPHA);
    }
    @Override public void dispose() {for(var f:fonts.values())f.dispose();for(var t:tiles.values())t.dispose();whiteTexture.dispose();generator.dispose();art.dispose();batch.dispose();shape.dispose();}
}
