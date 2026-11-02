import com.badlogic.gdx.ApplicationListener;
import com.badlogic.gdx.Gdx;
import com.badlogic.gdx.InputAdapter;
import com.badlogic.gdx.backends.lwjgl3.Lwjgl3Application;
import com.badlogic.gdx.backends.lwjgl3.Lwjgl3ApplicationConfiguration;
import com.badlogic.gdx.graphics.GL20;
import com.badlogic.gdx.graphics.Pixmap;
import com.badlogic.gdx.graphics.PixmapIO;
import com.badlogic.gdx.graphics.g2d.TextureRegion;
import com.badlogic.gdx.graphics.glutils.FrameBuffer;
import com.badlogic.gdx.graphics.glutils.HdpiMode;
import com.badlogic.gdx.utils.ScreenUtils;

import java.io.File;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * Retained executable design / validation example. Uses a real resizable GLFW
 * window and GL framebuffer readback, not generated mock screenshot images.
 * Fixed sample names/hands are layout fixtures, NOT production snapshots.
 */
public final class ResponsiveTableDesignTest implements ApplicationListener {
    record Case(String id,int w,int h,ResponsiveTableLayout.Page page,float animation,float ratio,ResponsiveTableLayout.Insets insets) {}
    final List<Case> cases=new ArrayList<>();
    final List<Object> results=new ArrayList<>(),alphaResults=new ArrayList<>(),resizeResults=new ArrayList<>();
    final File out=new File(System.getProperty("design.out","../doc/ui/round-preparation/responsive-validation"));
    ResponsiveTableRenderer renderer;
    ResponsiveTableLayout.Draft draft=new ResponsiveTableLayout.Draft();
    Case current;
    int index=0,waitFrames=0,assertions=0,pickSamples=0,failures=0;
    boolean resizePhase=false,finished=false;
    long start=System.nanoTime();
    final int[][] resizePath={{1280,720},{1160,700},{1024,768},{960,540},{1440,810},{1600,600},{1280,720}};

