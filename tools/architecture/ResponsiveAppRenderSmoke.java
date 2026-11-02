import com.badlogic.gdx.ApplicationListener;
import com.badlogic.gdx.Gdx;
import com.badlogic.gdx.backends.lwjgl3.Lwjgl3Application;
import com.badlogic.gdx.backends.lwjgl3.Lwjgl3ApplicationConfiguration;
import com.badlogic.gdx.graphics.Pixmap;
import com.badlogic.gdx.graphics.PixmapIO;
import com.badlogic.gdx.math.Rectangle;
import com.badlogic.gdx.math.Vector2;
import com.badlogic.gdx.utils.ScreenUtils;
import com.majiang.MahjongGame;
import com.majiang.client.api.ActionOptionView;
import com.majiang.client.api.GameSnapshotView;
import com.majiang.client.api.LegalActionView;
import com.majiang.client.api.ShuffleTarget;
import com.majiang.client.api.TileView;
import com.majiang.presentation.game.GameScreen;
import com.majiang.presentation.game.ShufflePreparationModel;
import com.majiang.presentation.game.ShufflePreparationPanel;
import com.majiang.presentation.menu.MainMenuItemId;
import com.majiang.presentation.menu.MenuScreen;
import kotlin.Pair;

import java.io.File;
import java.io.PrintWriter;
import java.io.IOException;
import java.math.BigDecimal;
import java.math.RoundingMode;
import java.util.ArrayList;
import java.util.List;

/**
 * 正式 APP 的响应式 UI/输入/状态保持 GL 冒烟。
 *
 * 只走生产链: MahjongGame -> 主菜单 SOLO -> 真实 GameScreen -> 权威准备 -> 真实发牌。
 * 不构造样例渲染器，不暂停单机权威 tick，不替换 Shuffle 草稿或回执。
 *
 * 权威准备窗口只有 10 秒，所以本测试不把整张尺寸矩阵塞进同一个准备窗口：
 * 1. 第一局在同一准备窗口内完成 1280 默认/抽屉/40点/连续 resize/回执/发牌；
 * 2. 之后的 960/1024/1280x800/1600/1920/2560 各自重新真实开局，取得独立准备窗口，
 *    再在该窗口内真实点击抽屉并等待真实发牌；
 * 3. 发牌后没有准备截止，才在同一牌局内做 1280 动画中段和牌河提示。
 *
 * 运行：
 *   .\gradlew.bat -I tools/architecture/responsive-app-smoke.init.gradle :lwjgl3:responsiveAppSmoke
 */
public final class ResponsiveAppRenderSmoke implements ApplicationListener {

    private static final String DEFAULT_OUT = "D:/majiang/doc/ui/round-preparation/responsive-app-runtime";
    private static final long OVERALL_TIMEOUT_MS = 150_000L;

    /** 每个尺寸使用独立真实准备窗口，避免 10 秒权威截止后被误判为输入失败。 */
    private static final int[][] FRESH_SESSION_SIZES = {
        {960, 540}, {1024, 768}, {1280, 800}, {1600, 600}, {1920, 1080}, {2560, 1080}
    };

    private static final int[][] CONTINUOUS_SIZES = {
        {1160, 700}, {1024, 768}, {960, 540}, {1440, 810}, {1600, 600}, {1280, 720}
    };

    private final File outputDir = new File(System.getProperty("smoke.output", DEFAULT_OUT));
    private final List<String> lines = new ArrayList<>();
    private final List<String> csv = new ArrayList<>();
    private final long startedAtNanos = System.nanoTime();

    private MahjongGame game;
    private int phase = 1;
    private int freshIndex = 0;
    private int continuousIndex = 0;
    private boolean failed = false;
    private boolean inRender = false;
    private boolean animationClickAttempted = false;
    private boolean menuReturnRequested = false;
    private int[] targetCheckPixelBefore = null;

    private int requestedWidth = 0;
    private int requestedHeight = 0;
    private int resizeSettleFrames = 0;
    private String pendingPurpose = null;
    private long resizeRequestedAtNanos = 0L;
    private boolean pressBeforeResize = false;

