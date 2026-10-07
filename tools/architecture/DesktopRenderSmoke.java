import com.badlogic.gdx.backends.lwjgl3.Lwjgl3Application;
import com.badlogic.gdx.backends.lwjgl3.Lwjgl3ApplicationConfiguration;

/** Real OpenGL check of the V4 product entry, physical tile input, AI, and release. */
public final class DesktopRenderSmoke extends V4LocalRenderSmoke {
    public DesktopRenderSmoke() {
        super("desktop", "RENDER_SMOKE_PASS", false);
    }

    public static void main(String[] args) {
        Lwjgl3ApplicationConfiguration config = new Lwjgl3ApplicationConfiguration();
        config.setTitle("Architecture V4 render smoke");
        config.setWindowedMode(1280, 720);
        config.setInitialVisible(false);
        config.disableAudio(true);
        config.setForegroundFPS(30);
        new Lwjgl3Application(new DesktopRenderSmoke(), config);
    }
}
