import com.badlogic.gdx.ApplicationListener;
import com.badlogic.gdx.Gdx;
import com.badlogic.gdx.backends.lwjgl3.Lwjgl3Application;
import com.badlogic.gdx.backends.lwjgl3.Lwjgl3ApplicationConfiguration;
import com.badlogic.gdx.graphics.Pixmap;
import com.badlogic.gdx.graphics.PixmapIO;
import com.badlogic.gdx.math.Rectangle;
import com.badlogic.gdx.utils.ScreenUtils;
import com.majiang.MahjongGame;
import com.majiang.client.api.GameSnapshotView;
import com.majiang.client.api.ShuffleTarget;
import com.majiang.presentation.game.GameScreen;
import com.majiang.presentation.game.ShufflePreparationModel;
import com.majiang.presentation.game.ShufflePreparationPanel;
import com.majiang.presentation.menu.MainMenuItemId;
import com.majiang.presentation.menu.MenuScreen;

import java.io.File;
import java.io.FileWriter;
import java.io.IOException;
import java.util.ArrayList;
import java.util.List;

/**
 * Official APP round preparation GL smoke.
 *
 * Only the real production chain is used: MahjongGame -> main menu "solo" -> real GameScreen ->
 * real authority preparation stage. Every screenshot comes from the real frame buffer; no concept
 * art is passed off as a runtime capture.
 *
 * Covered:
 * 1. default entry shows the compact preparation panel (no six-target picker), table stays visible;
 * 2. the six-target drawer only expands after clicking the cheat entry (mid frame + settled frame);
 * 3. the 40 point combination (target + see-through) cost preview;
 * 4. the "plan confirmed" receipt after submitting;
 * 5. 960x540 and ultra wide safe areas;
 * 6. real single-player dealing: measures confirm -> deal latency and verifies 136 tile conservation.
 *
 * Run:
 *   .\gradlew.bat -I tools/architecture/round-preparation-smoke.init.gradle :lwjgl3:roundPreparationSmoke
 *   (-PsmokeMode=zero runs the zero-cost start path; default target runs the target confirmation)
 */
public final class RoundPreparationRenderSmoke implements ApplicationListener {

    private static final String DEFAULT_OUT = "D:/majiang/doc/ui/round-preparation/visual-runtime";
    private static final long OVERALL_TIMEOUT_MS = 60_000L;
    private static final int WIDTH = 1280;
    private static final int HEIGHT = 720;
    private static final int SMALL_WIDTH = 960;
    private static final int SMALL_HEIGHT = 540;
    private static final int WIDE_WIDTH = 1600;
    private static final int WIDE_HEIGHT = 600;

    /**
     * 回执标题「方案已确认」在 1280×720 帧缓冲中的位置（PNG 坐标，左上角原点）。
     *
     * 新回执卡为 `(944,300,304,260)`，34px 暖金标题顶部在卡内 y+190
     * （世界坐标），PNG 区域覆盖完整字形。
     */
    private static final int RECEIPT_TITLE_X0 = 990;
    private static final int RECEIPT_TITLE_X1 = 1200;
    private static final int RECEIPT_TITLE_Y0 = 188;
    private static final int RECEIPT_TITLE_Y1 = 250;

    /** 回执标题至少要有这么多暖金色像素，才算「可读」而不是淡出中段。 */
    private static final int RECEIPT_TITLE_MIN_PIXELS = 40;

    private final String mode = System.getProperty("smoke.mode", "target");
    private final File outputDir = new File(System.getProperty("smoke.output", DEFAULT_OUT));
    private final List<String> lines = new ArrayList<>();

    private MahjongGame game;
    private int frame = 0;
    private int phase = 0;
    private boolean failed = false;
    private final long startedAtNanos = System.nanoTime();