    public static void main(String[] args) {
        Lwjgl3ApplicationConfiguration config = new Lwjgl3ApplicationConfiguration();
        config.setTitle("Responsive APP GL smoke");
        config.setWindowedMode(1280, 720);
        config.setResizable(true);
        config.setForegroundFPS(30);
        config.disableAudio(true);
        new Lwjgl3Application(new ResponsiveAppRenderSmoke(), config);
    }

    @Override
    public void create() {
        outputDir.mkdirs();
        csv.add("capture,windowW,windowH,fbW,fbH,fbStageX,fbStageY,stageLeft,stageTop,stageW,stageH,scale,state");
        log("create window=" + Gdx.graphics.getWidth() + "x" + Gdx.graphics.getHeight()
            + " fb=" + Gdx.graphics.getBackBufferWidth() + "x" + Gdx.graphics.getBackBufferHeight());
        try {
            game = new MahjongGame();
            game.create();
        } catch (Throwable t) {
            fail("create", t);
        }
    }

    @Override
    public void resize(int width, int height) {
        if (game != null) game.resize(width, height);
    }

    @Override
    public void dispose() {
        if (game != null) game.dispose();
    }

    @Override
    public void pause() {
    }

    @Override
    public void resume() {
    }

    private void check(boolean value, String text) {
        if (!value) throw new AssertionError(text);
    }

    private void log(String text) {
        lines.add(text);
        System.out.println("[RESPONSIVE-APP] " + text);
    }

    private void fail(String phaseName, Throwable t) {
        failed = true;
        log("FAIL phase=" + phaseName + " : " + t);
        t.printStackTrace(System.out);
        writeLogs();
        Gdx.app.exit();
    }

    private void writeLogs() {
        try (PrintWriter writer = new PrintWriter(new File(outputDir, "smoke-results.txt"), "UTF-8")) {
            for (String line : lines) writer.println(line);
        } catch (IOException ignored) {
        }
        try (PrintWriter writer = new PrintWriter(new File(outputDir, "runtime-matrix.csv"), "UTF-8")) {
            for (String line : csv) writer.println(line);
        } catch (IOException ignored) {
        }
    }

    private static String f2(double value) {
        return BigDecimal.valueOf(value).setScale(2, RoundingMode.HALF_UP).toPlainString();
    }

    private String stateText(GameScreen screen) {
        ShufflePreparationPanel panel = screen.getShufflePreparationPanel();
        if (panel.isActive()) {
            return "prep:" + panel.getModel().getPage()
                + ":target=" + panel.getModel().getTarget()
                + ":cost=" + panel.getModel().getTotalCost()
                + ":see=" + panel.getModel().draftSelection().getSeeThrough();
        }
        GameSnapshotView snapshot = screen.getCurrentSnapshot();
        if (snapshot != null && snapshot.getView().getMyDrawnTile() != null) {
            return "playing:hand=" + snapshot.getView().getHandTileCounts().stream().mapToInt(Integer::intValue).sum()
                + ":wall=" + snapshot.getView().getWallRemaining();
        }
        return "playing";
    }

    private void capture(String name, GameScreen screen) {
        check(outputDir.isDirectory() || outputDir.mkdirs(), "cannot create screenshot directory");
        int fbW = Gdx.graphics.getBackBufferWidth();
        int fbH = Gdx.graphics.getBackBufferHeight();
        Pixmap pixels = ScreenUtils.getFrameBufferPixmap(0, 0, fbW, fbH);
        try {
            PixmapIO.writePNG(Gdx.files.absolute(new File(outputDir, name + ".png").getAbsolutePath()), pixels, -1, true);
        } finally {
            pixels.dispose();
        }
        var v = screen.getStageViewport();
        csv.add(String.join(",",
            name,
            Integer.toString(Gdx.graphics.getWidth()),
            Integer.toString(Gdx.graphics.getHeight()),
            Integer.toString(fbW),
            Integer.toString(fbH),
            Integer.toString(v.getFramebufferStageX()),
            Integer.toString(v.getFramebufferStageY()),
            f2(v.getStageLeft()),
            f2(v.getStageTop()),
            f2(v.getStageWidth()),
            f2(v.getStageHeight()),
            String.format(java.util.Locale.ROOT, "%.6f", v.getScale()),
            stateText(screen).replace(',', ';')
        ));
        log("capture " + name
            + " window=" + Gdx.graphics.getWidth() + "x" + Gdx.graphics.getHeight()
            + " fb=" + fbW + "x" + fbH
            + " stage=" + f2(v.getStageLeft()) + "," + f2(v.getStageTop())
            + "," + f2(v.getStageWidth()) + "," + f2(v.getStageHeight())
            + " scale=" + String.format(java.util.Locale.ROOT, "%.6f", v.getScale())
            + " " + stateText(screen));
    }

