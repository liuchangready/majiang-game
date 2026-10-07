import com.badlogic.gdx.backends.lwjgl3.Lwjgl3Application;
import com.badlogic.gdx.backends.lwjgl3.Lwjgl3ApplicationConfiguration;

/** Legacy task name retained for its V4 real-GL shuffle fee and table assertions. */
public final class V3RenderSmoke extends V4LocalRenderSmoke {
    public V3RenderSmoke() {
        super("v4", "V4_DEFAULT_ASYNC_CLIENT_RENDER_PASS", true);
    }

    public static void main(String[] args) {
        Lwjgl3ApplicationConfiguration config = new Lwjgl3ApplicationConfiguration();
        config.setTitle("V4 default path acceptance");
        config.setWindowedMode(1280, 720);
        config.setInitialVisible(false);
        config.disableAudio(true);
        config.setForegroundFPS(30);
        new Lwjgl3Application(new V3RenderSmoke(), config);
    }
}
