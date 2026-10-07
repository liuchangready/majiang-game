import com.badlogic.gdx.backends.lwjgl3.Lwjgl3Application;
import com.badlogic.gdx.backends.lwjgl3.Lwjgl3ApplicationConfiguration;

/** V4 default room/session acceptance with a real local server and OpenGL table. */
public final class RoundSessionRenderSmoke extends V4LocalRenderSmoke {
    public RoundSessionRenderSmoke() {
        super("round-session", "ROUND_SESSION_CLIENT_RENDER_PASS", false);
    }

    public static void main(String[] args) {
        Lwjgl3ApplicationConfiguration config = new Lwjgl3ApplicationConfiguration();
        config.setTitle("V4 room/session acceptance");
        config.setWindowedMode(1280, 720);
        config.setInitialVisible(false);
        config.disableAudio(true);
        config.setForegroundFPS(30);
        new Lwjgl3Application(new RoundSessionRenderSmoke(), config);
    }
}
