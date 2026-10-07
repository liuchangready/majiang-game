import com.badlogic.gdx.ApplicationAdapter;
import com.badlogic.gdx.Gdx;
import com.badlogic.gdx.backends.lwjgl3.Lwjgl3Application;
import com.badlogic.gdx.backends.lwjgl3.Lwjgl3ApplicationConfiguration;
import com.badlogic.gdx.graphics.Pixmap;
import com.badlogic.gdx.graphics.PixmapIO;
import com.badlogic.gdx.utils.ScreenUtils;
import com.majiang.MahjongGame;
import com.majiang.client.api.*;
import com.majiang.presentation.common.render.DiscardAnimation;
import com.majiang.presentation.common.render.InMemorySkinPreferences;
import com.majiang.presentation.common.render.SkinId;
import com.majiang.presentation.common.render.SkinManager;
import com.majiang.presentation.game.GameScreen;
import java.io.File;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.concurrent.CompletableFuture;

/** Real GL regression: authoritative view advances while four discard animations gate input. */
public final class DiscardInputGateRenderSmoke extends ApplicationAdapter {
    private MahjongGame game;
    private GateSession session;
    private GameScreen screen;
    private int frame, stage, completed;

    public static void main(String[] args) {
        Lwjgl3ApplicationConfiguration c = new Lwjgl3ApplicationConfiguration();
        c.setWindowedMode(1280,720); c.setTitle("V4 discard input gate acceptance");
        c.setInitialVisible(false); c.disableAudio(true); c.setForegroundFPS(30);
        new Lwjgl3Application(new DiscardInputGateRenderSmoke(),c);
    }
    @Override public void create() {
        game = new MahjongGame(new SkinManager(new InMemorySkinPreferences(SkinId.WIKIPEDIA)));
        game.create(); session = new GateSession();
        game.changeScreen(new GameScreen(game.getUiAssets(),session,game,null));
        screen = (GameScreen)game.getScreen();
    }
    private void click(int x,int y) { screen.touchDown(x,720-y,0,0); }
    private void clickTile() {
        GameScreen.HandTileSlot slot = screen.refreshHandTileRects().get(0);
        click((int)(slot.getRect().x+slot.getRect().width/2),
            (int)(slot.getRect().y+slot.getRect().height/2));
    }
    private void capture(String name) {
        File dir = new File(System.getProperty("smoke.output"));
        if (!dir.isDirectory() && !dir.mkdirs()) throw new AssertionError("Cannot create screenshot directory");
        Pixmap p = ScreenUtils.getFrameBufferPixmap(0,0,1280,720);
        try { PixmapIO.writePNG(Gdx.files.absolute(new File(dir,name+".png").getAbsolutePath()),p,-1,true); }
        finally { p.dispose(); }
    }
    @Override public void render() {
        screen.render(0f); frame++;
        if(frame%3!=0) return;
        if(stage==0) {
            clickTile(); clickTile();
            if(session.submitted.size()!=1) throw new AssertionError("Double click bypassed pending gate");
            stage++;
        } else if(stage==1) {
            if(!screen.isDiscardPlaybackBusy()) throw new AssertionError("Missing synchronous animation input lock");
            capture("waiting-for-discards");
            screen.updateDiscardAnimation(0.30f);
            stage++;
        } else if(stage==2) {
            clickTile(); click(1140,75);
            if(session.submitted.size()!=1 || screen.isAutoSort()) throw new AssertionError("Playback accepted hand/sort input");
            if(completed==0) capture("discard-in-flight-locked");
            screen.updateDiscardAnimation(DiscardAnimation.TOTAL_DURATION+0.01f); completed++;
            if(!screen.isDiscardPlaybackBusy()) {
                if(completed!=4) throw new AssertionError("Did not await all four discards");
                stage++;
            }
        } else if(stage==3) {
            capture("playback-complete-unlocked");
            clickTile();
            if(session.submitted.size()!=2) throw new AssertionError("Input failed to unlock");
            stage++;
        } else if(stage==4) {
            if(!screen.isDiscardPlaybackBusy()) throw new AssertionError("Second turn did not lock again");
            capture("next-round-of-discards-locked");
            System.out.println("DISCARD_INPUT_GATE_RENDER_PASS screenshots=4 discardsWaited="+completed+" submissions="+session.submitted.size());
            Gdx.app.exit(); stage++;
        }
        if(frame>150) throw new AssertionError("Animation gate stuck");
    }
    @Override public void resize(int w,int h) { if(game!=null)game.resize(w,h); }
    @Override public void dispose() { if(game!=null)game.dispose(); }

    private static final class GateSession extends V4SmokeSession {
        private final List<TileView> closed = new ArrayList<>();
        private final List<TileView> history = new ArrayList<>();
        private final List<List<TileView>> rivers = Arrays.asList(new ArrayList<>(),new ArrayList<>(),new ArrayList<>(),new ArrayList<>());
        private TileView drawn;
        GateSession() {
            closed.addAll(V4SmokeViews.tiles(SuitView.SOU,"123456789"));
            closed.addAll(V4SmokeViews.tiles(SuitView.PIN,"1234"));
            drawn = V4SmokeViews.tile(SuitView.PIN,5,0);
            publishTurn();
        }
        private void publishTurn() {
            List<LegalActionView> actions = new ArrayList<>();
            List<ActionOptionView> options = new ArrayList<>();
            for (TileView tile : closed) {
                var action = new LegalActionView.DiscardTile(2,tile);
                actions.add(action); options.add(new ActionOptionView("discard-"+tile.getTileId(),action));
            }
            var drawnAction = new LegalActionView.DiscardTile(2,drawn);
            actions.add(drawnAction); options.add(new ActionOptionView("discard-"+drawn.getTileId(),drawnAction));
            var builder = V4SmokeViews.player(2).closed(closed).drawn(drawn).actions(actions).history(history);
            for (int seat=0;seat<4;seat++) for (TileView tile:rivers.get(seat)) builder.discard(seat,tile);
            publish(builder.build(), options, null);
        }
        @Override public CompletableFuture<Receipt> submit(ActionChoice choice) {
            LegalActionView action = null;
            for (ActionOptionView option : getCurrentSnapshot().getDecision().getOptions())
                if (option.getOptionId().equals(choice.getOptionId())) action = option.getAction();
            CompletableFuture<Receipt> result = super.submit(choice);
            if (result.join().getStatus()!=ReceiptStatus.APPLIED) return result;
            if (!(action instanceof LegalActionView.DiscardTile discard)) throw new AssertionError("Unexpected action");
            TileView tile = discard.getTile();
            if (!tile.equals(drawn)) { closed.remove(tile); closed.add(drawn); }
            history.add(tile); rivers.get(2).add(tile);
            int turn = submitted.size();
            for (int seat : new int[]{0,1,3})
                rivers.get(seat).add(V4SmokeViews.tile(SuitView.MAN,seat+1,turn-1));
            drawn = V4SmokeViews.tile(SuitView.PIN,5+turn,1);
            publishTurn();
            return result;
        }
    }
}
