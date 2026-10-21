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
import com.majiang.MahjongGame;
import com.majiang.client.api.GameSnapshotView;
import com.majiang.presentation.common.UiAssets;
import com.majiang.presentation.common.render.InMemorySkinPreferences;
import com.majiang.presentation.common.render.SkinId;
import com.majiang.presentation.common.render.SkinManager;
import com.majiang.presentation.game.GameScreen;
import com.majiang.presentation.game.ShufflePreparationPanel;
import com.majiang.presentation.menu.MainMenuItemId;
import com.majiang.presentation.menu.MenuScreen;
import kotlin.jvm.functions.Function0;

import javax.imageio.ImageIO;
import java.awt.image.BufferedImage;
import java.io.File;
import java.io.FileWriter;
import java.io.IOException;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.atomic.AtomicLong;

/**
 * 正式 APP 菜单点击滑入 + 单机准备早开局的真实 GL 冒烟。
 *
 * 不修改生产入口，不依赖无头状态断言冒充真实路径：
 * 1. 真实窗口/纹理下打开联机连接框、秘籍、设置，截点击后 0 / 80 / 280ms 图，
 *    用模板匹配测量目标内容相对终态底图的像素位移，并检查背景区域没有整屏移动；
 * 2. 真实 MahjongGame 单机启动（点击“单机对局”），进入准备面板后点击“不使用千术”，
 *    用墙钟记录从点击到权威 preparation 结束、遮罩消失、牌桌可见的实际耗时。
 *
 * 运行：
 *   .\gradlew.bat -I tools/architecture/menu-click-preparation-smoke.init.gradle :lwjgl3:menuClickPreparationSmoke
 */
public final class MenuClickPreparationSmoke {
    private static final String DEFAULT_OUT = "D:/majiang/snapshot/menu-click-preparation-smoke";
    private static final long OVERALL_TIMEOUT_MS = 45_000L;