    private int[] samplePng(String name, int pngX, int pngY) {
        try {
            java.awt.image.BufferedImage image = javax.imageio.ImageIO.read(new File(outputDir, name + ".png"));
            check(image != null, "cannot decode screenshot " + name);
            int rgb = image.getRGB(
                Math.max(0, Math.min(image.getWidth() - 1, pngX)),
                Math.max(0, Math.min(image.getHeight() - 1, pngY))
            );
            return new int[]{(rgb >>> 16) & 255, (rgb >>> 8) & 255, rgb & 255};
        } catch (IOException e) {
            throw new IllegalStateException("cannot read screenshot " + name, e);
        }
    }

    private void requestResize(int width, int height, String purpose) {
        requestedWidth = width;
        requestedHeight = height;
        pendingPurpose = purpose;
        resizeSettleFrames = 0;
        resizeRequestedAtNanos = System.nanoTime();
        Gdx.graphics.setWindowedMode(width, height);
        log("request resize -> " + width + "x" + height + " for " + purpose);
    }

    /** Returns true once the requested window is visible and enough frames have rendered. */
    private boolean resizeReady() {
        if (pendingPurpose == null) return true;
        if (Gdx.graphics.getWidth() != requestedWidth || Gdx.graphics.getHeight() != requestedHeight) {
            if ((System.nanoTime() - resizeRequestedAtNanos) / 1_000_000L > 5_000L) {
                throw new IllegalStateException("resize did not settle: requested "
                    + requestedWidth + "x" + requestedHeight + ", actual "
                    + Gdx.graphics.getWidth() + "x" + Gdx.graphics.getHeight());
            }
            return false;
        }
        if (++resizeSettleFrames < 3) return false;
        pendingPurpose = null;
        return true;
    }

    private boolean clickSoloIfReady() {
        if (!(game.getScreen() instanceof MenuScreen)) return false;
        MenuScreen menu = (MenuScreen) game.getScreen();
        if (!menu.getMainMenuView().getCurrentFrame().getAllSettled()) return false;
        Rectangle rect = menu.getMainMenuView().rectFor(MainMenuItemId.SOLO);
        Vector2 point = menu.screenPoint(rect.x + rect.width / 2f, rect.y + rect.height / 2f);
        menu.touchDown(Math.round(point.x), Math.round(point.y), 0, 0);
        menu.touchUp(Math.round(point.x), Math.round(point.y), 0, 0);
        log("clicked real SOLO entry");
        return true;
    }

    private Vector2 pointOfStage(GameScreen screen, float x, float y) {
        return screen.stageToScreen(x, y);
    }

    private boolean clickStage(GameScreen screen, float x, float y) {
        Vector2 point = pointOfStage(screen, x, y);
        int sx = Math.round(point.x);
        int sy = Math.round(point.y);
        boolean down = screen.touchDown(sx, sy, 0, 0);
        boolean up = screen.touchUp(sx, sy, 0, 0);
        return down || up;
    }

    private boolean clickRect(GameScreen screen, Rectangle rect) {
        return clickStage(screen, rect.x + rect.width / 2f, rect.y + rect.height / 2f);
    }

    private void pressRect(GameScreen screen, Rectangle rect) {
        Vector2 p = pointOfStage(screen, rect.x + rect.width / 2f, rect.y + rect.height / 2f);
        check(screen.touchDown(Math.round(p.x), Math.round(p.y), 0, 0), "press must be handled");
    }

