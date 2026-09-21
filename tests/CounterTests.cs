using System.Collections.Generic;
using Xunit;
using YxCounter;

namespace YxCounter.Tests
{
    /// <summary>假的卡牌元数据（脱离游戏测 Counter）。默认每卡 8 份、mult 3、都是牌库卡；可按需覆盖。</summary>
    sealed class FakeMeta : ICardMeta
    {
        public readonly HashSet<int> NonDeck = new HashSet<int>();
        public readonly Dictionary<int, int> Copies = new Dictionary<int, int>();
        public readonly HashSet<int> NoRerollPenalty = new HashSet<int>();

        public bool IsDeckCard(int baseId) { return !NonDeck.Contains(baseId); }
        public int MaxCopies(int baseId) { int v; return Copies.TryGetValue(baseId, out v) ? v : 8; }
        public int RerollMult(int baseId) { return NoRerollPenalty.Contains(baseId) ? 0 : 3; }
    }

    static class D
    {
        public static Dictionary<int, int> Owned(params int[] pairs)
        {
            var d = new Dictionary<int, int>();
            for (int i = 0; i + 1 < pairs.Length; i += 2) d[pairs[i]] = pairs[i + 1];
            return d;
        }
    }

    public class CounterTests
    {
        [Fact]
        public void Draw_decrements_from_owned_increase()
        {
            var m = new FakeMeta();
            var c = new Counter(m);
            c.ApplyOwnedSnapshot(D.Owned(100, 1));   // 抽到 1 张
            Assert.Equal(7, c.RemainingOf(100));
            c.ApplyOwnedSnapshot(D.Owned(100, 2));   // 又抽到 1 张
            Assert.Equal(6, c.RemainingOf(100));
        }

        [Fact]
        public void Playing_a_card_owned_decrease_is_not_a_draw()
        {
            var m = new FakeMeta();
            var c = new Counter(m);
            c.ApplyOwnedSnapshot(D.Owned(100, 2));   // 抽了 2 张 → 剩 6
            Assert.Equal(6, c.RemainingOf(100));
            c.ApplyOwnedSnapshot(D.Owned(100, 1));   // 打出 1 张（owned 减少）→ 不是抽
            Assert.Equal(6, c.RemainingOf(100));     // 还是 6
        }

        [Fact]
        public void Reroll_burns_extra_three_on_discard_and_one_on_new()
        {
            var m = new FakeMeta();
            var c = new Counter(m);
            c.ApplyReroll(200, 300);                 // 把 200 换成 300
            Assert.Equal(5, c.RemainingOf(200));     // 8 - 3*1 = 5
            Assert.Equal(7, c.RemainingOf(300));     // 8 - 1 = 7
        }

        [Fact]
        public void Reroll_in_is_not_double_counted_by_next_snapshot()
        {
            var m = new FakeMeta();
            var c = new Counter(m);
            c.ApplyReroll(200, 300);                 // 新牌 300 预记进 owned
            c.ApplyOwnedSnapshot(D.Owned(300, 1));   // 快照看到 300，不该再算一次抽
            Assert.Equal(7, c.RemainingOf(300));     // 仍是 8 - 1 = 7
        }

        [Fact]
        public void No_reroll_penalty_card_only_counts_the_draw()
        {
            var m = new FakeMeta();
            m.NoRerollPenalty.Add(400);
            var c = new Counter(m);
            c.ApplyReroll(400, 401);                 // 换掉 400（无惩罚）
            Assert.Equal(8, c.RemainingOf(400));     // 8 - 0*1 = 8
            Assert.Equal(7, c.RemainingOf(401));     // 新牌照常 -1
        }

        [Fact]
        public void Phase5_card_uses_six_copies()
        {
            var m = new FakeMeta();
            m.Copies[500] = 6;
            var c = new Counter(m);
            c.ApplyOwnedSnapshot(D.Owned(500, 1));
            Assert.Equal(5, c.RemainingOf(500));     // 6 - 1
        }

