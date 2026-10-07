import com.majiang.client.api.*;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

/** Compact builders for immutable client.api fixture views used by GL acceptance tools. */
public final class V4SmokeViews {
    private V4SmokeViews() {}

    public static TileView tile(SuitView suit, int rank, int copy) {
        int type = switch (suit) {
            case MAN -> rank - 1;
            case SOU -> 9 + rank - 1;
            case PIN -> 18 + rank - 1;
            case WIND -> 27 + rank - 1;
            case DRAGON -> 31 + rank - 1;
        };
        return TileView.Companion.fromTypeIndex(type, copy);
    }

    public static List<TileView> tiles(SuitView suit, String ranks) {
        Map<Integer, Integer> copies = new HashMap<>();
        List<TileView> result = new ArrayList<>();
        for (char c : ranks.toCharArray()) {
            int rank = c - '0';
            int copy = copies.getOrDefault(rank, 0);
            result.add(tile(suit, rank, copy));
            copies.put(rank, copy + 1);
        }
        return result;
    }

    public static Builder player(int seat) { return new Builder(seat); }

    public static final class Builder {
        private final int seat;
        private GamePhaseView phase = GamePhaseView.DISCARD;
        private int currentTurn;
        private int dealerSeat;
        private int wallRemaining = 70;
        private List<TileView> closed = Collections.emptyList();
        private TileView drawn;
        private List<LegalActionView> actions = Collections.emptyList();
        private List<TileView> history = Collections.emptyList();
        private FuritenStatusView furiten = new FuritenStatusView(false, false, false);
        private TileView lastDiscard;
        private Integer lastDiscardPlayer;
        private final List<List<TileView>> discards = new ArrayList<>();
        private final List<List<MeldView>> melds = new ArrayList<>();
        private List<Integer> scores = Arrays.asList(25000, 25000, 25000, 25000);

        private Builder(int seat) {
            this.seat = seat;
            this.currentTurn = seat;
            this.dealerSeat = seat;
            for (int i = 0; i < 4; i++) {
                discards.add(new ArrayList<>());
                melds.add(new ArrayList<>());
            }
        }
        public Builder phase(GamePhaseView value) { phase = value; return this; }
        public Builder turn(int value) { currentTurn = value; return this; }
        public Builder dealer(int value) { dealerSeat = value; return this; }
        public Builder wall(int value) { wallRemaining = value; return this; }
        public Builder closed(List<TileView> value) { closed = new ArrayList<>(value); return this; }
        public Builder drawn(TileView value) { drawn = value; return this; }
        public Builder actions(List<LegalActionView> value) { actions = new ArrayList<>(value); return this; }
        public Builder history(List<TileView> value) { history = new ArrayList<>(value); return this; }
        public Builder furiten(FuritenStatusView value) { furiten = value; return this; }
        public Builder lastDiscard(TileView value, int player) { lastDiscard = value; lastDiscardPlayer = player; return this; }
        public Builder discard(int player, TileView value) { discards.get(player).add(value); return this; }
        public Builder meld(int player, MeldView value) { melds.get(player).add(value); return this; }
        public Builder scores(List<Integer> value) { scores = new ArrayList<>(value); return this; }

        public PlayerView build() {
            List<Integer> handCounts = new ArrayList<>(Arrays.asList(13, 13, 13, 13));
            handCounts.set(seat, closed.size() + (drawn == null ? 0 : 1));
            return new PlayerView(seat, phase, 1, 1, 0, 0, dealerSeat, currentTurn,
                wallRemaining, Collections.emptyList(), scores,
                Arrays.asList(false, false, false, false), discards, melds,
                handCounts, closed, drawn, actions, lastDiscard, lastDiscardPlayer,
                Collections.emptyList(), furiten, history);
        }
    }
}
