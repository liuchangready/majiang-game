import com.badlogic.gdx.ApplicationListener;
import com.badlogic.gdx.Gdx;
import com.badlogic.gdx.Input;
import com.badlogic.gdx.backends.lwjgl3.Lwjgl3Application;
import com.badlogic.gdx.backends.lwjgl3.Lwjgl3ApplicationConfiguration;
import com.badlogic.gdx.files.FileHandle;
import com.badlogic.gdx.graphics.PixmapIO;
import com.badlogic.gdx.math.Rectangle;
import com.badlogic.gdx.math.Vector2;
import com.badlogic.gdx.utils.ScreenUtils;
import com.majiang.client.api.LobbySession;
import com.majiang.client.api.Receipt;
import com.majiang.client.api.ReceiptStatus;
import com.majiang.presentation.common.TableNavigation;
import com.majiang.presentation.common.UiAssets;
import com.majiang.presentation.common.render.InMemorySkinPreferences;
import com.majiang.presentation.common.render.SkinId;
import com.majiang.presentation.common.render.SkinManager;
import com.majiang.presentation.menu.MainMenuItemId;
import com.majiang.presentation.menu.MenuScreen;
import kotlin.jvm.functions.Function0;

import javax.imageio.ImageIO;
import java.awt.image.BufferedImage;
import java.io.File;
import java.io.FileWriter;
import java.io.IOException;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.atomic.AtomicLong;

/**
 * 正式主菜单“点击时按钮自身向右滑、背景稳定”的真实 GL 冒烟。
 *
 * 用户语义（2026-10-22 更正）：点击菜单入口时应当是**被点的按钮自己向右滑**，
 * 背景、标题、章鱼、其余按钮与全屏遮罩保持屏幕坐标稳定；
 * 目标弹层固定位置，相机与投影不发生横移。
 *
 * 本冒烟用真实窗口、真实纹理、真实输入路径验证：
 * 1. 皮肤项（不切页面）点击后 0 / 80 / 160 / 300ms 的**按钮底板/图标/文字整体右移**，
 *    位移量由模板匹配在像素上测得（不是读取内部 offset 数值）；
 * 2. 同一时间窗内，标题、副标题、章鱼、邻近入口与背景的像素位置保持不变；
 * 3. 打开设置/秘籍后，相机位置与投影矩阵逐元素完全一致，目标弹层像素完全不动；
 * 4. ESC 返回后没有残留位移，resize 后命中与绘制仍然对齐。
 *
 * 运行：
 *   .\gradlew.bat -I tools/architecture/menu-button-motion-smoke.init.gradle :lwjgl3:menuButtonMotionSmoke
 */
public final class MenuButtonMotionSmoke {
    private static final String DEFAULT_OUT = "D:/majiang/snapshot/menu-button-motion-smoke";

    /** 被点项的基准帧时间（毫秒）。 */
    private static final long CLICK_AT_MS = 940L;

    public static void main(String[] args) {
        Lwjgl3ApplicationConfiguration config = new Lwjgl3ApplicationConfiguration();
        config.setTitle("疯狂动物麻将 (Crazy Animal Mahjong) - MenuButtonMotionSmoke");
        config.setWindowedMode(1280, 720);
        config.setForegroundFPS(60);
        config.useVsync(true);
        new Lwjgl3Application(new SmokeListener(), config);
    }

    private static final class SmokeListener implements ApplicationListener {
        private final AtomicLong clock = new AtomicLong(0L);
        private final Function0<Long> clockFn = new Function0<Long>() {
            @Override
            public Long invoke() {
                return clock.get();
            }
        };

        private final List<String> lines = new ArrayList<>();
        private final File outputDir = new File(System.getProperty("majiang.smoke.out", DEFAULT_OUT));

        private UiAssets assets;
        private MenuScreen screen;
        private FakeNav nav;
        private int phase = 0;
        private int frame = 0;
        private boolean failed = false;

