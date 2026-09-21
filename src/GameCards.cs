using System;
using System.Collections.Generic;
using UnityEngine;
using Proto;   // CardConfig

namespace YxCounter
{
    /// <summary>手牌/牌桌上一张牌：可见 RectTransform（画剩X用）+ 一级 base id。</summary>
    public sealed class CardRef
    {
        public RectTransform Rt;
        public int BaseId;
    }

    /// <summary>
    /// 游戏访问层：读手牌/牌桌/玉瓶的牌、归一到一级 base id、供元数据（<see cref="ICardMeta"/>）。
    /// 只有这里碰游戏类型（CardPanel/CardItem/CardFactory/CardConfig）。Phase A：MaxCopies 一律 8
    /// （phase-5=6、SPECIAL_COPIES、副职牌池在 Phase B 补）。
    /// </summary>
    public sealed class GameCards : ICardMeta
    {
        // 换牌无惩罚的牌（换掉不额外扣 3）——移植自 proxy_view.py NO_REROLL_PENALTY。
        static readonly string[] NO_REROLL_PENALTY = { "练笔", "以画入道", "妙笔生花", "触类旁通" };

        // 牌库份数「不是 8」的牌：按牌名覆盖（记牌器只给抽到的牌标数，所以真正会显示错的只有「总份数≠8」这种）。
        // 两组并行数组一一对应。移植自参考 proxy_view.py 的 SPECIAL_COPIES / 炼丹师三丹（=3，2026-06 用户确认）。
        // 新的「加牌但非 8 张」的仙命（如天衍瞬影杀加进来的牌）：把那张牌的【牌名】和份数加到这两行即可。
        static readonly string[] SPECIAL_COPY_NAMES  = { "洗髓丹", "悟道丹", "锻体玄丹" };
        static readonly int[]    SPECIAL_COPY_COUNTS = {   3,       3,        3      };

        // 当前激活仙命往牌池加的牌：base id → 份数（每 ~0.5s 由 RebuildPool 重算）。
        readonly Dictionary<int, int> _pool = new Dictionary<int, int>();
        bool _omni;                                        // 全能副职(FateStrategy 31)激活：其他副职元婴/化神各 4
        readonly List<int> _pickedCareers = new List<int>();   // 玩家已选副职(FZJXCareers 的值)

        static CardPanel CP()
        {
            var bp = ILRPanelBase.FindILRPanel<BattlePanel>();
            return bp == null ? null : bp.FindILRSubPanel<CardPanel>();
        }

