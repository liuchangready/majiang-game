import java.util.ArrayList;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * Executable layout specification, deliberately outside production source sets.
 * All authoring rectangles use a top-left origin. The 16:9 stage fits inside
 * the safe window; only the noninteractive environment fills the outer gutters.
 */
public final class ResponsiveTableLayout {
    public static final float WIDTH = 1280, HEIGHT = 720;
    public enum Page { ENTRY, DRAWER, RECEIPT, PLAYING }
    public record Rect(float x, float y, float w, float h) {
        public float right() { return x+w; }
        public float bottom() { return y+h; }
        public float cx() { return x+w/2; }
        public float cy() { return y+h/2; }
        public boolean contains(float px,float py) { return px>=x && px<right() && py>=y && py<bottom(); }
        public Rect move(float dx,float dy) { return new Rect(x+dx,y+dy,w,h); }
        public boolean overlaps(Rect b) { return x<b.right() && right()>b.x && y<b.bottom() && bottom()>b.y; }
    }
    public record Insets(float left,float top,float right,float bottom) {
        public static final Insets ZERO = new Insets(0,0,0,0);
    }
    public record View(int width,int height,float scale,Rect stage,Insets insets) {
        public boolean drawable() { return width>0 && height>0 && scale>0; }
        public Rect screen(Rect r) { return new Rect(stage.x+r.x*scale,stage.y+r.y*scale,r.w*scale,r.h*scale); }
        public float localX(float x) { return (x-stage.x)/scale; }
        public float localY(float y) { return (y-stage.y)/scale; }
    }
    public record Region(String id,Rect rect,String asset,int seat,boolean interactive) {}
    public record Plan(Page page,float progress,List<Region> regions) {
        public Region get(String id) { return regions.stream().filter(r->r.id.equals(id)).findFirst().orElseThrow(); }
        public List<Region> controls() { return regions.stream().filter(Region::interactive).toList(); }
    }
    public static View view(int width,int height,Insets insets) {
        float availableW=Math.max(0,width-insets.left-insets.right);
        float availableH=Math.max(0,height-insets.top-insets.bottom);
        float scale=Math.min(availableW/WIDTH,availableH/HEIGHT);
        if(width<=0 || height<=0 || scale<=0) return new View(width,height,0,new Rect(0,0,0,0),insets);
        float w=WIDTH*scale,h=HEIGHT*scale;
        return new View(width,height,scale,new Rect(insets.left+(availableW-w)/2,insets.top+(availableH-h)/2,w,h),insets);
    }
    private static void add(List<Region> r,String id,float x,float y,float w,float h,String asset,int seat,boolean hit) {
        r.add(new Region(id,new Rect(x,y,w,h),asset,seat,hit));
    }
    private static void hud(List<Region> r,String slot,int seat,float x,float y,float size,float px,float py,float pw,float ph) {
        add(r,"portrait-"+slot,x,y,size,size,"portrait",seat,false);
        add(r,"plate-"+slot,px,py,pw,ph,"plate",seat,false);
    }
    public static Plan plan(Page page,float progress) {
        float p=Math.max(0,Math.min(1,progress));
        float eased=1-(1-p)*(1-p)*(1-p);
        List<Region> r=new ArrayList<>();
        add(r,"board",32,76,1216,620,"board",-1,false);
        if(page==Page.PLAYING) {
            add(r,"compass",546,261,188,188,"compass",-1,false);
            hud(r,"self",0,16,552,112,16,664,176,52);
            hud(r,"left",3,16,228,112,12,344,176,60);
            hud(r,"top",2,308,4,112,132,22,176,60);
            hud(r,"right",1,1148,228,112,1084,344,184,60);
            add(r,"hand-tray",208,596,852,110,"tray",-1,false);
            add(r,"top-tray",436,8,408,54,"tray",-1,false);
            add(r,"river",1098,18,154,48,"secondary",-1,true);
            String[] actions={"mark","swap","sleeve","sort","auto"};
            for(int i=0;i<actions.length;i++) add(r,actions[i],1078,466+i*49,174,46,i==0?"primary":"secondary",-1,true);
        } else {
            add(r,"compass",328,254,204,204,"compass",-1,false);
            hud(r,"self",0,16,470,144,16,622,176,60);
            hud(r,"left",3,16,184,144,16,332,176,60);
            hud(r,"top",2,368,8,120,496,30,176,60);
            float k=page==Page.DRAWER?eased:0;
            hud(r,"right",1,800-256*k,178-18*k,128-4*k,796-260*k,310-22*k,140,60);
            add(r,"preparation-hint",208,626,448,50,"hint",-1,false);
        }
        if(page==Page.ENTRY) {
            float dx=72*(1-eased);
            add(r,"dock",936+dx,48,312,624,"panel",-1,false);
            add(r,"timer",960+dx,124,264,40,"inner",-1,false);
            add(r,"open-target",956+dx,188,272,156,"card",-1,p==1);
            add(r,"look",956+dx,360,272,64,"inner",-1,p==1);
            add(r,"summary",956+dx,464,272,66,"inner",-1,false);
            add(r,"start",956+dx,552,272,76,"primary",-1,p==1);
        } else if(page==Page.DRAWER) {
            float dx=160*(1-eased);
            add(r,"drawer",688+dx,40,560,640,"panel",-1,false);
            add(r,"back",1132+dx,60,96,44,"secondary",-1,p==1);
            for(int i=0;i<6;i++) add(r,"target-"+i,708+(i%2)*268+dx,154+(i/2)*130,252,114,"target-"+i,-1,p==1);
            add(r,"look",708+dx,544,252,58,"inner",-1,p==1);
            add(r,"cost",976+dx,544,252,58,"inner",-1,false);
            add(r,"skip",708+dx,616,252,48,"secondary",-1,p==1);
            add(r,"confirm",976+dx,616,252,48,"primary",-1,p==1);
        } else if(page==Page.RECEIPT) add(r,"receipt",944,160,304,260,"panel",-1,false);
        return new Plan(page,p,Collections.unmodifiableList(r));
    }
    public static String hit(View view,Plan plan,float screenX,float screenY) {
        if(!view.drawable() || !view.stage.contains(screenX,screenY))return null;
        float x=view.localX(screenX),y=view.localY(screenY);
        List<Region> controls=plan.controls();
        for(int i=controls.size()-1;i>=0;i--) if(controls.get(i).rect.contains(x,y)) return controls.get(i).id;
        return null;
    }
    /** Interaction example: resize invalidates an in-flight press, not the draft. */
    public static final class Draft {
        public int target=-1;
        public boolean look;
        public boolean confirmed;
        public int submissions;
        public String pressed;
        public Page page=Page.ENTRY;
        public int cost() { return (target>=0?30:0)+(look?10:0); }
        public void resized() { pressed=null; }
        public void down(View view,Plan plan,float x,float y) { pressed=hit(view,plan,x,y); }
        public void up(View view,Plan plan,float x,float y) {
            String up=hit(view,plan,x,y),old=pressed; pressed=null;
            if(old==null || !old.equals(up) || confirmed)return;
            if(up.equals("open-target")) page=Page.DRAWER;
            else if(up.equals("back")) { target=-1;page=Page.ENTRY; }
            else if(up.equals("look"))look=!look;
            else if(up.startsWith("target-")) {int i=Integer.parseInt(up.substring(7));target=target==i?-1:i;}
            else if(up.equals("skip") || up.equals("start") || up.equals("confirm")) {
                if(up.equals("confirm") && target<0)return;
                if(up.equals("skip")) {target=-1;look=false;}
                confirmed=true;submissions++;page=Page.RECEIPT;
            }
        }
    }
    private ResponsiveTableLayout() {}
}
