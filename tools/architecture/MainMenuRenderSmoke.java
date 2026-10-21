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
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.atomic.AtomicLong;

/**
 * 独立正式主菜单 GL 冒烟：真实窗口、真实纹理、真实通知/输入路径。
 *
 * 不修改 SnapshotLauncher 业务快照顺序，也不复用它的阶段机。
 * 运行：
 *   ./gradlew -I tools/architecture/main-menu-smoke.init.gradle :lwjgl3:mainMenuRenderSmoke
 */
public final class MainMenuRenderSmoke {
    private static final String DEFAULT_OUT = "D:/majiang/snapshot/main-menu-smoke";

    public static void main(String[] args) {
        Lwjgl3ApplicationConfiguration config = new Lwjgl3ApplicationConfiguration();
        config.setTitle("疯狂动物麻将 (Crazy Animal Mahjong) - MainMenuRenderSmoke");
        config.setWindowedMode(1280, 720);
        config.setForegroundFPS(60);
        config.useVsync(true);
        new Lwjgl3Application(new SmokeListener(), config);
    }

    private static final class SmokeListener implements ApplicationListener {
        private final AtomicLong clock = new AtomicLong(0L);
        private final List<String> lines = new ArrayList<>();
        private final File outputDir = new File(System.getProperty("majiang.smoke.out", DEFAULT_OUT));
        private final Function0<Long> clockFn = new Function0<Long>() {
            @Override
            public Long invoke() {
                return clock.get();
            }
        };