        @Override
        public void create() {
            outputDir.mkdirs();
            assets = UiAssets.Companion.createDefault(
                new SkinManager(new InMemorySkinPreferences(SkinId.WIKIPEDIA))
            );
            nav = new FakeNav();
            clock.set(0L);
            screen = new MenuScreen(assets, nav, null, true, clockFn);
            screen.show();
            screen.resize(1280, 720);
            screen.getMainMenuView().beginEntry(0L);
            clock.set(CLICK_AT_MS);
            screen.getMainMenuView().update(CLICK_AT_MS);
            log("create: window=" + Gdx.graphics.getWidth() + "x" + Gdx.graphics.getHeight());
        }

        @Override
        public void resize(int width, int height) {
            if (screen != null) screen.resize(width, height);
        }

        @Override
        public void render() {
            if (failed) {
                return;
            }
            try {
                frame++;
                if (screen != null) {
                    screen.render(Gdx.graphics.getDeltaTime());
                }
                phase();
            } catch (Throwable t) {
                failed = true;
                log("FAILED at phase " + phase + ": " + t);
                for (StackTraceElement element : t.getStackTrace()) {
                    log("  at " + element);
                }
                writeLog();
                // 冒烟失败必须让 Gradle 任务失败，不能只打一条日志就退出 0。
                Gdx.app.exit();
                Runtime.getRuntime().halt(1);
            }
        }

        private void phase() {
            switch (phase) {
                case 0:
                    if (Gdx.graphics.getWidth() == 1280 && Gdx.graphics.getHeight() == 720 && frame >= 2) {
                        runButtonMotion();
                        phase = 1;
                    }
                    break;
                case 1:
                    runOverlayCase();
                    phase = 2;
                    break;
                case 2:
                    runEscapeAndResizeCase();
                    phase = 3;
                    break;
                case 3:
                    log("PASS: menu button motion verified (button slides right, background stays, camera never shifts)");
                    writeLog();
                    Gdx.app.exit();
                    break;
                default:
                    break;
            }
        }