    public static void main(String[] args) {
        var cfg=new Lwjgl3ApplicationConfiguration();cfg.setTitle("Crazy Animal Mahjong | responsive design validation");
        cfg.setWindowedMode(1280,720);cfg.setResizable(true);cfg.setForegroundFPS(60);cfg.useVsync(false);cfg.disableAudio(true);
        cfg.setHdpiMode(HdpiMode.Logical);
        cfg.setInitialVisible(Boolean.parseBoolean(System.getProperty("design.visible","false")));
        new Lwjgl3Application(new ResponsiveTableDesignTest(),cfg);
    }
    @Override public void create() {
        out.mkdirs();renderer=new ResponsiveTableRenderer();
        add("01-entry-1280x720",1280,720,ResponsiveTableLayout.Page.ENTRY);
        add("02-drawer-1280x720",1280,720,ResponsiveTableLayout.Page.DRAWER);
        add("02b-drawer-unselected-1280x720",1280,720,ResponsiveTableLayout.Page.DRAWER);
        add("03-receipt-1280x720",1280,720,ResponsiveTableLayout.Page.RECEIPT);
        add("04-playing-1280x720",1280,720,ResponsiveTableLayout.Page.PLAYING);
        for(int[] size:new int[][]{{960,540},{1920,1080},{1024,768},{1280,800},{1600,600},{2560,1080},{3440,1440}}) {
            String n=size[0]+"x"+size[1];add("drawer-"+n,size[0],size[1],ResponsiveTableLayout.Page.DRAWER);add("playing-"+n,size[0],size[1],ResponsiveTableLayout.Page.PLAYING);
        }
        add("robustness-800x450",800,450,ResponsiveTableLayout.Page.DRAWER);
        cases.add(new Case("drawer-animation-1280x720",1280,720,ResponsiveTableLayout.Page.DRAWER,.35f,0,ResponsiveTableLayout.Insets.ZERO));
        cases.add(new Case("drawer-animation-resized-960x540",960,540,ResponsiveTableLayout.Page.DRAWER,.35f,0,ResponsiveTableLayout.Insets.ZERO));
        cases.add(new Case("drawer-animation-resized-1600x600",1600,600,ResponsiveTableLayout.Page.DRAWER,.35f,0,ResponsiveTableLayout.Insets.ZERO));
        cases.add(new Case("entry-safe-insets-1280x720",1280,720,ResponsiveTableLayout.Page.ENTRY,1,0,new ResponsiveTableLayout.Insets(48,16,28,24)));
        cases.add(new Case("drawer-960x540-fb2x",960,540,ResponsiveTableLayout.Page.DRAWER,1,2,ResponsiveTableLayout.Insets.ZERO));
        cases.add(new Case("drawer-1280x720-fb1_5x",1280,720,ResponsiveTableLayout.Page.DRAWER,1,1.5f,ResponsiveTableLayout.Insets.ZERO));
        checkGoldens();
        Gdx.input.setInputProcessor(new InputAdapter(){
            @Override public boolean touchDown(int x,int y,int pointer,int button) {var v=liveView();draft.down(v,ResponsiveTableLayout.plan(draft.page,1),x,y);return true;}
            @Override public boolean touchUp(int x,int y,int pointer,int button) {var v=liveView();draft.up(v,ResponsiveTableLayout.plan(draft.page,1),x,y);return true;}
        });
        request(cases.get(0));
    }
    void add(String id,int w,int h,ResponsiveTableLayout.Page page) {cases.add(new Case(id+(w>3000?"-offscreen":""),w,h,page,1,w>3000?1:0,ResponsiveTableLayout.Insets.ZERO));}
    int hostWidth(Case c) {return c.w>3000?1280:c.w;}
    int hostHeight(Case c) {return c.w>3000?720:c.h;}
    ResponsiveTableLayout.View liveView(){return ResponsiveTableLayout.view(Gdx.graphics.getWidth(),Gdx.graphics.getHeight(),ResponsiveTableLayout.Insets.ZERO);}
    void request(Case c) {
        current=c;waitFrames=0;draft.resized();
        Gdx.graphics.setWindowedMode(hostWidth(c),hostHeight(c));
    }
    @Override public void resize(int width,int height) {if(draft!=null)draft.resized();}
    void check(boolean ok,String message) {assertions++;if(!ok)throw new AssertionError(message);}
    void checkGoldens() {
        var v=ResponsiveTableLayout.view(1024,768,ResponsiveTableLayout.Insets.ZERO);
        check(close(v.scale(),.8f)&&close(v.stage().x(),0)&&close(v.stage().y(),96)&&close(v.stage().w(),1024)&&close(v.stage().h(),576),"4:3 stage golden");
        v=ResponsiveTableLayout.view(1600,600,ResponsiveTableLayout.Insets.ZERO);
        check(close(v.scale(),5f/6)&&close(v.stage().x(),800f/3)&&close(v.stage().y(),0),"ultrawide stage golden");
        v=ResponsiveTableLayout.view(2560,1080,ResponsiveTableLayout.Insets.ZERO);
        check(close(v.scale(),1.5f)&&close(v.stage().x(),320),"21:9 stage golden");
        check(!ResponsiveTableLayout.view(0,0,ResponsiveTableLayout.Insets.ZERO).drawable(),"zero size safely suspended");
        check(!ResponsiveTableLayout.view(0,720,ResponsiveTableLayout.Insets.ZERO).drawable(),"zero width safely suspended");
        check(!ResponsiveTableLayout.view(1280,0,ResponsiveTableLayout.Insets.ZERO).drawable(),"zero height safely suspended");
    }
    static boolean close(float a,float b){return Math.abs(a-b)<.02f;}
    @Override public void render() {
        if(finished)return;
        try {
            if((System.nanoTime()-start)/1e9>240)throw new AssertionError("design test exceeded 240s");
            if(Gdx.graphics.getWidth()!=hostWidth(current) || Gdx.graphics.getHeight()!=hostHeight(current)) {
                if(++waitFrames>120)throw new AssertionError("Window size not honored: "+current.w+"x"+current.h+" actual "+Gdx.graphics.getWidth()+"x"+Gdx.graphics.getHeight());
                return;
            }
            if(++waitFrames<3)return;
            if(resizePhase){runResizeStep();return;}
            runCase(current);
            if(++index<cases.size())request(cases.get(index));
            else {
                resizePhase=true;index=0;draft=new ResponsiveTableLayout.Draft();draft.page=ResponsiveTableLayout.Page.DRAWER;draft.target=4;draft.look=true;
                request(new Case("resize-00",resizePath[0][0],resizePath[0][1],ResponsiveTableLayout.Page.DRAWER,1,0,ResponsiveTableLayout.Insets.ZERO));
            }
        } catch(Throwable error) {
            failures++;error.printStackTrace();
            try {Files.writeString(new File(out,"FAILURE.txt").toPath(),error.toString(),StandardCharsets.UTF_8);saveReport(false);}catch(Exception ignored){}
            finished=true;Gdx.app.exit();
            throw new RuntimeException(error);
        }
    }
    void runCase(Case c) throws Exception {
        var view=ResponsiveTableLayout.view(c.w,c.h,c.insets);
        var plan=ResponsiveTableLayout.plan(c.page,c.animation);
        var sample=new ResponsiveTableLayout.Draft();sample.page=c.page;
        if(c.page==ResponsiveTableLayout.Page.DRAWER&&!c.id.contains("unselected")){sample.target=0;sample.look=true;}
        Map<String,Object> item=new LinkedHashMap<>();item.put("id",c.id);item.put("designSurface",List.of(c.w,c.h));item.put("hostWindow",List.of(Gdx.graphics.getWidth(),Gdx.graphics.getHeight()));item.put("renderTarget",c.ratio>0?"offscreen FBO":"native GLFW framebuffer");item.put("stage",rect(view.stage()));item.put("scale",view.scale());item.put("page",c.page.name());
        List<Object> regions=new ArrayList<>();
        for(var r:plan.regions())regions.add(Map.of("id",r.id(),"local",rect(r.rect()),"screen",rect(view.screen(r.rect())),"hit",r.interactive()));
        item.put("regions",regions);
        verifyGeometry(view,plan);
        FrameBuffer fbo=null;
        float ratio=c.ratio>0?c.ratio:(float)Gdx.graphics.getBackBufferWidth()/c.w;
        int pw=Math.round(c.w*ratio),ph=Math.round(c.h*ratio);
        if(c.ratio>0){fbo=new FrameBuffer(Pixmap.Format.RGBA8888,pw,ph,false);fbo.begin();}
        else Gdx.gl.glViewport(0,0,pw,ph);
        renderer.render(view,plan,sample,ratio,false);verifyText(c.id);
        capture(c.id,pw,ph);
        if(c.id.equals("02-drawer-1280x720") || c.id.equals("drawer-1024x768") || c.id.equals("drawer-1600x600") || c.id.equals("entry-safe-insets-1280x720")){
            renderer.render(view,plan,sample,ratio,true);capture(c.id+"-bounds",pw,ph);
        }
        if(fbo!=null){fbo.end();fbo.dispose();}
        item.put("framebuffer",List.of(pw,ph));item.put("pixelRatio",ratio);item.put("textChecks",renderer.textMeasures.size());
        verifyGpuPick(view,plan,ratio);
        if(c.id.equals("02-drawer-1280x720") || c.id.equals("drawer-1600x600") || c.id.equals("drawer-960x540-fb2x"))verifyAlphaBounds(view,plan,ratio,c.id);
        results.add(item);System.out.println("CAPTURE PASS "+c.id+" stage="+rect(view.stage())+" framebuffer="+pw+"x"+ph);
    }
    void verifyGeometry(ResponsiveTableLayout.View view,ResponsiveTableLayout.Plan plan) {
        float tolerance=.1f;
        var stage=view.stage();check(stage.x()>=view.insets().left()-tolerance&&stage.y()>=view.insets().top()-tolerance,"stage respects safe top/left");
        check(stage.right()<=view.width()-view.insets().right()+tolerance&&stage.bottom()<=view.height()-view.insets().bottom()+tolerance,"stage respects safe bottom/right");
        check(close(stage.w()/stage.h(),16f/9),"stage keeps aspect");
        for(var a:plan.controls()) {
            var r=a.rect();check(r.x()>=0&&r.y()>=0&&r.right()<=1280&&r.bottom()<=720,"control in stage: "+a.id());
            for(var b:plan.controls())if(!a.id().equals(b.id()))check(!r.overlaps(b.rect()),"interactive overlap: "+a.id()+"/"+b.id());
        }
        if(plan.progress()==1) {
            for(var a:plan.regions()) {
                var r=a.rect();check(r.x()>=0&&r.y()>=0&&r.right()<=1280&&r.bottom()<=720,"art region in stage: "+a.id());
                if(a.asset().equals("portrait") || a.id().startsWith("plate-")) {
                    for(var b:plan.controls())check(!r.overlaps(b.rect()),"HUD/control overlap "+a.id()+"/"+b.id());
                }
            }
            var compass=plan.get("compass").rect();
            for(var a:plan.regions())if(a.id().startsWith("plate-")||a.asset().equals("portrait"))check(!compass.overlaps(a.rect()),"HUD/compass overlap "+a.id());
        } else check(plan.controls().isEmpty(),"moving panel input blocked");
    }
    void verifyText(String name) {
        for(var t:renderer.textMeasures)check(t.width()<=t.allowed().w()+1 && t.height()<=t.allowed().h()+1,"Text overflow "+name+" '"+t.text()+"' glyph="+t.width()+"x"+t.height()+" allowed="+t.allowed());
    }
    void verifyGpuPick(ResponsiveTableLayout.View view,ResponsiveTableLayout.Plan plan,float ratio) {
        int w=Math.round(view.width()*ratio),h=Math.round(view.height()*ratio);
        FrameBuffer fbo=new FrameBuffer(Pixmap.Format.RGBA8888,w,h,false);fbo.begin();Gdx.gl.glDisable(GL20.GL_DITHER);
        renderer.renderPick(view,plan,ratio);Pixmap pix=ScreenUtils.getFrameBufferPixmap(0,0,w,h);
        try {
            var controls=plan.controls();
            for(int i=0;i<controls.size();i++) {
                var c=controls.get(i);var box=view.screen(c.rect());
                for(float[] q:new float[][]{{.5f,.5f},{.04f,.04f},{.96f,.96f},{.04f,.96f},{.96f,.04f}}){
                    int px=Math.min(w-1,Math.max(0,(int)((box.x()+box.w()*q[0])*ratio)));
                    int py=Math.min(h-1,Math.max(0,(int)((box.y()+box.h()*q[1])*ratio)));
                    int id=(pix.getPixel(px,h-1-py)>>>24)&255;
                    String cpu=ResponsiveTableLayout.hit(view,plan,(px+.5f)/ratio,(py+.5f)/ratio);
                    check(id==i+1 && c.id().equals(cpu),"GPU/CPU hit mismatch "+c.id()+" GPU="+id+" CPU="+cpu);pickSamples++;
                }
            }
            for(float[] q:new float[][]{{0,0},{view.width()-1,0},{0,view.height()-1},{view.width()-1,view.height()-1}}) {
                int x=(int)(q[0]*ratio),y=(int)(q[1]*ratio);int id=(pix.getPixel(x,h-1-y)>>>24)&255;
                check(id==0 && ResponsiveTableLayout.hit(view,plan,q[0],q[1])==null,"gutter/corner has no input");pickSamples++;
            }
        } finally {pix.dispose();fbo.end();fbo.dispose();}
    }
    void verifyAlphaBounds(ResponsiveTableLayout.View view,ResponsiveTableLayout.Plan plan,float ratio,String name) {
        int w=Math.round(view.width()*ratio),h=Math.round(view.height()*ratio);
        for(var r:plan.regions()) {
            TextureRegion image=renderer.imageFor(r);if(image==null)continue;
            String source=switch(r.asset()){case "board"->"jade-table-board.png";case "compass"->"round-compass.png";default->"animal-medallions.png";};
            Pixmap src=new Pixmap(Gdx.files.internal("ui/table/"+source));
            int[] bbox=alphaBox(src,image.getRegionX(),image.getRegionY(),image.getRegionWidth(),image.getRegionHeight(),false);
            src.dispose();var dst=view.screen(r.rect());
            float[] expected={ratio*(dst.x()+bbox[0]*dst.w()/image.getRegionWidth()),ratio*(dst.y()+bbox[1]*dst.h()/image.getRegionHeight()),ratio*(dst.x()+(bbox[2]+1)*dst.w()/image.getRegionWidth())-1,ratio*(dst.y()+(bbox[3]+1)*dst.h()/image.getRegionHeight())-1};
            FrameBuffer fbo=new FrameBuffer(Pixmap.Format.RGBA8888,w,h,false);fbo.begin();renderer.renderImageMask(view,r,ratio);
            Pixmap gpu=ScreenUtils.getFrameBufferPixmap(0,0,w,h);int[] actual=alphaBox(gpu,0,0,w,h,true);gpu.dispose();fbo.end();fbo.dispose();
            float max=0;for(int k=0;k<4;k++)max=Math.max(max,Math.abs(expected[k]-actual[k]));
            check(max<=3,"GPU artwork alpha bounds "+name+"/"+r.id()+" deviation="+max);
            alphaResults.add(Map.of("capture",name,"region",r.id(),"expected",List.of(expected[0],expected[1],expected[2],expected[3]),"raster",List.of(actual[0],actual[1],actual[2],actual[3]),"maxErrorPx",max));
        }
    }
    static int[] alphaBox(Pixmap p,int sx,int sy,int width,int height,boolean flip) {
        int minX=width,minY=height,maxX=-1,maxY=-1;
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)if((p.getPixel(sx+x,sy+(flip?height-1-y:y))&255)>=128){minX=Math.min(minX,x);maxX=Math.max(maxX,x);minY=Math.min(minY,y);maxY=Math.max(maxY,y);}
        if(maxX<0)throw new AssertionError("empty alpha readback");return new int[]{minX,minY,maxX,maxY};
    }
    void click(ResponsiveTableLayout.View view,String id) {
        var plan=ResponsiveTableLayout.plan(draft.page,1);var r=view.screen(plan.get(id).rect());
        Gdx.input.getInputProcessor().touchDown(Math.round(r.cx()),Math.round(r.cy()),0,0);
        Gdx.input.getInputProcessor().touchUp(Math.round(r.cx()),Math.round(r.cy()),0,0);
    }
    void runResizeStep() throws Exception {
        var view=liveView();var plan=ResponsiveTableLayout.plan(draft.page,1);
        check(draft.target==4&&draft.look&&draft.cost()==40&&!draft.confirmed,"resize preserves target/look/cost");
        float ratio=(float)Gdx.graphics.getBackBufferWidth()/Gdx.graphics.getWidth();
        Gdx.gl.glViewport(0,0,Gdx.graphics.getBackBufferWidth(),Gdx.graphics.getBackBufferHeight());
        renderer.render(view,plan,draft,ratio,false);verifyText("resize");
        String file=String.format("resize-%02d-%dx%d",index,current.w,current.h);capture(file,Gdx.graphics.getBackBufferWidth(),Gdx.graphics.getBackBufferHeight());
        click(view,"target-0");check(draft.target==0,"resize actual InputProcessor click target0");click(view,"target-4");check(draft.target==4,"resize actual InputProcessor click target4");
        click(view,"look");check(!draft.look,"resize actual checkbox off");click(view,"look");check(draft.look,"resize actual checkbox on");
        verifyGpuPick(view,plan,ratio);
        // A press that began at the old size cannot submit after a geometry change.
        var confirm=view.screen(plan.get("confirm").rect());draft.down(view,plan,confirm.cx(),confirm.cy());draft.resized();draft.up(view,plan,confirm.cx(),confirm.cy());
        check(draft.submissions==0&&!draft.confirmed,"resize cancels held submit");
        resizeResults.add(Map.of("id",file,"window",List.of(current.w,current.h),"target",draft.target,"look",draft.look,"cost",draft.cost(),"submissions",draft.submissions,"stage",rect(view.stage())));
        if(++index<resizePath.length){int[] next=resizePath[index];request(new Case("resize",next[0],next[1],ResponsiveTableLayout.Page.DRAWER,1,0,ResponsiveTableLayout.Insets.ZERO));}
        else {
            int selected=draft.target;boolean look=draft.look;draft.resized();
            var zero=ResponsiveTableLayout.view(0,0,ResponsiveTableLayout.Insets.ZERO);
            check(!zero.drawable()&&draft.target==selected&&draft.look==look,"0x0 lifecycle preserves draft");
            click(view,"confirm");check(draft.confirmed&&draft.submissions==1,"confirm exactly once after resize sequence");
            var receipt=ResponsiveTableLayout.plan(draft.page,1);renderer.render(view,receipt,draft,ratio,false);capture("resize-07-confirmed",Gdx.graphics.getBackBufferWidth(),Gdx.graphics.getBackBufferHeight());
            saveReport(true);finished=true;System.out.println("PASS: "+assertions+" assertions, "+pickSamples+" GPU/CPU point checks, "+results.size()+" static cases, "+resizeResults.size()+" real window resizes, "+alphaResults.size()+" artwork silhouette checks.");Gdx.app.exit();
        }
    }
    void capture(String name,int w,int h) {
        Pixmap p=ScreenUtils.getFrameBufferPixmap(0,0,w,h);try{PixmapIO.writePNG(Gdx.files.absolute(new File(out,name+".png").getAbsolutePath()),p,-1,true);}finally{p.dispose();}
    }
    void saveReport(boolean passed) throws Exception {
        Map<String,Object> report=new LinkedHashMap<>();report.put("passed",passed);report.put("type","design reference GL render; fixture names/hands; not production APP acceptance");report.put("assertions",assertions);report.put("gpuCpuPointChecks",pickSamples);report.put("captures",results);report.put("artworkAlphaBounds",alphaResults);report.put("resizeSequence",resizeResults);report.put("limits",List.of("800x450 robustness only; normal minimum 960x540","3440x1440 is FBO rendering because OS capped native width to 3124","1.5x/2x are framebuffer simulations, not OS display-scale changes","0x0 is a synthetic lifecycle check, not a real OS minimize test"));
        Files.writeString(new File(out,"validation.json").toPath(),json(report),StandardCharsets.UTF_8);
    }
    static List<Float> rect(ResponsiveTableLayout.Rect r){return List.of(r.x(),r.y(),r.w(),r.h());}
    static String json(Object value) {
        if(value==null)return "null";if(value instanceof String s)return "\""+s.replace("\\","\\\\").replace("\"","\\\"").replace("\n","\\n")+"\"";
        if(value instanceof Map<?,?> m){List<String> parts=new ArrayList<>();for(var e:m.entrySet())parts.add(json(e.getKey().toString())+":"+json(e.getValue()));return "{"+String.join(",",parts)+"}";}
        if(value instanceof Iterable<?> list){List<String> parts=new ArrayList<>();for(var v:list)parts.add(json(v));return "["+String.join(",",parts)+"]";}return value.toString();
    }
    @Override public void pause() {}
    @Override public void resume() {}
    @Override public void dispose() {if(renderer!=null)renderer.dispose();}
}