    private void releaseRect(GameScreen screen, Rectangle rect) {
        Vector2 p = pointOfStage(screen, rect.x + rect.width / 2f, rect.y + rect.height / 2f);
        screen.touchUp(Math.round(p.x), Math.round(p.y), 0, 0);
    }

    private void verifyViewportGeometry(GameScreen screen) {
        double w = Gdx.graphics.getWidth();
        double h = Gdx.graphics.getHeight();
        double scale = Math.min(w / 1280.0, h / 720.0);
        double stageW = 1280.0 * scale;
        double stageH = 720.0 * scale;
        double stageX = (w - stageW) / 2.0;
        double stageY = (h - stageH) / 2.0;
        var v = screen.getStageViewport();
        check(Math.abs(v.getScale() - scale) < 0.0005, "stage scale mismatch");
        check(Math.abs(v.getStageLeft() - stageX) < 0.75, "stage left mismatch");
        check(Math.abs(v.getStageTop() - stageY) < 0.75, "stage top mismatch");
        check(Math.abs(v.getStageWidth() - stageW) < 0.75, "stage width mismatch");
        check(Math.abs(v.getStageHeight() - stageH) < 0.75, "stage height mismatch");
    }

    private void verifyInverseHit(GameScreen screen, float designX, float designY) {
        Vector2 screenPoint = screen.stageToScreen(designX, designY);
        Vector2 local = screen.screenToStage(Math.round(screenPoint.x), Math.round(screenPoint.y));
        check(local != null, "inverse mapping rejected an inside point");
        check(Math.abs(local.x - designX) <= 1.2f, "inverse x mismatch " + local.x + " vs " + designX);
        check(Math.abs(local.y - designY) <= 1.2f, "inverse y mismatch " + local.y + " vs " + designY);
    }

    private void verifyGutterRejected(GameScreen screen) {
        var v = screen.getStageViewport();
        int gx;
        int gy;
        if (v.getStageLeft() > 4f) {
            gx = Math.round(v.getStageLeft() / 2f);
            gy = Gdx.graphics.getHeight() / 2;
        } else if (v.getStageTop() > 4f) {
            gx = Gdx.graphics.getWidth() / 2;
            gy = Math.round(v.getStageTop() / 2f);
        } else {
            return;
        }
        check(!screen.touchDown(gx, gy, 0, 0), "environment gutter must not respond to table input");
        screen.touchUp(gx, gy, 0, 0);
        log("gutter rejected at window=" + Gdx.graphics.getWidth() + "x" + Gdx.graphics.getHeight()
            + " screen=" + gx + "," + gy);
    }

    private void clickTargetTwiceAndVerify(GameScreen screen) {
        ShufflePreparationPanel panel = screen.getShufflePreparationPanel();
        Rectangle card = panel.getBtnTargets().get(ShuffleTarget.KOKUSHI);
        verifyInverseHit(screen, card.x + card.width / 2f, card.y + card.height / 2f);
        Vector2 screenPoint = screen.stageToScreen(card.x + card.width / 2f, card.y + card.height / 2f);
        Vector2 local = screen.screenToStage(Math.round(screenPoint.x), Math.round(screenPoint.y));
        log("target click window=" + Gdx.graphics.getWidth() + "x" + Gdx.graphics.getHeight()
            + " card=" + f2(card.x) + "," + f2(card.y) + "," + f2(card.width) + "," + f2(card.height)
            + " screen=" + f2(screenPoint.x) + "," + f2(screenPoint.y)
            + " local=" + (local == null ? "null" : f2(local.x) + "," + f2(local.y))
            + " before=" + panel.getModel().getTarget()
            + " drawerOffset=" + panel.getMotion().getDrawerOffsetX()
            + " motion=" + panel.getMotion().isAnimating());
        check(clickRect(screen, card), "KOKUSHI card click must be handled");
        log("target click after first=" + panel.getModel().getTarget());
        check(panel.getModel().getTarget() == null, "first real click must toggle selection off");
        check(clickRect(screen, card), "KOKUSHI re-click must be handled");
        log("target click after second=" + panel.getModel().getTarget());
        check(panel.getModel().getTarget() == ShuffleTarget.KOKUSHI, "second real click must reselect KOKUSHI");
        check(panel.getModel().getTotalCost() == 40, "target + see-through must stay 40, got "
            + panel.getModel().getTotalCost());
    }

