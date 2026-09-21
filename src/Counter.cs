using System.Collections.Generic;

namespace YxCounter
{
    /// <summary>
    /// 卡牌元数据来源（把游戏相关的判断抽象出去，好让 <see cref="Counter"/> 脱离游戏单测）。
    /// 键是「一级 base id」（游戏里同名牌 1/2/3 级共一个 base，见 CardFactory.GetBaseCardId）。
    /// </summary>
    public interface ICardMeta
    {
        /// <summary>是不是牌库里能抽到的牌（排除梦牌、personal / 命运给的牌）。</summary>
        bool IsDeckCard(int baseId);
        /// <summary>牌库里这张牌一共几份（默认 8；phase-5 = 6；SPECIAL_COPIES 覆盖；副职牌另算）。</summary>
        int MaxCopies(int baseId);
        /// <summary>换牌弃掉这张牌时，额外扣几份（一般 3；NO_REROLL_PENALTY 里的牌 0）。</summary>
        int RerollMult(int baseId);
    }

    /// <summary>
    /// 记牌器核心（纯逻辑，不碰游戏类型）。移植自参考实现 <c>proxy_view.py</c> 的 <c>Counter</c>。
    /// 模型：每卡 <see cref="ICardMeta.MaxCopies"/> 份；抽走 −1；换牌弃掉 −(RerollMult) 额外；
    /// <c>剩 = max(0, max_copies − draws − mult*reroll_discards)</c>。
    /// 抽牌两路检测：换牌事件显式给出 old/new；开局发牌 = owned（手牌+牌桌+玉瓶）多重集的增量。
    /// 用法：每帧（或每 0.5s）先 <see cref="ApplyReroll"/>（把本帧的换牌事件喂进来），再 <see cref="ApplyOwnedSnapshot"/>；
    /// 然后 <see cref="Remaining"/> 取每张 base id 的剩余份数。
    ///
    /// 本期（Phase A）不含：副职牌池、免费给牌吸收、成对变身牌共池——那些在 Phase B 补（见计划）。
    /// </summary>
    public sealed class Counter
    {
        readonly ICardMeta _meta;
        readonly Dictionary<int, int> _draws = new Dictionary<int, int>();
        readonly Dictionary<int, int> _rerollDiscards = new Dictionary<int, int>();
        readonly Dictionary<int, int> _prevOwned = new Dictionary<int, int>();
        // 「白给」额度：道韵 / 天衍仙命等直接给的牌不从牌库来，不该算抽。记在这里，等这张牌真的进 owned
        // 时（下一次快照看到增量）用额度把增量抵掉，抵掉的不计 draw。用额度账本而不是预记 prevOwned，
        // 是因为给牌到落地有异步延迟，中途的快照会把预记的 prevOwned 冲掉（快照末尾按 owned 重建）。
        readonly Dictionary<int, int> _grants = new Dictionary<int, int>();

        public Counter(ICardMeta meta) { _meta = meta; }

        /// <summary>换局重置。</summary>
        public void Reset()
        {
            _draws.Clear();
            _rerollDiscards.Clear();
            _prevOwned.Clear();
            _grants.Clear();
        }

        /// <summary>
        /// 记一张「白给」的牌（道韵选牌 / 天衍仙命给牌）：不算抽、不减牌库。等它进 owned 时把那一次增量抵掉。
        /// 非牌库卡（personal / 短 id 如自在随心 27）忽略——那类要么本就不显示，要么按真抽处理。
        /// </summary>
        public void ApplyGrant(int grantedBaseId)
        {
            if (_meta.IsDeckCard(grantedBaseId)) Inc(_grants, grantedBaseId, 1);
        }

        /// <summary>
        /// 换牌事件：把 <paramref name="oldBaseId"/> 换成 <paramref name="newBaseId"/>。
        /// 新牌算一次抽取（+1 draw），并预记进 owned（免得随后的快照增量把它再当一次抽）；
        /// 旧牌算一次换牌弃掉（reroll_discards +1），并从 owned 预扣。
        /// </summary>
        public void ApplyReroll(int oldBaseId, int newBaseId)
        {
            if (_meta.IsDeckCard(newBaseId))
            {
                Inc(_draws, newBaseId, 1);
                Inc(_prevOwned, newBaseId, 1);
            }
            if (_meta.IsDeckCard(oldBaseId))
            {
                Inc(_rerollDiscards, oldBaseId, 1);
                int prev; _prevOwned.TryGetValue(oldBaseId, out prev);
                _prevOwned[oldBaseId] = prev > 0 ? prev - 1 : 0;
            }
        }

        /// <summary>
        /// 当前 owned（手牌+牌桌+玉瓶）的多重集：base id → 张数。相对上次快照的**增量**记为抽牌
        /// （合并/摆放/吸收只会让 owned 减少，不会误判为抽）；减少不处理。之后把它记为新的上次快照。
        /// </summary>
        public void ApplyOwnedSnapshot(IDictionary<int, int> owned)
        {
            if (owned == null) return;
            foreach (KeyValuePair<int, int> kv in owned)
            {
                int id = kv.Key;
                if (!_meta.IsDeckCard(id)) continue;
                int prev; _prevOwned.TryGetValue(id, out prev);
                int delta = kv.Value - prev;
                if (delta > 0)
                {
                    // 先用「白给」额度抵掉增量：白给的牌不从牌库来，不算抽；剩下的才是真抽。
                    int grant; _grants.TryGetValue(id, out grant);
                    int absorb = grant < delta ? grant : delta;
                    if (absorb > 0) _grants[id] = grant - absorb;
                    int drawn = delta - absorb;
                    if (drawn > 0) Inc(_draws, id, drawn);
                }
            }
            // 新快照：只留牌库卡；没出现的 base id 归 0（下次再出现才是新抽）。
            _prevOwned.Clear();
            foreach (KeyValuePair<int, int> kv in owned)
                if (_meta.IsDeckCard(kv.Key)) _prevOwned[kv.Key] = kv.Value;
        }

        /// <summary>每张见过的牌当前剩余份数（base id → 剩数），只含剩 &gt; 0 的。</summary>
        public Dictionary<int, int> Remaining()
        {
            var seen = new Dictionary<int, int>();   // 当集合用
            foreach (int id in _draws.Keys) seen[id] = 1;
            foreach (int id in _prevOwned.Keys) seen[id] = 1;

            var outMap = new Dictionary<int, int>();
            foreach (int id in seen.Keys)
            {
                if (!_meta.IsDeckCard(id)) continue;
                int max = _meta.MaxCopies(id);
                int mult = _meta.RerollMult(id);
                int draws; _draws.TryGetValue(id, out draws);
                int disc; _rerollDiscards.TryGetValue(id, out disc);
                int removed = draws + mult * disc;
                int left = max - removed;
                if (left < 0) left = 0;
                if (left > 0) outMap[id] = left;
            }
            return outMap;
        }

        /// <summary>某张牌当前剩余份数（不在牌库/已耗尽返回 0）。</summary>
        public int RemainingOf(int baseId)
        {
            if (!_meta.IsDeckCard(baseId)) return 0;
            int draws; _draws.TryGetValue(baseId, out draws);
            int disc; _rerollDiscards.TryGetValue(baseId, out disc);
            int removed = draws + _meta.RerollMult(baseId) * disc;
            int left = _meta.MaxCopies(baseId) - removed;
            return left > 0 ? left : 0;
        }

        static void Inc(Dictionary<int, int> d, int key, int by)
        {
            int v; d.TryGetValue(key, out v);
            d[key] = v + by;
        }
    }
}