        /**
         * 主证据：皮肤项点击后按钮自身向右滑。
         */
        private void runButtonMotion() {
            Rectangle skin = screen.getMainMenuView().rectFor(MainMenuItemId.SKIN);
            float cx = skin.x + skin.width / 2f;
            float cy = skin.y + skin.height / 2f;

            // 让基准帧与点击后的帧处于同一 hover/focus 状态，避免把高亮差异误判成位移。
            Vector2 hover = screen.screenPoint(cx, cy);
            screen.mouseMoved(Math.round(hover.x), Math.round(hover.y));
            screen.getMainMenuView().restoreFocus(MainMenuItemId.SKIN);
            check(screen.getMainMenuView().getHoveredItemId() == MainMenuItemId.SKIN,
                "skin item must be hovered for the reference frame");

            renderNow();
            String reference = "01_skin_settled_reference.png";
            capture(reference);
            BufferedImage ref = readScreenshot(reference);

            float[] cameraBefore = screen.cameraStateForDiagnostics();

            SkinId skinBefore = assets.getSkinManager().getCurrentSkinId();
            tapWorld(cx, cy);
            check(assets.getSkinManager().getCurrentSkinId() != skinBefore,
                "skin callback must run synchronously on click");
            check(screen.getMainMenuView().getButtonMotionItemId() == MainMenuItemId.SKIN,
                "the clicked item must own the motion state");

            clock.set(CLICK_AT_MS);
            renderNow();
            String shot0 = "02_skin_click_000ms.png";
            capture(shot0);

            clock.set(CLICK_AT_MS + 80L);
            renderNow();
            String shot80 = "03_skin_click_080ms.png";
            capture(shot80);

            clock.set(CLICK_AT_MS + 160L);
            renderNow();
            String shot160 = "04_skin_click_160ms.png";
            capture(shot160);

            clock.set(CLICK_AT_MS + 300L);
            renderNow();
            String shot300 = "05_skin_click_300ms.png";
            capture(shot300);

            BufferedImage img0 = readScreenshot(shot0);
            BufferedImage img80 = readScreenshot(shot80);
            BufferedImage img160 = readScreenshot(shot160);
            BufferedImage img300 = readScreenshot(shot300);

            // 按钮底板 + 图标区域（皮肤项左缘到图标结束，世界坐标 752..840）。
            final int bx0 = 752;
            final int bx1 = 840;
            final int by0 = 344;
            final int by1 = 402;

            int shift0 = estimateShift(img0, ref, bx0, bx1, by0, by1, 80, 1.0);
            int shift80 = estimateShift(img80, ref, bx0, bx1, by0, by1, 80, 1.0);
            int shift160 = estimateShift(img160, ref, bx0, bx1, by0, by1, 80, 1.0);
            int shift300 = estimateShift(img300, ref, bx0, bx1, by0, by1, 80, 1.0);

            log("MOTION skin plate/icon shiftPx 0ms=" + shift0 + " 80ms=" + shift80
                + " 160ms=" + shift160 + " 300ms=" + shift300);
            check(Math.abs(shift0) <= 3, "0ms must not have moved, measured " + shift0);
            check(shift80 >= 40 && shift80 <= 60, "80ms must be mid-flight, measured " + shift80);
            check(shift160 >= 48 && shift160 <= 64, "160ms must be at the peak, measured " + shift160);
            check(Math.abs(shift300) <= 3, "300ms must be back at rest, measured " + shift300);

            // 固定区域（标题/副标题/章鱼/邻近入口/背景）像素位置必须不动。
            double driftTitle = meanAbsDiff(img80, ref, 64, 684, 32, 108);
            double driftSubtitle = meanAbsDiff(img80, ref, 72, 672, 120, 146);
            double driftMascot = meanAbsDiff(img80, ref, 96, 626, 150, 656);
            double driftNeighbour = meanAbsDiff(img80, ref, 752, 1192, 414, 472);
            double driftBackground = meanAbsDiff(img80, ref, 0, 90, 300, 420);
            log("STATIC 80ms drift title=" + fmt(driftTitle) + " subtitle=" + fmt(driftSubtitle)
                + " mascot=" + fmt(driftMascot) + " neighbour=" + fmt(driftNeighbour)
                + " background=" + fmt(driftBackground));
            check(driftTitle < 3.0, "title must not move, drift=" + driftTitle);
            check(driftSubtitle < 3.0, "subtitle must not move, drift=" + driftSubtitle);
            check(driftMascot < 3.0, "mascot must not move, drift=" + driftMascot);
            check(driftNeighbour < 3.0, "neighbour entry must not move, drift=" + driftNeighbour);
            check(driftBackground < 3.0, "background must not move, drift=" + driftBackground);

            // 对照：按钮区域本身必须明显变化（证明确实动了，而不是整屏没动）。
            double buttonDrift = meanAbsDiff(img80, ref, bx0, bx1, by0, by1);
            log("STATIC 80ms button region drift=" + fmt(buttonDrift));
            check(buttonDrift > 6.0, "the clicked button region must visibly change, drift=" + buttonDrift);

            // 相机位置与投影在点击前后完全一致。
            float[] cameraAfter = screen.cameraStateForDiagnostics();
            check(Arrays.equals(cameraBefore, cameraAfter), "camera must not move when clicking a menu entry");
            log("CAMERA click: position+projection unchanged (" + cameraBefore.length + " floats)");
        }