    private void beginContinuousStep(GameScreen screen) {
        ShufflePreparationPanel panel = screen.getShufflePreparationPanel();
        check(panel.getModel().getTarget() == ShuffleTarget.KOKUSHI, "continuous draft target lost");
        check(panel.getModel().getTotalCost() == 40, "continuous draft cost lost");
        log("continuous begin index=" + continuousIndex + " page=" + panel.getModel().getPage()
            + " target=" + panel.getModel().getTarget() + " cost=" + panel.getModel().getTotalCost());
        // 按住提交，再改变窗口。resize 必须取消这次按压；释放点即使落在新按钮上也无效。
        pressRect(screen, panel.getBtnDrawerConfirmRect());
        pressBeforeResize = true;
        int[] next = CONTINUOUS_SIZES[continuousIndex];
        requestResize(next[0], next[1], "continuous-resize");
        phase = 5;
    }

    private void finishContinuousStep(GameScreen screen) {
        ShufflePreparationPanel panel = screen.getShufflePreparationPanel();
        if (pressBeforeResize) {
            releaseRect(screen, panel.getBtnDrawerConfirmRect());
            pressBeforeResize = false;
        }
        log("continuous after release index=" + continuousIndex + " page=" + panel.getModel().getPage()
            + " target=" + panel.getModel().getTarget() + " cost=" + panel.getModel().getTotalCost()
            + " submitting=" + panel.getModel().isSubmitting() + " confirmed=" + panel.getModel().isPlanConfirmed());
        if (panel.getModel().isSubmitting() || panel.getModel().isPlanConfirmed()) {
            throw new AssertionError("prepare window expired or resize released a cancelled press and submitted; "
                + "this sequence must stay inside the authoritative 10s selection window");
        }
        check(panel.getModel().getTarget() == ShuffleTarget.KOKUSHI, "target lost after resize");
        check(panel.getModel().getTotalCost() == 40, "40 point draft lost after resize");

        int[] size = CONTINUOUS_SIZES[continuousIndex];
        capture(String.format(java.util.Locale.ROOT, "app-resize-%02d-%dx%d", continuousIndex + 1, size[0], size[1]), screen);
        verifyViewportGeometry(screen);
        clickTargetTwiceAndVerify(screen);
        check(panel.getModel().getTarget() == ShuffleTarget.KOKUSHI, "draft target not preserved after real click");
        check(panel.getModel().getTotalCost() == 40, "40 point draft not preserved after real click");

        continuousIndex++;
        if (continuousIndex < CONTINUOUS_SIZES.length) {
            beginContinuousStep(screen);
        } else {
            phase = 6;
        }
    }

    private boolean clickLegalDiscard(GameScreen screen) {
        GameSnapshotView snapshot = screen.getCurrentSnapshot();
        if (snapshot == null) return false;
        Integer tileId = null;
        for (ActionOptionView option : snapshot.getDecision().getOptions()) {
            if (option.getAction() instanceof LegalActionView.DiscardTile discard) {
                tileId = discard.getTile().getTileId();
                break;
            }
        }
        if (tileId == null) return false;
        for (Pair<Rectangle, TileView> slot : screen.getHandTileRects()) {
            if (slot.getSecond().getTileId() == tileId) {
                Rectangle rect = slot.getFirst();
                return clickStage(screen, rect.x + rect.width / 2f, 80f);
            }
        }
        return false;
    }

    private void startFreshSession() {
        game.returnToMenu();
        menuReturnRequested = true;
        phase = 13;
    }