    public static void main(String[] args) {
        Lwjgl3ApplicationConfiguration config = new Lwjgl3ApplicationConfiguration();
        config.setTitle("疯狂动物麻将 (Crazy Animal Mahjong) - MenuClickPreparationSmoke");
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

        private UiAssets menuAssets;
        private MenuScreen menu;
        private FakeNav nav;
        private MahjongGame game;
        private int phase = 0;
        private int frame = 0;
        private boolean failed = false;
        private final long startedAtNanos = System.nanoTime();
        private long skipClickedNanos = 0L;
        private long skipClickedEpochMillis = 0L;
        private double measuredSkipToTableMs = -1.0;

        @Override
        public void create() {
            outputDir.mkdirs();
            log("create: window=" + Gdx.graphics.getWidth() + "x" + Gdx.graphics.getHeight());
            try {
                prepareMenu();
            } catch (Throwable t) {
                fail("create", t);
            }
        }

        private void prepareMenu() {
            menuAssets = UiAssets.Companion.createDefault(
                new SkinManager(new InMemorySkinPreferences(SkinId.WIKIPEDIA))
            );
            nav = new FakeNav();
            clock.set(0L);
            menu = new MenuScreen(menuAssets, nav, null, true, clockFn);
            menu.show();
            menu.resize(1280, 720);
            menu.getMainMenuView().beginEntry(0L);
            clock.set(940L);
            menu.getMainMenuView().update(940L);
            menu.render(1f / 60f);
            check(menu.getMainMenuView().getCurrentFrame().getAllSettled(),
                "six menu entries must be settled before click tests");
        }

        @Override
        public void resize(int width, int height) {
            if (menu != null) menu.resize(width, height);
        }

        @Override
        public void render() {
            if (failed) return;
            try {
                frame++;
                if ((System.nanoTime() - startedAtNanos) / 1_000_000L > OVERALL_TIMEOUT_MS) {
                    fail("overall timeout", new IllegalStateException("smoke exceeded " + OVERALL_TIMEOUT_MS + "ms"));
                    return;
                }
                switch (phase) {
                    case 0:
                        if (Gdx.graphics.getWidth() == 1280 && Gdx.graphics.getHeight() == 720 && frame >= 2) {
                            runSlideTarget(
                                "tcp_connect",
                                MainMenuItemId.ONLINE,
                                MenuScreen.MenuState.TCP_CONNECT_DIALOG,
                                new Rectangle(360f, 180f, 560f, 360f),
                                1_000L,
                                false
                            );
                            phase = 1;
                        }
                        break;
                    case 1:
                        runSlideTarget(
                            "secrets",
                            MainMenuItemId.SECRETS,
                            MenuScreen.MenuState.SECRETS_MODAL,
                            new Rectangle(260f, 110f, 760f, 500f),
                            2_400L,
                            true
                        );
                        phase = 2;
                        break;
                    case 2:
                        runSlideTarget(
                            "settings",
                            MainMenuItemId.SETTINGS,
                            MenuScreen.MenuState.SETTINGS_MODAL,
                            new Rectangle(260f, 110f, 760f, 500f),
                            3_800L,
                            true
                        );
                        phase = 3;
                        break;
                    case 3:
                        disposeMenu();
                        game = new MahjongGame(new SkinManager(new InMemorySkinPreferences(SkinId.WIKIPEDIA)));
                        game.create();
                        log("real MahjongGame created for single-player path");
                        phase = 4;
                        break;
                    case 4:
                        game.render();
                        if (game.getScreen() instanceof MenuScreen) {
                            MenuScreen main = (MenuScreen) game.getScreen();
                            if (main.getMainMenuView().getCurrentFrame().getAllSettled()) {
                                clickMainItem(main, MainMenuItemId.SOLO);
                                log("clicked SOLO on the real app menu");
                                phase = 5;
                            }
                        }
                        break;
                    case 5:
                        game.render();
                        if (game.getScreen() instanceof GameScreen) {
                            GameScreen table = (GameScreen) game.getScreen();
                            ShufflePreparationPanel panel = table.getShufflePreparationPanel();
                            if (panel.isActive()) {
                                check(panel.getModel().getCanSkip(),
                                    "preparation panel must allow skip before the deadline");
                                capture("prep_before_skip.png");
                                skipClickedNanos = System.nanoTime();
                                skipClickedEpochMillis = System.currentTimeMillis();
                                Rectangle skip = panel.getBtnSkipRect();
                                panel.touchDown(skip.x + skip.width / 2f, skip.y + skip.height / 2f);
                                log("clicked skip button at epochMillis=" + skipClickedEpochMillis
                                    + " round=" + snapshotRoundId());
                                phase = 6;
                            }
                        }
                        break;
                    case 6:
                        game.render();
                        if (game.getScreen() instanceof GameScreen) {
                            GameScreen table = (GameScreen) game.getScreen();
                            ShufflePreparationPanel panel = table.getShufflePreparationPanel();
                            GameSnapshotView snapshot = currentSnapshot();
                            if (!panel.isActive() && snapshot != null && snapshot.getPreparation() == null) {
                                measuredSkipToTableMs = (System.nanoTime() - skipClickedNanos) / 1_000_000.0;
                                capture("prep_after_skip.png");
                                int points = snapshot.getCheatPoints().isEmpty()
                                    ? -1
                                    : snapshot.getCheatPoints().get(0);
                                int hand = snapshot.getView().getMyAllClosedTiles().size();
                                log(String.format("PREP skip-to-table wallClockMs=%.2f", measuredSkipToTableMs));
                                log("PREP preparation=null, panelActive=false, cheatPoints[0]=" + points
                                    + ", handTiles=" + hand);
                                check(measuredSkipToTableMs < 5_000.0,
                                    "skip must not wait for the 10s deadline: " + measuredSkipToTableMs + "ms");
                                check(points == 100, "skip must keep all 100 cheat points, got " + points);
                                check(hand > 0, "dealt hand must be visible after preparation closes");
                                phase = 7;
                            }
                        }
                        break;
                    case 7:
                        log("PASS: menu slide displacement and real single-player early deal verified");
                        writeLog();
                        if (game != null) {
                            game.dispose();
                            game = null;
                        }
                        Gdx.app.exit();
                        break;
                    default:
                        break;
                }
            } catch (Throwable t) {
                fail("phase " + phase, t);
            }
        }

        /**
         * 打开目标内容，在 0/80/280ms 各渲染并截图，然后用目标终态图做模板匹配测量位移。
         */
        private void runSlideTarget(
            String name,
            MainMenuItemId menuItem,
            MenuScreen.MenuState expectedState,
            Rectangle finalPanelWorldRect,
            long startClockMs,
            boolean useDiffBoundary
        ) {
            menu.getMainMenuView().restoreFocus(menuItem);
            menu.keyDown(Input.Keys.ENTER);
            check(menu.getCurrentState() == expectedState,
                name + " must open state " + expectedState + ", got " + menu.getCurrentState());

            clock.set(startClockMs);
            menu.render(1f / 60f);
            String shot0 = name + "_00ms.png";
            capture(shot0);
            clock.set(startClockMs + 80L);
            menu.render(1f / 60f);
            String shotMid = name + "_80ms.png";
            capture(shotMid);
            clock.set(startClockMs + 280L);
            menu.render(1f / 60f);
            String shotFinal = name + "_280ms.png";
            capture(shotFinal);

            BufferedImage img0 = readScreenshot(shot0);
            BufferedImage imgMid = readScreenshot(shotMid);
            BufferedImage imgFinal = readScreenshot(shotFinal);

            // 目标终态面板内侧区域：0ms 时面板整体右移 220px，仍完全覆盖该区域。
            int x0 = Math.round(finalPanelWorldRect.x + 240f);
            int x1 = Math.round(finalPanelWorldRect.x + finalPanelWorldRect.width - 20f);
            int y0 = 260;
            int y1 = 440;
            int off0 = useDiffBoundary
                ? estimateOffsetByDiff(img0, imgFinal, Math.round(finalPanelWorldRect.x + finalPanelWorldRect.width))
                : estimateOffset(img0, imgFinal, x0, x1, y0, y1, 240);
            int offMid = useDiffBoundary
                ? estimateOffsetByDiff(imgMid, imgFinal, Math.round(finalPanelWorldRect.x + finalPanelWorldRect.width))
                : estimateOffset(imgMid, imgFinal, x0, x1, y0, y1, 240);
            int offFinal = useDiffBoundary
                ? estimateOffsetByDiff(imgFinal, imgFinal, Math.round(finalPanelWorldRect.x + finalPanelWorldRect.width))
                : estimateOffset(imgFinal, imgFinal, x0, x1, y0, y1, 240);
            double bgDrift = meanAbsDiff(img0, imgFinal, 80, 200, 260, 440);

            log("SLIDE " + name + " targetOffsetsPx 0ms=" + off0 + " 80ms=" + offMid + " 280ms=" + offFinal
                + " backgroundDrift=" + String.format("%.2f", bgDrift));
            check(Math.abs(off0 - 220) <= 4,
                name + " 0ms target must start +220px, measured " + off0);
            check(offMid > 40 && offMid < 200,
                name + " 80ms target must be mid-flight, measured " + offMid);
            check(offFinal <= 1, name + " 280ms target must land, measured " + offFinal);
            check(bgDrift < 45.0,
                name + " background must not move with the target, drift=" + bgDrift);

            menu.keyDown(Input.Keys.ESCAPE);
            check(menu.getCurrentState() == MenuScreen.MenuState.MAIN,
                name + " ESC must return to MAIN");
            menu.render(1f / 60f);
        }

        /**
         * 对不透明弹窗用“差异带右边界 - 终态右边界”求位移。
         *
         * 终态面板 [left,right]；滑入 offset 后 test 面板为 [left+offset,right+offset]。
         * 两帧在 (right, right+offset] 一为面板、一为背景，差异带的最右 x 即 right+offset-1。
         * 对 TCP 连接框这种底色与牌桌接近的目标，改用面板内部模板匹配。
         */
        private int estimateOffsetByDiff(BufferedImage test, BufferedImage reference, int finalRight) {
            int lastDiff = -1;
            for (int y = 260; y <= 440; y += 20) {
                for (int x = 0; x < test.getWidth(); x++) {
                    int a = test.getRGB(x, y);
                    int b = reference.getRGB(x, y);
                    int delta = Math.abs(((a >> 16) & 0xFF) - ((b >> 16) & 0xFF))
                        + Math.abs(((a >> 8) & 0xFF) - ((b >> 8) & 0xFF))
                        + Math.abs((a & 0xFF) - (b & 0xFF));
                    if (delta > 60) {
                        lastDiff = Math.max(lastDiff, x);
                    }
                }
            }
            if (lastDiff < 0) return 0;
            return Math.max(0, lastDiff - (finalRight - 1));
        }

        /**
         * 在 test 图中寻找 dx，使 test(x) ≈ reference(x-dx)。参考图为无位移终态。
         */
        private int estimateOffset(
            BufferedImage test, BufferedImage reference,
            int x0, int x1, int y0, int y1, int maxDx
        ) {
            double bestError = Double.MAX_VALUE;
            int bestDx = -1;
            for (int dx = 0; dx <= maxDx; dx++) {
                double error = 0.0;
                int samples = 0;
                for (int y = y0; y <= y1; y += 3) {
                    for (int x = x0; x <= x1; x += 3) {
                        int sx = x - dx;
                        if (sx < 0 || sx >= reference.getWidth()) continue;
                        int a = test.getRGB(x, y);
                        int b = reference.getRGB(sx, y);
                        error += Math.abs(((a >> 16) & 0xFF) - ((b >> 16) & 0xFF));
                        error += Math.abs(((a >> 8) & 0xFF) - ((b >> 8) & 0xFF));
                        error += Math.abs((a & 0xFF) - (b & 0xFF));
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
            for (int y = y0; y <= y1; y += 4) {
                for (int x = x0; x <= x1; x += 4) {
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

        private void clickMainItem(MenuScreen main, MainMenuItemId id) {
            Rectangle rect = main.getMainMenuView().rectFor(id);
            Vector2 point = main.screenPoint(rect.x + rect.width / 2f, rect.y + rect.height / 2f);
            int x = Math.round(point.x);
            int y = Math.round(point.y);
            main.touchDown(x, y, 0, 0);
            main.touchUp(x, y, 0, 0);
        }

        private GameSnapshotView currentSnapshot() {
            if (game == null || game.getCurrentSession() == null) return null;
            return game.getCurrentSession().getCurrentSnapshot();
        }

        private String snapshotRoundId() {
            GameSnapshotView snapshot = currentSnapshot();
            return snapshot == null ? "<null>" : snapshot.getRoundId();
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
            if (!condition) throw new IllegalStateException(message);
        }

        private void log(String message) {
            lines.add(message);
            System.out.println("[MenuClickPreparationSmoke] " + message);
        }

        private void fail(String where, Throwable t) {
            failed = true;
            log("FAILED at " + where + ": " + t);
            for (StackTraceElement element : t.getStackTrace()) {
                log("  at " + element);
            }
            writeLog();
            Gdx.app.exit();
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
                System.out.println("[MenuClickPreparationSmoke] failed to write log: " + e);
            }
        }

        private void disposeMenu() {
            if (menu != null) {
                menu.dispose();
                menu = null;
            }
            if (menuAssets != null) {
                menuAssets.dispose();
                menuAssets = null;
            }
        }

        @Override
        public void pause() {
        }

        @Override
        public void resume() {
        }

        @Override
        public void dispose() {
            disposeMenu();
            if (game != null) {
                game.dispose();
                game = null;
            }
            writeLog();
        }
    }

    private static final class FakeNav implements com.majiang.presentation.common.TableNavigation {
        @Override
        public java.util.concurrent.CompletableFuture<com.majiang.client.api.LobbySession> openLocalLobby() {
            return java.util.concurrent.CompletableFuture.failedFuture(
                new IllegalStateException("menu-only smoke"));
        }

        @Override
        public java.util.concurrent.CompletableFuture<com.majiang.client.api.LobbySession> openRemoteLobby(
            String host, int port
        ) {
            return java.util.concurrent.CompletableFuture.failedFuture(
                new IllegalStateException("menu-only smoke"));
        }

        @Override
        public void showPlayerTable() {
        }

        @Override
        public void showSpectatorTable() {
        }

        @Override
        public java.util.concurrent.CompletableFuture<com.majiang.client.api.Receipt> leaveToMenu() {
            return java.util.concurrent.CompletableFuture.completedFuture(
                new com.majiang.client.api.Receipt(
                    "leave",
                    com.majiang.client.api.ReceiptStatus.APPLIED,
                    null,
                    null
                )
            );
        }

        @Override
        public void exitGame() {
        }
    }
}