        /**
         * 覆盖层证据：目标弹层固定位置，相机/投影不横移。
         */
        private void runOverlayCase() {
            float[] cameraBefore = screen.cameraStateForDiagnostics();

            Rectangle settings = screen.getMainMenuView().rectFor(MainMenuItemId.SETTINGS);
            tapWorld(settings.x + settings.width / 2f, settings.y + settings.height / 2f);
            check(screen.getCurrentState() == MenuScreen.MenuState.SETTINGS_MODAL,
                "settings overlay must open");
            check(screen.getMainMenuView().getButtonMotionItemId() == MainMenuItemId.SETTINGS,
                "the settings entry owns the motion while the overlay is open");
            check(screen.getMainMenuView().getFocusedItemId() == MainMenuItemId.SETTINGS,
                "the clicked entry keeps the focus ring while the overlay is open");

            clock.set(CLICK_AT_MS + 300L);
            renderNow();
            String open0 = "06_settings_open_000ms.png";
            capture(open0);
            float alpha0 = screen.getOverlayRevealAlpha();

            clock.set(CLICK_AT_MS + 380L);
            renderNow();
            String open80 = "07_settings_open_080ms.png";
            capture(open80);
            float alpha80 = screen.getOverlayRevealAlpha();

            clock.set(CLICK_AT_MS + 620L);
            renderNow();
            String settled = "08_settings_settled.png";
            capture(settled);

            BufferedImage img0 = readScreenshot(open0);
            BufferedImage img80 = readScreenshot(open80);
            BufferedImage imgSettled = readScreenshot(settled);

            // 相机位置与投影逐元素一致：打开弹层不会横移任何像素。
            float[] cameraAfter = screen.cameraStateForDiagnostics();
            check(Arrays.equals(cameraBefore, cameraAfter),
                "camera must not move when an overlay opens");
            log("CAMERA overlay: position+projection unchanged (" + cameraAfter.length + " floats)");

            // 目标弹层面板像素必须完全不动（面板不透明，固定位置）。
            double panelDiff = maxAbsDiff(img0, imgSettled, 300, 700, 200, 520);
            double panelDiff2 = maxAbsDiff(img80, imgSettled, 300, 700, 200, 520);
            log("OVERLAY panel maxAbsDiff 0ms=" + panelDiff + " 80ms=" + panelDiff2);
            check(panelDiff == 0.0, "the target panel must not move at all, diff=" + panelDiff);
            check(panelDiff2 == 0.0, "the target panel must not move at all, diff=" + panelDiff2);

            // 遮罩淡入期间，弹层之外的菜单确实在变化（说明视觉衔接存在，而不是整屏静止）。
            double dimmerDiff = meanAbsDiff(img0, imgSettled, 1030, 1180, 414, 472);
            log("OVERLAY dimmer region meanAbsDiff=" + fmt(dimmerDiff)
                + " revealAlpha 0ms=" + alpha0 + " 80ms=" + alpha80);
            check(dimmerDiff > 1.0, "the dimmer must fade in over the menu, diff=" + dimmerDiff);

            // 被点的设置项在覆盖层打开时也向右滑。
            // 遮罩是黑色乘算（最终不透明度 = 0.75 * 遮罩淡入 alpha），先按该系数归一化；
            // 追踪高对比度的金色焦点框右缘（0ms 在 x≈1189，位移后随按钮一起右移），
            // 避免低对比底板边缘带来的歧义。
            double dimmerFactor = Math.max(1e-3, 1.0 - 0.75 * alpha80);
            double scale = 1.0 / dimmerFactor;
            int shift = estimateShift(img80, img0, 1175, 1255, 486, 540, 80, scale);
            log("OVERLAY settings focus-ring shiftPx=" + shift
                + " (dimmerFactor=" + fmt(dimmerFactor) + ", scale=" + fmt(scale) + ")");
            check(shift >= 40 && shift <= 60,
                "the clicked entry must also slide right under the overlay, measured " + shift);
        }