    @Override
    public void render() {
        if (failed || inRender) return;
        inRender = true;
        try {
            if (game == null) return;
            game.render();
            if ((System.nanoTime() - startedAtNanos) / 1_000_000L > OVERALL_TIMEOUT_MS) {
                fail("timeout", new IllegalStateException("responsive APP smoke exceeded "
                    + OVERALL_TIMEOUT_MS + "ms"));
                return;
            }

            if (!resizeReady()) return;

            switch (phase) {
                case 1: {
                    if (!clickSoloIfReady()) return;
                    phase = 2;
                    return;
                }
                case 2: {
                    if (!(game.getScreen() instanceof GameScreen)) return;
                    GameScreen screen = (GameScreen) game.getScreen();
                    ShufflePreparationPanel panel = screen.getShufflePreparationPanel();
                    if (!panel.isActive() || panel.getMotion().getPanelOffsetX() > 0.01f) return;
                    check(panel.getModel().getPage() == ShufflePreparationModel.PreparationPage.PANEL,
                        "default entry must be compact panel");
                    check(panel.getModel().getTarget() == null, "default entry must not preselect target");
                    verifyViewportGeometry(screen);
                    capture("app-01-entry-1280x720", screen);
                    check(clickRect(screen, panel.getBtnOpenTargetRect()), "cheat entry must be clickable");
                    check(panel.getModel().getPage() == ShufflePreparationModel.PreparationPage.DRAWER,
                        "cheat entry must open target drawer");
                    check(panel.getModel().getTarget() == null, "drawer must not preselect a target");
                    phase = 3;
                    return;
                }
                case 3: {
                    GameScreen screen = (GameScreen) game.getScreen();
                    ShufflePreparationPanel panel = screen.getShufflePreparationPanel();
                    float offset = panel.getMotion().getDrawerOffsetX();
                    if (offset > 100f) return;
                    capture("app-02-drawer-mid-1280x720", screen);
                    phase = 4;
                    return;
                }
                case 4: {
                    GameScreen screen = (GameScreen) game.getScreen();
                    ShufflePreparationPanel panel = screen.getShufflePreparationPanel();
                    if (panel.getMotion().getDrawerOffsetX() > 0.01f) return;
                    capture("app-03-drawer-unselected-1280x720", screen);
                    targetCheckPixelBefore = samplePng("app-03-drawer-unselected-1280x720", 939, 430);
                    check(clickRect(screen, panel.getBtnTargets().get(ShuffleTarget.KOKUSHI)),
                        "unselected KOKUSHI card must be clickable");
                    check(panel.getModel().getTarget() == ShuffleTarget.KOKUSHI, "KOKUSHI selection lost");
                    // 状态改变后必须让下一帧真正重绘，再从帧缓冲截图；
                    // 同一帧内立即 getFrameBufferPixmap 读到的仍是点击前的画面。
                    phase = 19;
                    return;
                }
                case 19: {
                    GameScreen screen = (GameScreen) game.getScreen();
                    ShufflePreparationPanel panel = screen.getShufflePreparationPanel();
                    capture("app-04-target-selected-1280x720", screen);
                    int[] selectedCheck = samplePng("app-04-target-selected-1280x720", 939, 430);
                    log("selected card pixel before=" + targetCheckPixelBefore[0] + ","
                        + targetCheckPixelBefore[1] + "," + targetCheckPixelBefore[2]
                        + " after=" + selectedCheck[0] + "," + selectedCheck[1] + "," + selectedCheck[2]);
                    check(targetCheckPixelBefore[0] != selectedCheck[0]
                            || targetCheckPixelBefore[1] != selectedCheck[1]
                            || targetCheckPixelBefore[2] != selectedCheck[2],
                        "selected target must change a real frame-buffer pixel");
                    check(clickRect(screen, panel.getBtnDrawerSeeThroughRect()), "see-through must be clickable");
                    check(panel.getModel().getTotalCost() == 40, "target + see-through must cost 40");
                    phase = 20;
                    return;
                }
                case 20: {
                    GameScreen screen = (GameScreen) game.getScreen();
                    capture("app-05-cost40-1280x720", screen);
                    continuousIndex = 0;
                    capture("app-resize-00-1280x720", screen);
                    beginContinuousStep(screen);
                    return;
                }
                case 5: {
                    finishContinuousStep((GameScreen) game.getScreen());
                    return;
                }
                case 6: {
                    GameScreen screen = (GameScreen) game.getScreen();
                    check(Gdx.graphics.getWidth() == 1280 && Gdx.graphics.getHeight() == 720,
                        "expected 1280x720 before final confirm");
                    ShufflePreparationPanel panel = screen.getShufflePreparationPanel();
                    check(panel.getModel().getTarget() == ShuffleTarget.KOKUSHI, "final target lost");
                    check(panel.getModel().getTotalCost() == 40, "final cost lost");
                    check(clickRect(screen, panel.getBtnDrawerConfirmRect()), "final confirm must be handled");
                    log("final confirm submitted once; modelSubmitting=" + panel.getModel().isSubmitting()
                        + " page=" + panel.getModel().getPage());
                    phase = 7;
                    return;
                }
                case 7: {
                    GameScreen screen = (GameScreen) game.getScreen();
                    ShufflePreparationPanel panel = screen.getShufflePreparationPanel();
                    if (!panel.isReceiptVisible() || panel.getReceiptAlpha() < 0.999f) return;
                    capture("app-10-receipt-1280x720", screen);
                    check(panel.getModel().getTarget() == ShuffleTarget.KOKUSHI, "receipt must retain target");
                    check(panel.getModel().getTotalCost() == 40, "receipt must retain 40 points");
                    phase = 8;
                    return;
                }
                case 8: {
                    GameScreen screen = (GameScreen) game.getScreen();
                    ShufflePreparationPanel panel = screen.getShufflePreparationPanel();
                    GameSnapshotView snapshot = screen.getCurrentSnapshot();
                    if (panel.isReceiptVisible() || panel.isActive()) return;
                    if (snapshot == null || snapshot.getView().getMyDrawnTile() == null) return;
                    int hands = snapshot.getView().getHandTileCounts().stream().mapToInt(Integer::intValue).sum();
                    int wall = snapshot.getView().getWallRemaining();
                    log("dealt hands=" + hands + " wall=" + wall + " deadWall=14 cheat=" + snapshot.getCheatPoints());
                    check(hands + wall + 14 == 136, "136 physical tiles must be conserved");
                    capture("app-11-dealt-1280x720", screen);
                    capture("app-12-playing-1280x720", screen);
                    phase = 9;
                    return;
                }
                case 9: {
                    GameScreen screen = (GameScreen) game.getScreen();
                    if (!animationClickAttempted) {
                        boolean clicked = clickLegalDiscard(screen);
                        log("animation kickoff discard click=" + clicked);
                        if (!clicked) return;
                        animationClickAttempted = true;
                    }
                    phase = 10;
                    return;
                }
                case 10: {
                    GameScreen screen = (GameScreen) game.getScreen();
                    if (!screen.isDiscardPlaybackBusy()) return;
                    capture("app-13-animation-mid-1280x720", screen);
                    check(clickStage(screen, 1175f, 678f), "river hint toggle must be clickable");
                    check(screen.getRiverHintPanel().isOpen(), "river hint panel must open");
                    phase = 11;
                    return;
                }
                case 11: {
                    GameScreen screen = (GameScreen) game.getScreen();
                    capture("app-14-river-hint-1280x720", screen);
                    int[] rgb = samplePng("app-14-river-hint-1280x720", 900, 320);
                    log("river hint pixel rgb=" + rgb[0] + "," + rgb[1] + "," + rgb[2]);
                    check(rgb[1] + 16 >= rgb[2],
                        "river hint panel must use jade/gold art, not the old blue rectangle rgb="
                            + rgb[0] + "," + rgb[1] + "," + rgb[2]);
                    check(clickStage(screen, 1175f, 678f), "river hint close must be clickable");
                    check(!screen.getRiverHintPanel().isOpen(), "river hint panel must close");
                    phase = 12;
                    return;
                }
                case 12: {
                    if (freshIndex < FRESH_SESSION_SIZES.length) {
                        startFreshSession();
                    } else {
                        log("stageFontCacheSize=" + game.getUiAssets().getStageFontCacheSize()
                            + " (bound=" + com.majiang.presentation.common.UiAssets.MAX_STAGE_FONTS + ")");
                        log("PASS responsive APP smoke; captures=" + (csv.size() - 1));
                        writeLogs();
                        Gdx.app.exit();
                    }
                    return;
                }
                case 13: {
                    if (!(game.getScreen() instanceof MenuScreen)) return;
                    if (!menuReturnRequested) return;
                    if (!clickSoloIfReady()) return;
                    menuReturnRequested = false;
                    phase = 14;
                    return;
                }
                case 14: {
                    if (!(game.getScreen() instanceof GameScreen)) return;
                    GameScreen screen = (GameScreen) game.getScreen();
                    ShufflePreparationPanel panel = screen.getShufflePreparationPanel();
                    if (!panel.isActive() || panel.getMotion().getPanelOffsetX() > 0.01f) return;
                    int[] size = FRESH_SESSION_SIZES[freshIndex];
                    requestResize(size[0], size[1], "fresh-session-drawer");
                    phase = 15;
                    return;
                }
                case 15: {
                    GameScreen screen = (GameScreen) game.getScreen();
                    ShufflePreparationPanel panel = screen.getShufflePreparationPanel();
                    if (!panel.isActive()) return;
                    int[] size = FRESH_SESSION_SIZES[freshIndex];
                    check(Gdx.graphics.getWidth() == size[0] && Gdx.graphics.getHeight() == size[1],
                        "fresh session resize did not settle");
                    verifyViewportGeometry(screen);
                    verifyGutterRejected(screen);
                    check(clickRect(screen, panel.getBtnOpenTargetRect()), "fresh cheat entry must be clickable");
                    phase = 17;
                    return;
                }
                case 17: {
                    GameScreen screen = (GameScreen) game.getScreen();
                    ShufflePreparationPanel panel = screen.getShufflePreparationPanel();
                    if (!panel.isActive() || panel.getMotion().getDrawerOffsetX() > 0.01f) return;
                    check(clickRect(screen, panel.getBtnTargets().get(ShuffleTarget.KOKUSHI)),
                        "fresh KOKUSHI card must be clickable");
                    check(panel.getModel().getTarget() == ShuffleTarget.KOKUSHI, "fresh KOKUSHI selection lost");
                    check(clickRect(screen, panel.getBtnDrawerSeeThroughRect()), "fresh see-through must be clickable");
                    check(panel.getModel().getTotalCost() == 40, "fresh target + see-through must cost 40");
                    phase = 18;
                    return;
                }
                case 18: {
                    GameScreen screen = (GameScreen) game.getScreen();
                    ShufflePreparationPanel panel = screen.getShufflePreparationPanel();
                    int[] size = FRESH_SESSION_SIZES[freshIndex];
                    // 等待选中/看破后的真实重绘，再截图并做两次真实点击回归。
                    capture("app-06-drawer-" + size[0] + "x" + size[1], screen);
                    clickTargetTwiceAndVerify(screen);
                    check(clickRect(screen, panel.getBtnDrawerConfirmRect()), "fresh final confirm must be handled");
                    phase = 16;
                    return;
                }
                case 16: {
                    GameScreen screen = (GameScreen) game.getScreen();
                    ShufflePreparationPanel panel = screen.getShufflePreparationPanel();
                    GameSnapshotView snapshot = screen.getCurrentSnapshot();
                    if (panel.isActive() || panel.isReceiptVisible()) return;
                    if (snapshot == null || snapshot.getView().getMyDrawnTile() == null) return;
                    int[] size = FRESH_SESSION_SIZES[freshIndex];
                    int hands = snapshot.getView().getHandTileCounts().stream().mapToInt(Integer::intValue).sum();
                    int wall = snapshot.getView().getWallRemaining();
                    check(hands + wall + 14 == 136, "136 physical tiles must be conserved at "
                        + size[0] + "x" + size[1]);
                    capture("app-12-playing-" + size[0] + "x" + size[1], screen);
                    verifyViewportGeometry(screen);
                    verifyGutterRejected(screen);
                    log("fresh session size=" + size[0] + "x" + size[1] + " hands=" + hands + " wall=" + wall);
                    freshIndex++;
                    if (freshIndex < FRESH_SESSION_SIZES.length) {
                        startFreshSession();
                    } else {
                        phase = 12;
                    }
                    return;
                }
                default:
                    return;
            }
        } catch (Throwable t) {
            fail("phase " + phase, t);
        } finally {
            inRender = false;
        }
    }
}