        static Talent199Panel YuPing()
        {
            try
            {
                var bp = ILRPanelBase.FindILRPanel<BattlePanel>(); if (bp == null) return null;
                var p = bp.FindILRSubPanel<Talent199Panel>(); if (p == null) return null;
                var go = p.gameObject;
                return (go != null && go.activeInHierarchy) ? p : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>把游戏里的卡牌 id 归一到一级 base id。</summary>
        public static int BaseId(int cardId)
        {
            try { return CardFactory.GetBaseCardId(cardId); } catch (Exception) { return cardId; }
        }

        /// <summary>
        /// 当前对局的开始时间戳（<c>GameStatus.beginTs</c>）：每局唯一、局中不变；不在对局返回 0。
        /// 用来判「换了一局」——不靠 StartGameResp（进程内那条每几秒被状态轮询触发一次，会乱清零），
        /// 也不受「连续两局都停在 round 1」影响。
        /// </summary>
        public long GameBeginTs()
        {
            try
            {
                var bm = BattleManager.Instance;
                var st = bm != null ? bm.currentGameStatus : null;
                return st != null ? st.beginTs : 0L;
            }
            catch (Exception) { return 0L; }
        }

        /// <summary>
        /// 通用「选牌获得」系统当前选中的牌 id（原始 id，未归一）：<c>GameStatus.playerPrivateData.cardSelectionData.selected</c>。
        /// 三思抉择等天衍仙命 / 各种「发你几张选一张获得」的效果都走这套（<c>CardSelectionPanel</c> + <c>CardSelectedReq</c>）。
        /// 没有选中 / 不在对局返回 0。轮询它的变化即可识别「白给（选来的）」的牌。
        /// </summary>
        public int CardSelectionSelectedId()
        {
            try
            {
                var bm = BattleManager.Instance;
                var st = bm != null ? bm.currentGameStatus : null;
                var pd = st != null ? st.playerPrivateData : null;
                var cs = pd != null ? pd.cardSelectionData : null;
                return cs != null ? cs.selected : 0;
            }
            catch (Exception) { return 0; }
        }

        /// <summary>
        /// 从 <c>BattleDaoYunSelectionPanel.OnComfirmBtnClick</c> 钩子的 this 取玩家选中的道韵牌的一级 base id。
        /// 不是牌库卡（如自在随心 id=27 这种短 id，按用户规则算真抽）或取不到返回 -1。
        /// </summary>
        public int DaoYunPickedBaseId(object panelInstance)
        {
            try
            {
                BattleDaoYunSelectionPanel p = panelInstance as BattleDaoYunSelectionPanel;
                if (p == null) return -1;
                int id = BaseId(p.daoYun);
                return IsDeckCard(id) ? id : -1;
            }
            catch (Exception) { return -1; }
        }

        /// <summary>
        /// 从 <c>CardItem.ToNewCard(newCardId,…)</c> 钩子判断这次「变换」是不是「变换获得一张别的牌」（如天衍另辟蹊径）。
        /// 变成不同 base 的牌 → 返回新牌一级 base id（该记白给、不算抽）；同 base（合成/升级）或非牌库卡 → 返回 -1。
        /// 靠 prefix 时机：ToNewCard 第一行才改 id，所以这里读到的 <c>cardInfo.id</c> 还是变换【前】的旧牌。
        /// </summary>
        public int TransformGrantBaseId(object cardItemInstance, int newCardId)
        {
            try
            {
                CardItem c = cardItemInstance as CardItem;
                if (c == null) return -1;
                int oldBase = BaseId(c.cardInfo.id);
                int newBase = BaseId(newCardId);
                if (newBase == oldBase) return -1;               // 合成/同牌升级 → 不是「变换成别的牌」
                return IsDeckCard(newBase) ? newBase : -1;
            }
            catch (Exception) { return -1; }
        }

        /// <summary>
        /// 从 <c>CommonActionNotifyHandler.GotCards(BattleNotifyInfo info)</c> 钩子取这次「获得牌」白给的牌（一级 base id 列表）。
        /// <c>info.args</c> 就是要 AddRange 进手牌的牌 id（天衍仙命白给、各种"获得一张牌"效果都走 BN_GotCards）。
        /// 只在 <c>clientApply</c>（真加进手牌）时算；只留牌库卡；没有返回 null。这条通道是同步加牌，额度必被 owned 消费、不残留。
        /// </summary>
        public int[] GotCardsGrantBaseIds(object infoInstance)
        {
            try
            {
                BattleNotifyInfo info = infoInstance as BattleNotifyInfo;
                if (info == null || !info.clientApply) return null;
                List<int> args = info.args;
                if (args == null || args.Count == 0) return null;
                int[] tmp = new int[args.Count];
                int n = 0;
                for (int i = 0; i < args.Count; i++)
                {
                    int b = BaseId(args[i]);
                    if (IsDeckCard(b)) tmp[n++] = b;
                }
                if (n == 0) return null;
                int[] r = new int[n];
                for (int i = 0; i < n; i++) r[i] = tmp[i];
                return r;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// 发现器：读当前对局玩家的天命/天衍（<c>talentDatas</c> 的 id + <c>TalentConfig.name</c>）与副职
        /// （<c>FZJXCareers</c>）。用来在日志里看清有哪些仙命在起作用——牌库池 / 仙命效果游戏不结构化地暴露，
        /// 只能靠这个把 id+名字捞出来，再决定哪些要进「牌名→份数」覆盖表。每条形如「仙命 205 斩断俗尘」。
        /// </summary>
        public List<string> DiscoverFates()
        {
            var outp = new List<string>();
            try
            {
                var bm = BattleManager.Instance;
                var st = bm != null ? bm.currentGameStatus : null;
                var pd = st != null ? st.playerPrivateData : null;
                if (pd == null) return outp;
                if (pd.talentDatas != null)
                {
                    foreach (int id in pd.talentDatas.Keys)
                    {
                        string nm = null; string desc = null;
                        try
                        {
                            TalentConfig tc = ConfigManager.GetTalentConfig(id);
                            if (tc != null)
                            {
                                nm = tc.name;
                                // 效果就写在描述里（如"牌库中加入N张X"）→ 读出来打日志，据此决定进覆盖表。
                                try { desc = ConfigExtension.ParseDescription(tc); } catch (Exception) { }
                            }
                        }
                        catch (Exception) { }
                        if (desc != null) desc = desc.Replace((char)10, ' ').Replace((char)13, ' ');
                        outp.Add("仙命 " + id.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + (nm != null ? nm : "?") + " | " + (desc != null ? desc : ""));
                    }
                }
                if (pd.FZJXCareers != null)
                {
                    foreach (int k in pd.FZJXCareers.Keys)
                    {
                        int v; pd.FZJXCareers.TryGetValue(k, out v);
                        outp.Add("副职 slot=" + k.ToString(System.Globalization.CultureInfo.InvariantCulture) + " career=" + v.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                }
            }
            catch (Exception) { }
            return outp;
        }

        /// <summary>
        /// 每局/每 ~0.5s 重算「激活仙命往牌池加了哪些牌、各几张」：读当前激活的天命(GetMainPlayerData().talents)、
        /// 天命策略(fateStrategyData.strategies)、共鸣(talentResonanceData)，对照 <see cref="FateEffects"/> 表填 _pool；
        /// 全能副职(strategy 31)单独置 _omni；已选副职存 _pickedCareers 供「其他副职」判定。
        /// </summary>
        public void RebuildPool()
        {
            try
            {
                _pool.Clear(); _omni = false; _pickedCareers.Clear();
                var bm = BattleManager.Instance;
                var st = bm != null ? bm.currentGameStatus : null;
                if (st == null) return;
                var pd = st.playerPrivateData;
                if (pd != null && pd.FZJXCareers != null)
                    foreach (int k in pd.FZJXCareers.Keys) { int cv; pd.FZJXCareers.TryGetValue(k, out cv); if (cv > 0) _pickedCareers.Add(cv); }

                var mp = st.GetMainPlayerData();
                List<int> talents = mp != null ? mp.talents : null;
                if (talents != null)
                    for (int i = 0; i < FateEffects.TalentId.Length; i++)
                        if (talents.Contains(FateEffects.TalentId[i])) AddPool(FateEffects.TalentCards[i], FateEffects.TalentCount[i]);

                if (pd != null && pd.fateStrategyData != null && pd.fateStrategyData.strategies != null)
                {
                    List<SelectionData> strat = pd.fateStrategyData.strategies;
                    for (int i = 0; i < FateEffects.StrategyId.Length; i++)
                        if (StrategyActive(strat, FateEffects.StrategyId[i])) AddPool(FateEffects.StrategyCards[i], FateEffects.StrategyCount[i]);
                    if (StrategyActive(strat, 31)) _omni = true;   // 全能副职
                }

                if (pd != null && pd.talentResonanceData != null && pd.talentResonanceData.selectionData != null)
                {
                    int rsel = pd.talentResonanceData.selectionData.selected;
                    for (int i = 0; i < FateEffects.ResonanceId.Length; i++)
                        if (FateEffects.ResonanceId[i] == rsel) AddPool(FateEffects.ResonanceCards[i], FateEffects.ResonanceCount[i]);
                }
            }
            catch (Exception) { }
        }

        /// <summary>这张牌是不是「激活仙命往牌池加的牌」（在 _pool 里）。这类牌不做白给吸收——它们本就是那个小牌池，
        /// 按抽计数才对（如瞬影击「获得1张」抽掉后应显示剩0，而不是被吸收后显示剩1）。</summary>
        public bool IsFatePoolCard(int baseId) { return _pool.ContainsKey(baseId); }

        static bool StrategyActive(List<SelectionData> strat, int id)
        {
            for (int i = 0; i < strat.Count; i++) { SelectionData s = strat[i]; if (s != null && s.selected == id) return true; }
            return false;
        }

        void AddPool(int[] cards, int count)
        {
            if (cards == null) return;
            for (int i = 0; i < cards.Length; i++)
            {
                int b = BaseId(cards[i]);
                int v; _pool.TryGetValue(b, out v); _pool[b] = v + count;
            }
        }

        // 全能副职判定：这张牌是不是「其他副职（玩家没选的副职）的元婴/化神牌」。
        bool IsOtherSidejobHigh(int baseId)
        {
            try
            {
                CardConfig cc = CardFactory.FindCardConfig(baseId);
                if (cc == null) return false;
                int career = (int)cc.career;
                if (career < 1 || career > 7) return false;          // 只算副职 1..7
                if (_pickedCareers.Contains(career)) return false;    // 自己选的副职不算「其他」
                int lvl = (int)cc.level;
                return lvl == 4 || lvl == 5;                           // 元婴(4)/化神(5)
            }
            catch (Exception) { return false; }
        }

        // ── ICardMeta ────────────────────────────────────────────
        public bool IsDeckCard(int baseId)
        {
            if (_pool.ContainsKey(baseId)) return true;               // 仙命加进牌池的牌（可能 id < 100万）
            if (_omni && IsOtherSidejobHigh(baseId)) return true;
            // personal / 命运给的牌 id < 1,000,000；梦牌按名字排除。
            if (baseId < 1000000) return false;
            try
            {
                CardConfig cc = CardFactory.FindCardConfig(baseId);
                string n = cc != null ? cc.name : null;
                if (n != null && n.Length > 0 && n[0] == '梦') return false;
            }
            catch (Exception) { }
            return true;
        }

        public int MaxCopies(int baseId)
        {
            // 优先级：仙命加牌份数 > 全能副职(其他副职元婴化神 ×4) > 牌名特殊表(丹药=3) > 默认 8。
            // phase-5=6 暂不做：CardConfig 无 phase 字段、也没有客户端牌库池可读。
            int pv;
            if (_pool.TryGetValue(baseId, out pv) && pv > 0) return pv;
            if (_omni && IsOtherSidejobHigh(baseId)) return 4;
            try
            {
                CardConfig cc = CardFactory.FindCardConfig(baseId);
                string n = cc != null ? cc.name : null;
                if (n != null)
                    for (int i = 0; i < SPECIAL_COPY_NAMES.Length; i++)
                        if (n == SPECIAL_COPY_NAMES[i]) return SPECIAL_COPY_COUNTS[i];
            }
            catch (Exception) { }
            return 8;
        }

        public int RerollMult(int baseId)
        {
            try
            {
                CardConfig cc = CardFactory.FindCardConfig(baseId);
                string n = cc != null ? cc.name : null;
                if (n != null)
                    for (int i = 0; i < NO_REROLL_PENALTY.Length; i++)
                        if (n == NO_REROLL_PENALTY[i]) return 0;
            }
            catch (Exception) { }
            return 3;
        }

        // ── 快照 / 可见牌 ─────────────────────────────────────────
        /// <summary>当前 owned（手牌+牌桌+玉瓶）的多重集：base id → 张数；不在对局返回 null。</summary>
        public Dictionary<int, int> OwnedSnapshot()
        {
            var cp = CP();
            if (cp == null) return null;
            var d = new Dictionary<int, int>();
            try
            {
                var h = cp.GetHandCards();
                if (h != null) for (int i = 0; i < h.Count; i++) AddCard(d, h[i]);
                var g = cp.GetCardGrids();
                if (g != null) for (int i = 0; i < g.Count; i++) { var gr = g[i]; if (gr != null) AddCard(d, gr.GetCard()); }
                var yp = YuPing();
                if (yp != null)
                {
                    var c = yp.cardGridContainer;
                    var yg = c != null ? c.cardGrids : null;
                    if (yg != null) for (int i = 0; i < yg.Count; i++) { var gr = yg[i]; if (gr != null) AddCard(d, gr.GetCard()); }
                }
            }
            catch (Exception) { }
            return d;
        }

        /// <summary>当前手牌+牌桌上可见的牌（供画剩X）：RectTransform + base id。</summary>
        public List<CardRef> HandBoardCards()
        {
            var list = new List<CardRef>();
            var cp = CP();
            if (cp == null) return list;
            try
            {
                var h = cp.GetHandCards();
                if (h != null) for (int i = 0; i < h.Count; i++) AddRef(list, h[i]);
                var g = cp.GetCardGrids();
                if (g != null) for (int i = 0; i < g.Count; i++) { var gr = g[i]; if (gr != null) AddRef(list, gr.GetCard()); }
            }
            catch (Exception) { }
            return list;
        }

        static void AddCard(Dictionary<int, int> d, CardItem c)
        {
            if (c == null) return;
            try
            {
                int id = BaseId(c.cardInfo.id);
                int v; d.TryGetValue(id, out v); d[id] = v + 1;
            }
            catch (Exception) { }
        }

        static void AddRef(List<CardRef> list, CardItem c)
        {
            if (c == null) return;
            try
            {
                var rt = c.movableRT != null ? c.movableRT : c.transform as RectTransform;
                if (rt == null) return;
                list.Add(new CardRef { Rt = rt, BaseId = BaseId(c.cardInfo.id) });
            }
            catch (Exception) { }
        }
    }
}
