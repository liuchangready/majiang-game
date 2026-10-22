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
import java.util.Arrays;
import java.util.List;
import java.util.concurrent.atomic.AtomicLong;

/**
 * 正式 APP 菜单点击反馈 + 单机准备早开局的真实 GL 冒烟。
 *
 * 不修改生产入口，不依赖无头状态断言冒充真实路径：
 * 1. 真实窗口/纹理下打开联机连接框、秘籍、设置，截点击后 0 / 80 / 300ms 图，
 *    断言目标弹层固定位置、相机位置与投影矩阵逐元素不变（不再有任何目标层横移）；
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
                            runFixedTarget(
                                "tcp_connect",
                                MainMenuItemId.ONLINE,
                                MenuScreen.MenuState.TCP_CONNECT_DIALOG,
                                new Rectangle(360f, 180f, 560f, 360f),
                                1_000L
                            );
                            phase = 1;
                        }
                        break;
                    case 1:
                        runFixedTarget(
                            "secrets",
                            MainMenuItemId.SECRETS,
                            MenuScreen.MenuState.SECRETS_MODAL,
                            new Rectangle(260f, 110f, 760f, 500f),
                            2_400L
                        );
                        phase = 2;
                        break;
                    case 2:
                        runFixedTarget(
                            "settings",
                            MainMenuItemId.SETTINGS,
                            MenuScreen.MenuState.SETTINGS_MODAL,
                            new Rectangle(260f, 110f, 760f, 500f),
                            3_800L
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
                        log("PASS: menu target stays fixed (no camera shift) and real single-player early deal verified");
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
         * 打开目标内容，在 0/80/300ms 各渲染并截图。
         *
         * 2026-10-22 用户更正后的语义：点击时向右滑的是**被点的按钮本身**，
         * 目标弹层固定位置、相机与投影不横移。因此这里断言：
         * 1. 相机位置与投影矩阵逐元素完全一致（任何横移都会让绘制整体平移）；
         * 2. 目标面板在三帧之间亮度一致（若整体平移 220px，面板内容会立刻变成背景）。
         */
        private void runFixedTarget(
            String name,
            MainMenuItemId menuItem,
            MenuScreen.MenuState expectedState,
            Rectangle finalPanelWorldRect,
            long startClockMs
        ) {
            float[] cameraBefore = menu.cameraStateForDiagnostics();
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
            clock.set(startClockMs + 300L);
            menu.render(1f / 60f);
            String shotFinal = name + "_300ms.png";
            capture(shotFinal);

            float[] cameraAfter = menu.cameraStateForDiagnostics();
            check(Arrays.equals(cameraBefore, cameraAfter),
                name + " camera position/projection must not shift when a target opens");
            log("FIXED " + name + " cameraShift=false (position+projection identical)");

            BufferedImage img0 = readScreenshot(shot0);
            BufferedImage imgMid = readScreenshot(shotMid);
            BufferedImage imgFinal = readScreenshot(shotFinal);

            // 目标面板固定位置：面板内部亮度在三帧之间必须一致。
            int px0 = Math.round(finalPanelWorldRect.x + 40f);
            int px1 = Math.round(finalPanelWorldRect.x + finalPanelWorldRect.width - 40f);
            int py0 = 260;
            int py1 = 440;
            double luma0 = meanLuma(img0, px0, px1, py0, py1);
            double lumaMid = meanLuma(imgMid, px0, px1, py0, py1);
            double lumaFinal = meanLuma(imgFinal, px0, px1, py0, py1);
            log("FIXED " + name + " panelLuma 0ms=" + luma0 + " 80ms=" + lumaMid + " 300ms=" + lumaFinal);
            check(Math.abs(luma0 - lumaFinal) < 4.0,
                name + " target panel must stay at a fixed position, luma drift="
                    + Math.abs(luma0 - lumaFinal));
            check(Math.abs(lumaMid - lumaFinal) < 4.0,
                name + " target panel must stay at a fixed position, luma drift="
                    + Math.abs(lumaMid - lumaFinal));

            menu.keyDown(Input.Keys.ESCAPE);
            check(menu.getCurrentState() == MenuScreen.MenuState.MAIN,
                name + " ESC must return to MAIN");
            menu.render(1f / 60f);
        }

        private double meanLuma(BufferedImage image, int x0, int x1, int y0, int y1) {
            double sum = 0.0;
            int samples = 0;
            for (int y = y0; y < y1; y += 2) {
                for (int x = x0; x < x1; x += 2) {
                    int rgb = image.getRGB(x, y);
                    sum += 0.299 * ((rgb >> 16) & 0xFF)
                        + 0.587 * ((rgb >> 8) & 0xFF)
                        + 0.114 * (rgb & 0xFF);
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