        [Fact]
        public void Non_deck_card_is_ignored()
        {
            var m = new FakeMeta();
            m.NonDeck.Add(999);
            var c = new Counter(m);
            c.ApplyOwnedSnapshot(D.Owned(999, 1));
            Assert.Equal(0, c.RemainingOf(999));
            Assert.False(c.Remaining().ContainsKey(999));
        }

        [Fact]
        public void Seasonal_roundtrip_does_not_look_like_redraw()
        {
            // owned 已含玉瓶（seasonal）持有，所以手牌↔玉瓶往返时 owned 计数不变 → 不算重抽。
            var m = new FakeMeta();
            var c = new Counter(m);
            c.ApplyOwnedSnapshot(D.Owned(100, 1));   // 抽 1 → 7
            c.ApplyOwnedSnapshot(D.Owned(100, 1));   // 移进玉瓶又拿回，owned 不变
            Assert.Equal(7, c.RemainingOf(100));
        }

        [Fact]
        public void Remaining_lists_drawn_then_played_cards_and_drops_exhausted()
        {
            var m = new FakeMeta();
            m.Copies[100] = 1;                       // 只有 1 份
            var c = new Counter(m);
            c.ApplyOwnedSnapshot(D.Owned(100, 1));   // 抽走唯一 1 份 → 剩 0
            c.ApplyOwnedSnapshot(D.Owned());         // 打出去，owned 空
            Assert.Equal(0, c.RemainingOf(100));
            Assert.False(c.Remaining().ContainsKey(100));   // 耗尽的不显示
        }

        [Fact]
        public void Granted_card_is_not_counted_as_a_draw()
        {
            var m = new FakeMeta();
            var c = new Counter(m);
            c.ApplyGrant(600);                       // 道韵/仙命白给一张 600
            c.ApplyOwnedSnapshot(D.Owned(600, 1));   // 它进了 owned，但不是抽
            Assert.Equal(8, c.RemainingOf(600));     // 牌库仍是满的 8
        }

        [Fact]
        public void Grant_absorbs_only_one_even_if_also_drawn_same_tick()
        {
            var m = new FakeMeta();
            var c = new Counter(m);
            c.ApplyGrant(700);                       // 白给 1 张
            c.ApplyOwnedSnapshot(D.Owned(700, 2));   // 同一拍 owned +2：1 张白给 + 1 张真抽
            Assert.Equal(7, c.RemainingOf(700));     // 只扣真抽那 1 张 → 8-1
        }

        [Fact]
        public void Grant_survives_a_snapshot_before_the_card_lands()
        {
            var m = new FakeMeta();
            var c = new Counter(m);
            c.ApplyGrant(800);                       // 记下白给额度
            c.ApplyOwnedSnapshot(D.Owned());         // 牌还没落地（异步延迟），空快照
            c.ApplyOwnedSnapshot(D.Owned(800, 1));   // 稍后才落地 → 仍应被额度抵掉
            Assert.Equal(8, c.RemainingOf(800));
        }

        [Fact]
        public void Non_deck_grant_is_ignored_so_its_random_card_still_counts()
        {
            // 自在随心(短 id)按用户规则算真抽：ApplyGrant 对非牌库 id 不记额度。
            var m = new FakeMeta();
            m.NonDeck.Add(27);
            var c = new Counter(m);
            c.ApplyGrant(27);                        // 非牌库 → 不记额度
            c.ApplyOwnedSnapshot(D.Owned(100, 1));   // 它给的随机牌 100 照常算抽
            Assert.Equal(7, c.RemainingOf(100));
        }

        [Fact]
        public void Reset_clears_all_state()
        {
            var m = new FakeMeta();
            var c = new Counter(m);
            c.ApplyOwnedSnapshot(D.Owned(100, 3));
            c.ApplyGrant(200);                       // 也清白给额度
            c.Reset();
            Assert.Equal(8, c.RemainingOf(100));
            Assert.Empty(c.Remaining());
            c.ApplyOwnedSnapshot(D.Owned(200, 1));   // reset 后额度没了 → 这张算抽
            Assert.Equal(7, c.RemainingOf(200));
        }
    }
}