        /**
         * ESC 返回与 resize：不得残留位移，命中与绘制仍对齐。
         */
        private void runEscapeAndResizeCase() {
            screen.keyDown(Input.Keys.ESCAPE);
            check(screen.getCurrentState() == MenuScreen.MenuState.MAIN, "ESC must close the overlay");
            renderNow();

            Rectangle settings = screen.getMainMenuView().rectFor(MainMenuItemId.SETTINGS);
            Rectangle settingsFrame = screen.getMainMenuView().itemRect(MainMenuItemId.SETTINGS);
            check(settingsFrame != null, "settings frame rect must exist");
            check(Math.abs(settingsFrame.x - MainMenuLayoutBase.SETTINGS_X) < 0.01,
                "no residual offset after ESC, x=" + settingsFrame.x);

            screen.resize(960, 540);
            renderNow();
            String small = "09_resized_960x540.png";
            capture(small);

            tapWorld(settings.x + settings.width / 2f, settings.y + settings.height / 2f);
            check(screen.getCurrentState() == MenuScreen.MenuState.SETTINGS_MODAL,
                "960x540 click must still hit the entry");
            clock.set(CLICK_AT_MS + 700L);
            renderNow();
            String smallMoved = "10_resized_960x540_moved.png";
            capture(smallMoved);
            check(screen.getMainMenuView().getButtonMotionItemId() == MainMenuItemId.SETTINGS,
                "the resized menu must still run the button motion");
            check(screen.getMainMenuView().getButtonMotionOffsetX() > 0f,
                "the resized menu must still slide the clicked entry to positive X");
            log("RESIZE 960x540: click aligned, motion offset="
                + screen.getMainMenuView().getButtonMotionOffsetX());
        }

        private void renderNow() {
            screen.render(1f / 60f);
        }

        private void tapWorld(float worldX, float worldY) {
            Vector2 point = screen.screenPoint(worldX, worldY);
            int x = Math.round(point.x);
            int y = Math.round(point.y);
            screen.touchDown(x, y, 0, 0);
            screen.touchUp(x, y, 0, 0);
        }

        /**
         * 在 test 图中寻找 dx，使 testScale * test(x+dx) ≈ reference(x)。
         * 参考图是按钮静止时的画面；testScale 用于把被黑色遮罩压暗的 test 还原回同一亮度。
         */
        private int estimateShift(
            BufferedImage test, BufferedImage reference,
            int x0, int x1, int y0, int y1, int maxDx, double testScale
        ) {
            double bestError = Double.MAX_VALUE;
            int bestDx = -1;
            for (int dx = 0; dx <= maxDx; dx++) {
                double error = 0.0;
                int samples = 0;
                for (int y = y0; y <= y1; y += 2) {
                    for (int x = x0; x <= x1; x += 2) {
                        int tx = x + dx;
                        if (tx < 0 || tx >= test.getWidth() || x < 0 || x >= reference.getWidth()) continue;
                        int a = test.getRGB(tx, y);
                        int b = reference.getRGB(x, y);
                        error += Math.abs(testScale * ((a >> 16) & 0xFF) - ((b >> 16) & 0xFF));
                        error += Math.abs(testScale * ((a >> 8) & 0xFF) - ((b >> 8) & 0xFF));
                        error += Math.abs(testScale * (a & 0xFF) - (b & 0xFF));
                        samples++;
                    }
                }
                if (samples == 0) continue;
                double average = error / samples;
                if (average < bestError) {
                    bestError = average;
                    bestDx = dx;
                }
            }
            return bestDx;
        }

        private double meanAbsDiff(
            BufferedImage a, BufferedImage b,
            int x0, int x1, int y0, int y1
        ) {
            double sum = 0.0;
            int samples = 0;
            for (int y = y0; y <= y1; y += 2) {
                for (int x = x0; x <= x1; x += 2) {
                    int pa = a.getRGB(x, y);
                    int pb = b.getRGB(x, y);
                    sum += Math.abs(((pa >> 16) & 0xFF) - ((pb >> 16) & 0xFF));
                    sum += Math.abs(((pa >> 8) & 0xFF) - ((pb >> 8) & 0xFF));
                    sum += Math.abs((pa & 0xFF) - (pb & 0xFF));
                    samples++;
                }
            }
            return sum / Math.max(1, samples);
        }