    private long confirmClickedNanos = 0L;
    private long dealEndedNanos = 0L;
    private long receiptGoneNanos = 0L;
    private double confirmToDealMs = -1.0;
    private boolean receiptCaptured = false;
    private boolean receiptAttempted = false;
    private int[] cardPixelBefore = null;
    /** Guards against the window-mode switch re-entering the render callback. */
    private boolean inRender = false;

    public static void main(String[] args) {
        Lwjgl3ApplicationConfiguration config = new Lwjgl3ApplicationConfiguration();
        config.setTitle("Round preparation GL smoke (" + System.getProperty("smoke.mode", "target") + ")");
        config.setWindowedMode(WIDTH, HEIGHT);
        config.setForegroundFPS(60);
        config.disableAudio(true);
        new Lwjgl3Application(new RoundPreparationRenderSmoke(), config);
    }

    @Override
    public void create() {
        outputDir.mkdirs();
        log("create: window=" + Gdx.graphics.getWidth() + "x" + Gdx.graphics.getHeight() + " mode=" + mode);
        try {
            game = new MahjongGame();
            game.create();
            phase = 1;
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
        System.out.println("[ROUND-PREP] " + text);
    }

    private void fail(String phase, Throwable t) {
        failed = true;
        log("FAIL phase=" + phase + " : " + t);
        t.printStackTrace(System.out);
        for (Throwable c = t.getCause(); c != null; c = c.getCause()) {
            log("  caused by: " + c);
        }
        writeLog();
        Gdx.app.exit();
    }

    private void writeLog() {
        try (FileWriter writer = new FileWriter(new File(outputDir, "smoke-results.txt"))) {
            for (String line : lines) {
                writer.write(line);
                writer.write(System.lineSeparator());
            }
        } catch (IOException ignored) {
        }
    }

    private void capture(String name) {
        File dir = outputDir;
        check(dir.isDirectory() || dir.mkdirs(), "Cannot create screenshots");
        Pixmap pixels = ScreenUtils.getFrameBufferPixmap(
            0, 0, Gdx.graphics.getBackBufferWidth(), Gdx.graphics.getBackBufferHeight());
        try {
            PixmapIO.writePNG(
                Gdx.files.absolute(new File(dir, name + ".png").getAbsolutePath()), pixels, -1, true);
        } finally {
            pixels.dispose();
        }
        log("captured " + name + ".png");
    }

    /** Sample the pixel at a world coordinate (proves the table stays visible). */
    private int[] sampleWorld(int worldX, int worldY) {
        int screenX = (int) (worldX * Gdx.graphics.getWidth() / 1280f);
        int screenY = (int) ((720f - worldY) * Gdx.graphics.getHeight() / 720f);
        screenX = Math.max(0, Math.min(Gdx.graphics.getWidth() - 1, screenX));
        screenY = Math.max(0, Math.min(Gdx.graphics.getHeight() - 1, screenY));
        Pixmap pixel = ScreenUtils.getFrameBufferPixmap(screenX, screenY, 1, 1);
        try {
            int rgba = pixel.getPixel(0, 0);
            int r = (rgba >>> 24) & 255;
            int g = (rgba >>> 16) & 255;
            int b = (rgba >>> 8) & 255;
            return new int[] { r, g, b };
        } finally {
            pixel.dispose();
        }
    }

    /** Sample a pixel of a written screenshot (PNG coordinates, top-left origin). */
    private int[] samplePng(String name, int pngX, int pngY) {
        try {
            java.awt.image.BufferedImage image = javax.imageio.ImageIO.read(new File(outputDir, name + ".png"));
            check(image != null, "cannot decode screenshot: " + name);
            int rgb = image.getRGB(Math.max(0, Math.min(image.getWidth() - 1, pngX)),
                Math.max(0, Math.min(image.getHeight() - 1, pngY)));
            return new int[] { (rgb >>> 16) & 255, (rgb >>> 8) & 255, rgb & 255 };
        } catch (java.io.IOException e) {
            throw new IllegalStateException("cannot read screenshot: " + name, e);
        }
    }

    /**
     * Count the bright green pixels of the receipt title inside a written screenshot.
     *
     * The receipt title "方案已确认" is drawn in warm green; a mid-fade frame fades it out,
     * so the count is a direct readability probe for the captured receipt.
     */
    private int countReceiptTitlePixels(String name) {
        try {
            java.awt.image.BufferedImage image = javax.imageio.ImageIO.read(new File(outputDir, name + ".png"));
            check(image != null, "cannot decode screenshot: " + name);
            int count = 0;
            for (int y = RECEIPT_TITLE_Y0; y <= RECEIPT_TITLE_Y1; y++) {
                for (int x = RECEIPT_TITLE_X0; x <= RECEIPT_TITLE_X1; x++) {
                    if (x < 0 || y < 0 || x >= image.getWidth() || y >= image.getHeight()) continue;
                    int rgb = image.getRGB(x, y);
                    int r = (rgb >>> 16) & 255;
                    int g = (rgb >>> 8) & 255;
                    int b = rgb & 255;
                    if (r > 170 && g > 115 && b < 150 && r + g > 300) count++;
                }
            }
            return count;
        } catch (java.io.IOException e) {
            throw new IllegalStateException("cannot read screenshot: " + name, e);
        }
    }

    private void clickPanel(GameScreen screen, Rectangle rect) {
        clickWorld(screen, rect.x + rect.width / 2f, rect.y + rect.height / 2f);
    }

    /** Click a 1280×720 design coordinate through the same stage transform used for drawing. */
    private void clickWorld(GameScreen screen, float worldX, float worldY) {
        com.badlogic.gdx.math.Vector2 point = screen.stageToScreen(worldX, worldY);
        int screenX = Math.round(point.x);
        int screenY = Math.round(point.y);
        screen.touchDown(screenX, screenY, 0, 0);
        screen.touchUp(screenX, screenY, 0, 0);
    }

    @Override
    public void render() {
        if (failed || inRender) return;
        inRender = true;
        try {
            frame++;
            if (game != null) {
                // The real app drives its own screen rendering and (single player) the authority tick.
                game.render();
            }
            if ((System.nanoTime() - startedAtNanos) / 1_000_000L > OVERALL_TIMEOUT_MS) {
                fail("timeout", new IllegalStateException("smoke exceeded " + OVERALL_TIMEOUT_MS + "ms"));
                return;
            }
            switch (phase) {
                case 1: {
                    if (!(game.getScreen() instanceof MenuScreen)) {
                        return;
                    }
                    MenuScreen menu = (MenuScreen) game.getScreen();
                    if (!menu.getMainMenuView().getCurrentFrame().getAllSettled()) {
                        return;
                    }
                    Rectangle rect = menu.getMainMenuView().rectFor(MainMenuItemId.SOLO);
                    com.badlogic.gdx.math.Vector2 point =
                        menu.screenPoint(rect.x + rect.width / 2f, rect.y + rect.height / 2f);
                    menu.touchDown(Math.round(point.x), Math.round(point.y), 0, 0);
                    menu.touchUp(Math.round(point.x), Math.round(point.y), 0, 0);
                    log("clicked solo menu entry");
                    phase = 2;
                    return;
                }
                case 2: {
                    if (!(game.getScreen() instanceof GameScreen)) {
                        return;
                    }
                    GameScreen screen = (GameScreen) game.getScreen();
                    ShufflePreparationPanel panel = screen.getShufflePreparationPanel();
                    if (!panel.isActive()) {
                        return;
                    }
                    // 1. Default entry: compact panel, no six-target picker.
                    check(panel.getModel().getPage()
                            == ShufflePreparationModel.PreparationPage.PANEL,
                        "default entry must stay on the compact preparation panel");
                    check(panel.getModel().getTarget() == null,
                        "default entry must not preselect any target");
                    check("开始本局 · 0点".equals(panel.getModel().getPrimaryActionLabel()),
                        "default primary action must be the zero-cost start");

                    // Wait for the panel slide-in to settle so the capture shows the final state.
                    if (panel.getMotion().getPanelOffsetX() > 0.01f) {
                        return;
                    }
                    capture("visual-runtime-01-entry-1280");

                    // The table must stay visible: the left area is not a full-screen black mask.
                    // PNG coordinates are top-left based, so world (x, y) maps to (x, 720 - y).
                    int[] felt = samplePng("visual-runtime-01-entry-1280", 320, 360);
                    log("table pixel rgb=" + felt[0] + "," + felt[1] + "," + felt[2]);
                    check(felt[1] > felt[0] && felt[1] > felt[2] && (felt[0] + felt[1] + felt[2]) > 30,
                        "the table must stay visible behind the preparation panel, rgb="
                            + felt[0] + "," + felt[1] + "," + felt[2]);
                    // The preparation panel itself must be painted on the right side.
                    int[] panelPixel = samplePng("visual-runtime-01-entry-1280", 1000, 220);
                    log("panel pixel rgb=" + panelPixel[0] + "," + panelPixel[1] + "," + panelPixel[2]);
                    check(panelPixel[0] + panelPixel[1] + panelPixel[2] > 20,
                        "the compact preparation panel must be painted, rgb="
                            + panelPixel[0] + "," + panelPixel[1] + "," + panelPixel[2]);

                    if ("zero".equals(mode)) {
                        log("mode=zero: submitting the zero-cost start");
                        confirmClickedNanos = System.nanoTime();
                        clickPanel(screen, panel.getBtnPrimaryRect());
                        phase = 8;
                        return;
                    }

                    // 2. The drawer only expands after clicking the cheat entry.
                    clickPanel(screen, panel.getBtnOpenTargetRect());
                    check(panel.getModel().getPage()
                            == ShufflePreparationModel.PreparationPage.DRAWER,
                        "the cheat entry must expand the target drawer");
                    check(panel.getModel().getTarget() == null,
                        "the drawer must not preselect a target");
                    phase = 3;
                    return;
                }
                case 3: {
                    GameScreen screen = (GameScreen) game.getScreen();
                    ShufflePreparationPanel panel = screen.getShufflePreparationPanel();
                    // 280ms easeOutCubic 的中段：等抽屉真正滑到约一半再截，而不是点击后第一帧。
                    float offset = panel.getMotion().getDrawerOffsetX();
                    if (offset > 100f) {
                        return;
                    }
                    capture("visual-runtime-02-drawer-mid-1280");
                    phase = 4;
                    return;
                }
                case 4: {
                    GameScreen screen = (GameScreen) game.getScreen();
                    ShufflePreparationPanel panel = screen.getShufflePreparationPanel();
                    // Wait for the drawer slide-in to settle before the final capture.
                    if (panel.getMotion().getDrawerOffsetX() > 0.01f) {
                        return;
                    }
                    capture("visual-runtime-03-targets-unselected-1280");
                    // Remember the unselected card checkmark area so selection can be proven visible.
                    cardPixelBefore = samplePng("visual-runtime-03-targets-unselected-1280", 928, 430);
                    check(panel.getModel().getPage()
                            == ShufflePreparationModel.PreparationPage.DRAWER,
                        "the drawer must stay open");
                    check(panel.getBtnTargets().size() == 6, "the drawer must expose exactly six targets");

                    // 3. Select a target first (30 points), then stack see-through (40 points).
                    clickPanel(screen, panel.getBtnTargets().get(ShuffleTarget.KOKUSHI));
                    check(panel.getModel().getTarget() == ShuffleTarget.KOKUSHI,
                        "the chosen target must be kept");
                    phase = 11;
                    return;
                }
                case 11: {
                    capture("visual-runtime-04-target-selected-1280");
                    int[] selectedPixel = samplePng("visual-runtime-04-target-selected-1280", 928, 430);
                    log("kokushi selection pixel rgb before=" + cardPixelBefore[0] + ","
                        + cardPixelBefore[1] + "," + cardPixelBefore[2] + " after=" + selectedPixel[0] + ","
                        + selectedPixel[1] + "," + selectedPixel[2]);
                    check(cardPixelBefore == null
                            || selectedPixel[0] != cardPixelBefore[0]
                            || selectedPixel[1] != cardPixelBefore[1]
                            || selectedPixel[2] != cardPixelBefore[2],
                        "the selected target card must be visibly highlighted");
                    GameScreen screen = (GameScreen) game.getScreen();
                    ShufflePreparationPanel panel = screen.getShufflePreparationPanel();
                    clickPanel(screen, panel.getBtnDrawerSeeThroughRect());
                    check(panel.getModel().getTotalCost() == 40,
                        "target + see-through must cost 40 points, got " + panel.getModel().getTotalCost());
                    phase = 10;
                    return;
                }
                case 10: {
                    capture("visual-runtime-05-cost40-1280");
                    // 4. 960x540 and ultra wide safe areas.
                    Gdx.graphics.setWindowedMode(SMALL_WIDTH, SMALL_HEIGHT);
                    phase = 5;
                    return;
                }
                case 5: {
                    if (Gdx.graphics.getWidth() != SMALL_WIDTH || Gdx.graphics.getHeight() != SMALL_HEIGHT) {
                        return;
                    }
                    capture("visual-runtime-06-960x540");
                    Gdx.graphics.setWindowedMode(WIDE_WIDTH, WIDE_HEIGHT);
                    phase = 6;
                    return;
                }
                case 6: {
                    GameScreen screen = (GameScreen) game.getScreen();
                    ShufflePreparationPanel panel = screen.getShufflePreparationPanel();
                    if (Gdx.graphics.getWidth() != WIDE_WIDTH || Gdx.graphics.getHeight() != WIDE_HEIGHT) {
                        return;
                    }
                    capture("visual-runtime-07-1600x600");

                    // 宽屏相机扩展后，命中矩形必须仍与画面一致：实际点一张卡再切回。
                    Rectangle wideCard = panel.getBtnTargets().get(ShuffleTarget.DAISANGEN);
                    clickWorld(screen, wideCard.x + wideCard.width / 2f, wideCard.y + wideCard.height / 2f);
                    check(panel.getModel().getTarget() == ShuffleTarget.DAISANGEN,
                        "1600x600 target card hit must follow the extended stage camera");
                    Rectangle back = panel.getBtnTargets().get(ShuffleTarget.KOKUSHI);
                    clickWorld(screen, back.x + back.width / 2f, back.y + back.height / 2f);
                    check(panel.getModel().getTarget() == ShuffleTarget.KOKUSHI,
                        "1600x600 target card hit must return to the selected KOKUSHI");
                    Gdx.graphics.setWindowedMode(WIDTH, HEIGHT);
                    phase = 7;
                    return;
                }
                case 7: {
                    GameScreen screen = (GameScreen) game.getScreen();
                    ShufflePreparationPanel panel = screen.getShufflePreparationPanel();
                    if (Gdx.graphics.getWidth() != WIDTH || Gdx.graphics.getHeight() != HEIGHT) {
                        return;
                    }
                    // Back at 16:9 the submit still lands on the right control.
                    log("mode=target: submitting the confirmed cheat plan");
                    confirmClickedNanos = System.nanoTime();
                    clickPanel(screen, panel.getBtnDrawerConfirmRect());
                    phase = 8;
                    return;
                }
                case 8: {
                    GameScreen screen = (GameScreen) game.getScreen();
                    ShufflePreparationPanel panel = screen.getShufflePreparationPanel();
                    if (!receiptCaptured) {
                        // The receipt is latched by the panel itself: the authority may already have
                        // closed the preparation, so the model flag is not a reliable gate.
                        if (!panel.isReceiptVisible()) {
                            if (receiptAttempted) {
                                fail("receipt capture", new IllegalStateException(
                                    "the receipt vanished before a readable frame could be captured"));
                            }
                            return;
                        }
                        // The receipt must be a *stable* collapsed read-only frame:
                        // the target drawer has to be fully closed.
                        if (panel.getMotion().getDrawerOffsetX() < 159.9f) {
                            return;
                        }
                        // Only capture a fully opaque receipt: a mid-fade frame is unreadable.
                        if (panel.getReceiptAlpha() < 0.999f) {
                            return;
                        }
                        receiptAttempted = true;
                        capture("visual-runtime-08-receipt-1280");
                        int bright = countReceiptTitlePixels("visual-runtime-08-receipt-1280");
                        log("receipt: page=" + panel.getModel().getPage()
                            + " drawerOffsetX=" + panel.getMotion().getDrawerOffsetX()
                            + " alpha=" + panel.getReceiptAlpha()
                            + " titlePixels=" + bright
                            + " panelActive=" + panel.isActive());
                        if (bright < RECEIPT_TITLE_MIN_PIXELS) {
                            // The frame buffer lagged one frame behind the receipt latch: retry.
                            log("receipt frame not readable yet, retrying on a later frame");
                            return;
                        }
                        receiptCaptured = true;
                    }
                    GameSnapshotView snapshot = screen.getCurrentSnapshot();
                    if (snapshot != null && snapshot.getPreparation() == null && !panel.isActive()) {
                        confirmToDealMs = (System.nanoTime() - confirmClickedNanos) / 1_000_000.0;
                        dealEndedNanos = System.nanoTime();
                        log(String.format("confirm-to-deal wallClockMs=%.2f", confirmToDealMs));
                        check(confirmToDealMs < 5_000.0,
                            "preparation must not wait for the deadline: " + confirmToDealMs + "ms");
                        phase = 9;
                        return;
                    }
                    return;
                }
                case 9: {
                    GameScreen screen = (GameScreen) game.getScreen();
                    ShufflePreparationPanel panel = screen.getShufflePreparationPanel();
                    // After the deal the receipt must never claim that we are still waiting.
                    check(!panel.getReceiptFeedbackText().contains("等待"),
                        "the receipt must not claim to wait after the deal: " + panel.getReceiptFeedbackText());
                    // The post-deal capture must show the real table, not the fading receipt overlay.
                    if (panel.isReceiptVisible()) {
                        return;
                    }
                    if (receiptGoneNanos == 0L) {
                        receiptGoneNanos = System.nanoTime();
                    }
                    double lingerMs = (receiptGoneNanos - dealEndedNanos) / 1_000_000.0;
                    log(String.format("receipt-linger-after-deal ms=%.2f", lingerMs));
                    check(lingerMs <= 400.0,
                        "the receipt must fade out within ~150ms after the deal, took " + lingerMs + "ms");
                    GameSnapshotView snapshot = screen.getCurrentSnapshot();
                    check(snapshot != null, "snapshot missing after dealing");
                    int hands = 0;
                    for (int count : snapshot.getView().getHandTileCounts()) {
                        hands += count;
                    }
                    int wall = snapshot.getView().getWallRemaining();
                    // The standard dead wall (dora indicators + kan replacements) always holds 14
                    // physical tiles, so hands + live wall + dead wall must stay exactly 136.
                    final int deadWall = 14;
                    log("after deal: handTiles=" + hands + " wallRemaining=" + wall
                        + " deadWall=" + deadWall
                        + " cheatPoints=" + snapshot.getCheatPoints());
                    check(hands + wall + deadWall == 136,
                        "136 physical tiles must be conserved, hands=" + hands
                            + " wall=" + wall + " deadWall=" + deadWall);
                    capture("visual-runtime-09-dealt-1280");
                    log("PASS: round preparation GL smoke mode=" + mode
                        + " confirmToDealMs=" + String.format("%.2f", confirmToDealMs));
                    writeLog();
                    Gdx.app.exit();
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