        private UiAssets assets;
        private MenuScreen screen;
        private FakeNav nav;
        private int phase = 0;
        private int frame = 0;
        private boolean failed = false;
        private int baselineEntryCount = 0;

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
                Gdx.app.exit();
            }
        }

        private void phase() {
            switch (phase) {
                case 0:
                    if (Gdx.graphics.getWidth() == 1280 && Gdx.graphics.getHeight() == 720 && frame >= 2) {
                        clock.set(0L);
                        screen.getMainMenuView().update(0L);
                        capture("01_entry_000ms.png");
                        assertBackgroundVisible("01_entry_000ms.png", 690, 500, 55, 120, 20.0, 80);
                        check(screen.getMainMenuView().isEntryActive(), "entry must be active at 0ms");
                        check(!screen.getMainMenuView().isItemSettled(MainMenuItemId.SOLO), "solo not settled at 0ms");
                        log("0ms: solo drawX=" + screen.getMainMenuView().getCurrentFrame()
                            .item(MainMenuItemId.SOLO).getRect().x);
                        clock.set(240L);
                        phase = 1;
                    }
                    break;
                case 1: {
                    screen.getMainMenuView().update(240L);
                    capture("02_entry_240ms.png");
                    assertBackgroundVisible("02_entry_240ms.png", 690, 500, 55, 120, 20.0, 80);
                    check(!screen.getMainMenuView().isItemSettled(MainMenuItemId.SOLO), "solo not settled at 240ms");
                    int before = nav.localLobbyCalls;
                    tapWorld(972f, 520f); // 最终矩形中心，仍在滑动，不得误触
                    check(nav.localLobbyCalls == before, "clicking final solo rect during slide must not launch");
                    log("240ms: click at final solo center correctly ignored");
                    clock.set(540L);
                    phase = 2;
                    break;
                }
                case 2: {
                    screen.getMainMenuView().update(540L);
                    capture("03_entry_540ms.png");
                    assertBackgroundVisible("03_entry_540ms.png", 690, 500, 55, 120, 20.0, 80);
                    check(screen.getMainMenuView().isItemSettled(MainMenuItemId.SOLO), "solo settled at 540ms");
                    check(!screen.getMainMenuView().isItemSettled(MainMenuItemId.ONLINE), "online not settled at 540ms");
                    int remoteBefore = nav.remoteLobbyCalls;
                    tapWorld(972f, 428f);
                    check(nav.remoteLobbyCalls == remoteBefore, "online must still be disabled at 540ms");
                    tapWorld(972f, 520f);
                    check(nav.localLobbyCalls == 1, "settled solo must launch at 540ms");
                    log("540ms: solo independent enable verified; online still gated");
                    clock.set(940L);
                    phase = 3;
                    break;
                }
                case 3: {
                    screen.getMainMenuView().update(940L);
                    capture("04_entry_940ms.png");
                    assertBackgroundVisible("04_entry_940ms.png", 690, 500, 55, 120, 20.0, 80);
                    assertNoOpaqueGlow("04_entry_940ms.png");
                    check(screen.getMainMenuView().getCurrentFrame().getAllSettled(), "all entries settled at 940ms");
                    baselineEntryCount = screen.getMainMenuView().getEntryPlayCount();

                    screen.getMainMenuView().restoreFocus(MainMenuItemId.ONLINE);
                    renderNow();
                    capture("05_focus_online.png");
                    assertBackgroundVisible("05_focus_online.png", 690, 500, 55, 120, 20.0, 80);
                    check(screen.getMainMenuView().getFocusedItemId() == MainMenuItemId.ONLINE,
                        "focus ring must be on online for focus screenshot");

                    tapItem(MainMenuItemId.ONLINE);
                    check(screen.getCurrentState() == MenuScreen.MenuState.TCP_CONNECT_DIALOG,
                        "online entry must reach TCP dialog");
                    screen.keyDown(Input.Keys.ESCAPE);
                    check(screen.getCurrentState() == MenuScreen.MenuState.MAIN, "ESC must return to MAIN");

                    SkinId beforeSkin = assets.getSkinManager().getCurrentSkinId();
                    tapItem(MainMenuItemId.SKIN);
                    check(assets.getSkinManager().getCurrentSkinId() != beforeSkin, "skin must update immediately");
                    renderNow();
                    capture("08_skin_updated_1280.png");
                    assertBackgroundVisible("08_skin_updated_1280.png", 690, 500, 55, 120, 20.0, 80);

                    screen.getMainMenuView().restoreFocus(MainMenuItemId.SECRETS);
                    screen.keyDown(Input.Keys.ENTER);
                    check(screen.getCurrentState() == MenuScreen.MenuState.SECRETS_MODAL,
                        "secrets overlay must open from keyboard");
                    settleTargetSlide();
                    capture("06_secrets_overlay.png");
                    assertOverlayBaseVisible("06_secrets_overlay.png");
                    int localWhileOverlay = nav.localLobbyCalls;
                    tapItem(MainMenuItemId.SOLO);
                    check(nav.localLobbyCalls == localWhileOverlay,
                        "overlay must gate underlying menu clicks");
                    screen.keyDown(Input.Keys.ESCAPE);
                    check(screen.getCurrentState() == MenuScreen.MenuState.MAIN, "ESC must close overlay");
                    check(screen.getMainMenuView().getFocusedItemId() == MainMenuItemId.SECRETS,
                        "overlay close must restore focus");
                    check(screen.getMainMenuView().getEntryPlayCount() == baselineEntryCount,
                        "overlay close must not replay entry");

                    screen.getMainMenuView().restoreFocus(MainMenuItemId.SETTINGS);
                    screen.keyDown(Input.Keys.ENTER);
                    check(screen.getCurrentState() == MenuScreen.MenuState.SETTINGS_MODAL,
                        "settings overlay must open");
                    settleTargetSlide();
                    capture("07_settings_overlay.png");
                    assertOverlayBaseVisible("07_settings_overlay.png");
                    screen.keyDown(Input.Keys.ESCAPE);
                    check(screen.getCurrentState() == MenuScreen.MenuState.MAIN, "settings overlay must close");

                    tapItem(MainMenuItemId.EXIT);
                    check(nav.exitCalls == 1, "exit entry must call navigation.exitGame");
                    log("six entry callbacks exercised: solo/online/skin/secrets/settings/exit");

                    screen.getMainMenuView().beginEntry(5000L);
                    clock.set(5240L);
                    screen.getMainMenuView().update(5240L);
                    check(screen.getMainMenuView().isEntryActive(), "explicit replay must restart entry");
                    renderNow();
                    capture("13_reentry_240ms.png");
                    assertBackgroundVisible("13_reentry_240ms.png", 690, 500, 55, 120, 20.0, 80);
                    check(screen.getMainMenuView().getEntryPlayCount() == baselineEntryCount + 1,
                        "reentry must increment the entry play count exactly once");
                    clock.set(6000L);
                    screen.getMainMenuView().update(6000L);
                    phase = 4;
                    break;
                }
                case 4:
                    Gdx.graphics.setWindowedMode(960, 540);
                    phase = 5;
                    break;
                case 5:
                    if (Gdx.graphics.getWidth() == 960 && Gdx.graphics.getHeight() == 540) {
                        screen.resize(960, 540);
                        renderNow();
                        capture("09_960x540.png");
                        assertBackgroundVisible("09_960x540.png", 520, 380, 35, 80, 20.0, 40);
                        SkinId before = assets.getSkinManager().getCurrentSkinId();
                        tapItem(MainMenuItemId.SKIN);
                        check(assets.getSkinManager().getCurrentSkinId() != before,
                            "960x540 input must align with drawing");
                        renderNow();
                        capture("10_960x540_skin_updated.png");
                        assertBackgroundVisible("10_960x540_skin_updated.png", 520, 380, 35, 80, 20.0, 40);
                        log("960x540: skin click aligned after resize; viewport="
                            + screen.getViewport().getScreenWidth() + "x"
                            + screen.getViewport().getScreenHeight());
                        phase = 6;
                    }
                    break;
                case 6:
                    Gdx.graphics.setWindowedMode(1560, 720);
                    phase = 7;
                    break;
                case 7:
                    if (Gdx.graphics.getWidth() == 1560 && Gdx.graphics.getHeight() == 720) {
                        screen.resize(1560, 720);
                        renderNow();
                        capture("11_ultrawide_1560x720.png");
                        assertBackgroundVisible("11_ultrawide_1560x720.png", 830, 500, 55, 120, 20.0, 80);
                        assertBackgroundVisible("11_ultrawide_1560x720.png", 30, 250, 80, 200, 15.0, 80);
                        check(screen.getViewport().getScreenWidth() == 1280,
                            "19.5:9 content viewport must remain 1280 wide (16:9)");
                        check(screen.getViewport().getScreenX() == 140,
                            "19.5:9 content must be centered inside the scene");
                        tapItem(MainMenuItemId.SETTINGS);
                        check(screen.getCurrentState() == MenuScreen.MenuState.SETTINGS_MODAL,
                            "ultrawide input must align with drawing");
                        settleTargetSlide();
                        capture("12_ultrawide_settings.png");
                        assertBackgroundVisible("12_ultrawide_settings.png", 236, 450, 100, 100, 8.0, 80);
                        assertBackgroundVisible("12_ultrawide_settings.png", 1230, 200, 90, 80, 8.0, 20);
                        screen.keyDown(Input.Keys.ESCAPE);
                        log("ultrawide: viewport=1280x720 centered at x=140, input aligned");
                        phase = 8;
                    }
                    break;
                case 8: {
                    screen.dispose();
                    FakeNav reducedNav = new FakeNav();
                    clock.set(9_000L);
                    MenuScreen reduced = new MenuScreen(assets, reducedNav, null, false, clockFn);
                    reduced.show();
                    reduced.resize(Gdx.graphics.getWidth(), Gdx.graphics.getHeight());
                    reduced.render(1f / 60f);
                    check(!reduced.getMainMenuView().isEntryActive(), "reduced motion must not be active");
                    check(reduced.getMainMenuView().getCurrentFrame().getAllSettled(),
                        "reduced motion must settle immediately");
                    screen = reduced;
                    nav = reducedNav;
                    capture("14_reduced_motion.png");
                    assertBackgroundVisible("14_reduced_motion.png", 830, 500, 55, 120, 20.0, 80);
                    tapItem(MainMenuItemId.SOLO);
                    check(reducedNav.localLobbyCalls == 1, "reduced motion must be immediately operable");
                    log("reduced motion: immediate final state and click verified");
                    phase = 9;
                    break;
                }
                case 9:
                    log("PASS: all MainMenuRenderSmoke checks completed");
                    writeLog();
                    Gdx.app.exit();
                    break;
                default:
                    break;
            }
        }

        private void tapItem(MainMenuItemId id) {
            Rectangle rect = screen.getMainMenuView().rectFor(id);
            tapWorld(rect.x + rect.width / 2f, rect.y + rect.height / 2f);
        }

        private void renderNow() {
            screen.render(1f / 60f);
        }

        /**
         * 目标弹窗现在是 280ms 轻滑入：先渲染第一帧启动动画，再推进虚拟时钟到终态，
         * 保持既有“稳定底图 + 覆盖弹窗”像素断言针对落位后的弹窗。
         */
        private void settleTargetSlide() {
            renderNow();
            clock.addAndGet(300L);
            renderNow();
        }

        private void tapWorld(float worldX, float worldY) {
            Vector2 point = screen.screenPoint(worldX, worldY);
            int x = Math.round(point.x);
            int y = Math.round(point.y);
            screen.touchDown(x, y, 0, 0);
            screen.touchUp(x, y, 0, 0);
        }

        private void assertBackgroundVisible(
            String name, int x, int y, int width, int height, double minAverageLuma, int minUniqueColors
        ) {
            BufferedImage image = readScreenshot(name);
            double sum = 0.0;
            int samples = 0;
            Set<Integer> colors = new HashSet<>();
            for (int yy = y; yy < y + height; yy += 2) {
                for (int xx = x; xx < x + width; xx += 2) {
                    int rgb = image.getRGB(xx, yy) & 0xFFFFFF;
                    int r = (rgb >> 16) & 0xFF;
                    int g = (rgb >> 8) & 0xFF;
                    int b = rgb & 0xFF;
                    sum += 0.299 * r + 0.587 * g + 0.114 * b;
                    samples++;
                    colors.add(rgb);
                }
            }
            double average = sum / samples;
            check(average >= minAverageLuma,
                name + " background region is too dark: avgLuma=" + average + " at " + x + "," + y);
            check(colors.size() >= minUniqueColors,
                name + " background region lost scene detail: uniqueColors=" + colors.size());
            log("PIXEL " + name + " background region (" + x + "," + y + "," + width + "x" + height
                + ") avgLuma=" + String.format("%.1f", average) + " uniqueColors=" + colors.size());
        }

        private void assertOverlayBaseVisible(String name) {
            // 0.75 黑遮罩下仍应能看到左侧吉祥物/场景与右侧菜单底板，而不是纯黑。
            assertBackgroundVisible(name, 150, 450, 100, 100, 8.0, 80);
            assertBackgroundVisible(name, 1090, 200, 90, 80, 8.0, 20);
        }

        private void assertNoOpaqueGlow(String name) {
            BufferedImage image = readScreenshot(name);
            int greenRaw = countNearColor(image, 96, 150, 530, 506, 38, 122, 92, 12);
            int goldRaw = countNearColor(image, 1220, 270, 40, 40, 245, 189, 79, 12);
            check(greenRaw < 100,
                name + " left ambient glow appears opaque: rawGreenPixels=" + greenRaw);
            check(goldRaw < 80,
                name + " right ambient glow appears opaque: rawGoldPixels=" + goldRaw);
            log("PIXEL " + name + " glow raw-color pixels green=" + greenRaw + " gold=" + goldRaw);
        }

        private int countNearColor(
            BufferedImage image, int x, int y, int width, int height,
            int targetR, int targetG, int targetB, int tolerance
        ) {
            int count = 0;
            for (int yy = y; yy < y + height; yy += 2) {
                for (int xx = x; xx < x + width; xx += 2) {
                    int rgb = image.getRGB(xx, yy) & 0xFFFFFF;
                    int r = (rgb >> 16) & 0xFF;
                    int g = (rgb >> 8) & 0xFF;
                    int b = rgb & 0xFF;
                    if (Math.abs(r - targetR) <= tolerance
                        && Math.abs(g - targetG) <= tolerance
                        && Math.abs(b - targetB) <= tolerance) {
                        count++;
                    }
                }
            }
            return count;
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

        private void log(String message) {
            lines.add(message);
            System.out.println("[MainMenuRenderSmoke] " + message);
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
                System.out.println("[MainMenuRenderSmoke] failed to write log: " + e);
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