        private double maxAbsDiff(
            BufferedImage a, BufferedImage b,
            int x0, int x1, int y0, int y1
        ) {
            double max = 0.0;
            for (int y = y0; y <= y1; y++) {
                for (int x = x0; x <= x1; x++) {
                    int pa = a.getRGB(x, y);
                    int pb = b.getRGB(x, y);
                    int dr = Math.abs(((pa >> 16) & 0xFF) - ((pb >> 16) & 0xFF));
                    int dg = Math.abs(((pa >> 8) & 0xFF) - ((pb >> 8) & 0xFF));
                    int db = Math.abs((pa & 0xFF) - (pb & 0xFF));
                    max = Math.max(max, Math.max(dr, Math.max(dg, db)));
                }
            }
            return max;
        }

        private BufferedImage readScreenshot(String name) {
            try {
                BufferedImage image = ImageIO.read(new File(outputDir, name));
                check(image != null, "cannot decode screenshot: " + name);
                return image;
            } catch (IOException e) {
                throw new IllegalStateException("cannot read screenshot: " + name, e);
            }
        }

        private void capture(String name) {
            int width = Gdx.graphics.getBackBufferWidth();
            int height = Gdx.graphics.getBackBufferHeight();
            File file = new File(outputDir, name);
            com.badlogic.gdx.graphics.Pixmap pixmap = ScreenUtils.getFrameBufferPixmap(0, 0, width, height);
            PixmapIO.writePNG(new FileHandle(file), pixmap, -1, true);
            pixmap.dispose();
            check(file.length() > 1024, "screenshot must not be empty: " + file);
            log("SHOT " + file.getAbsolutePath() + " (" + width + "x" + height + ", "
                + file.length() + " bytes)");
        }

        private void check(boolean condition, String message) {
            if (!condition) {
                throw new IllegalStateException(message);
            }
        }

        private String fmt(double value) {
            return String.format("%.2f", value);
        }

        private void log(String message) {
            lines.add(message);
            System.out.println("[MenuButtonMotionSmoke] " + message);
        }

        private void writeLog() {
            try {
                outputDir.mkdirs();
                try (FileWriter writer = new FileWriter(new File(outputDir, "smoke-results.txt"))) {
                    for (String line : lines) {
                        writer.write(line);
                        writer.write(System.lineSeparator());
                    }
                }
            } catch (IOException e) {
                System.out.println("[MenuButtonMotionSmoke] failed to write log: " + e);
            }
        }

        @Override
        public void pause() {
            if (screen != null) screen.pause();
        }

        @Override
        public void resume() {
            if (screen != null) screen.resume();
        }

        @Override
        public void dispose() {
            if (screen != null) {
                screen.dispose();
                screen = null;
            }
            if (assets != null) {
                assets.dispose();
                assets = null;
            }
            writeLog();
        }
    }

    /** 菜单布局常量（避免在冒烟里重复硬编码设计坐标）。 */
    private static final class MainMenuLayoutBase {
        static final float SETTINGS_X = 752f;
    }

    private static final class FakeNav implements TableNavigation {
        int localLobbyCalls = 0;
        int remoteLobbyCalls = 0;
        int playerTableCalls = 0;
        int spectatorTableCalls = 0;
        int exitCalls = 0;

        @Override
        public CompletableFuture<LobbySession> openLocalLobby() {
            localLobbyCalls++;
            return CompletableFuture.failedFuture(new IllegalStateException("smoke local start failure"));
        }

        @Override
        public CompletableFuture<LobbySession> openRemoteLobby(String host, int port) {
            remoteLobbyCalls++;
            return CompletableFuture.failedFuture(new IllegalStateException("smoke remote disabled"));
        }

        @Override
        public void showPlayerTable() {
            playerTableCalls++;
        }

        @Override
        public void showSpectatorTable() {
            spectatorTableCalls++;
        }

        @Override
        public CompletableFuture<Receipt> leaveToMenu() {
            return CompletableFuture.completedFuture(
                new Receipt("leave", ReceiptStatus.APPLIED, null, null)
            );
        }

        @Override
        public void exitGame() {
            exitCalls++;
        }
    }
}
