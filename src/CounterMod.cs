using System;
using System.Collections.Generic;
using System.Globalization;
using Yx.ModSdk;
using Yx.ModSdk.Game;

namespace YxCounter
{
    /// <summary>
    /// 记牌器 mod 入口。每 ~0.5s 读手牌/牌桌/玉瓶的 owned 快照 + 每帧处理换牌事件，喂 <see cref="Counter"/>，
    /// 用 <see cref="CardUi.SetBadge"/> 在每张牌右上角画「剩X」（同 yxphud 的描边样式，复用不闪）。新的一局清零。
    /// 纯读 + 显示，不发包、不改状态。
    ///
    /// 抽牌 = owned 增量；换牌 = 钩 <c>ReadyLayer.OnReplaceSucceed(srcCard, dstCard)</c>（换牌成功时游戏自己 Invoke 的
    /// 空回调，带旧/新牌 id）→ <see cref="Counter.ApplyReroll"/>（弃牌 -3；先于当帧快照处理，避免把换进来的新牌重复计一次）。
    /// 早先用 <c>OnNetIn&lt;ReplaceCardResp&gt;</c> 收不稳（那条 decode 路不一定过），改钩游戏方法后只在换牌成功时触发、且拿到双方 id。
    /// 换局重置：读 <c>GameStatus.beginTs</c>（每局唯一、局中不变），变了就清零——不靠 StartGameResp（进程内那条被状态
    /// 轮询每几秒触发一次，会把局中的记录乱清掉），也不受「连续两局都停在 round 1」影响。
    /// Phase B 续：phase-5=6 / SPECIAL_COPIES / 副职牌池 / 免费给牌吸收 / 成对牌共池。
    /// </summary>
    public sealed class CounterMod : YxMod
    {
        GameCards _game;
        Counter _counter;
        ModLog _log;
        long _lastBeginTs;                                    // 上一局的 GameStatus.beginTs；变了 = 新局
        int _cd;
        int _lastSelected;                                   // 上次看到的「选牌获得」selected id（去重用）
        readonly System.Collections.Generic.HashSet<string> _seenFates = new System.Collections.Generic.HashSet<string>();   // 已打过日志的天命/副职（去重）
        readonly List<int[]> _rerolls = new List<int[]>();   // 待处理换牌：{旧牌原始id, 新牌原始id}
        readonly List<int> _grantQueue = new List<int>();    // 待处理白给（道韵选牌）：一级 base id

        public override void OnLoad(ModContext ctx)
        {
            _game = new GameCards();
            _counter = new Counter(_game);
            _log = ctx.Log;
            // 换牌成功时游戏在主线程 Invoke 的空回调（带旧/新牌 id）；比 net 消息可靠，只在真的换成时触发。
            // 挂不上（游戏改名）只少「换牌 -3」这一路，不让整个 mod 加载失败 → 用 TryPrefix。
            Yx.Shared.ISubscription sub = ctx.Hooks.TryPrefix("ReadyLayer", "OnReplaceSucceed", 2, OnReplaceHook);
            if (sub == null) ctx.Log.Warn("记牌器：换牌钩子 ReadyLayer.OnReplaceSucceed 没挂上（游戏可能更新了），换牌 -3 本局不生效。");
            // 道韵选牌确认：白给的牌不从牌库来，别算抽。玩家点「确认」时 panel.daoYun 就是选中的牌 id。
            Yx.Shared.ISubscription sub2 = ctx.Hooks.TryPrefix("BattleDaoYunSelectionPanel", "OnComfirmBtnClick", 0, OnDaoYunConfirmHook);
            if (sub2 == null) ctx.Log.Warn("记牌器：道韵钩子 BattleDaoYunSelectionPanel.OnComfirmBtnClick 没挂上（游戏可能更新了），道韵白给的牌会被误当抽牌。");
            // 变换获得（如天衍另辟蹊径把一张牌变成别的牌）：变出来的牌不是抽的。钩 CardItem.ToNewCard，变成不同 base 时记白给。
            // 合成/升级也走 ToNewCard 但 base 不变，GameCards 里会跳过。
            Yx.Shared.ISubscription sub3 = ctx.Hooks.TryPrefix("CardItem", "ToNewCard", 4, OnTransformHook);
            if (sub3 == null) ctx.Log.Warn("记牌器：变换钩子 CardItem.ToNewCard 没挂上（游戏可能更新了），变换获得的牌会被误当抽牌。");
            // 通用「获得牌」通道（BN_GotCards）：天衍仙命被选中时白给一张牌（如极·云剑柔心 → 云剑·柔心）等都走这里，
            // info.args 就是白给进手牌的牌 id。这条是同步加牌，额度必被 owned 消费、不会残留。
            Yx.Shared.ISubscription sub4 = ctx.Hooks.TryPrefix("YiXianPai.BattleManagerComponents.CommonActionNotifyHandler", "GotCards", 1, OnGotCardsHook);
            if (sub4 == null) ctx.Log.Warn("记牌器：获得牌钩子 CommonActionNotifyHandler.GotCards 没挂上（游戏可能更新了），仙命白给的牌会被误当抽牌。");
            ctx.Log.Info("记牌器已加载：每张牌角显示牌库剩余份数（每卡 8，抽 -1，换 -3；白给/选牌/变换/仙命给牌不减）。");
        }

        // 换牌成功钩子：Args[0]=旧牌 id、Args[1]=新牌 id（都是装箱 int）。入队，OnUpdate 每帧 drain（先于快照）。
        // 空方法返回 true 继续执行原方法（原方法本就是空的，无副作用）。
        bool OnReplaceHook(Yx.Shared.HookContext h)
        {
            try
            {
                if (h != null && h.Args != null && h.Args.Length >= 2 && h.Args[0] != null && h.Args[1] != null)
                {
                    int oldId = (int)h.Args[0];
                    int newId = (int)h.Args[1];
                    _rerolls.Add(new int[] { oldId, newId });
                    if (_log != null)
                        _log.Info("记牌器：换牌 " + oldId.ToString(CultureInfo.InvariantCulture) + "→" + newId.ToString(CultureInfo.InvariantCulture) + "（弃牌 -3）");
                }
            }
            catch (Exception) { }
            return true;
        }

        // 道韵选牌确认钩子：this = BattleDaoYunSelectionPanel，读它选中的牌 id（白给，不算抽）。入队，OnUpdate drain。
        bool OnDaoYunConfirmHook(Yx.Shared.HookContext h)
        {
            try
            {
                if (h != null)
                {
                    int baseId = _game.DaoYunPickedBaseId(h.Instance);
                    if (baseId > 0)
                    {
                        _grantQueue.Add(baseId);
                        if (_log != null) _log.Info("记牌器：道韵白给 " + baseId.ToString(CultureInfo.InvariantCulture) + "（不减牌库）");
                    }
                }
            }
            catch (Exception) { }
            return true;
        }

        // 变换获得钩子：this = 被变换的 CardItem，Args[0] = 变成的新牌 id。变成不同 base 的牌才记白给（合成/升级同 base 跳过）。
        bool OnTransformHook(Yx.Shared.HookContext h)
        {
            try
            {
                if (h != null && h.Args != null && h.Args.Length >= 1 && h.Args[0] != null)
                {
                    int newId = (int)h.Args[0];
                    int baseId = _game.TransformGrantBaseId(h.Instance, newId);
                    if (baseId > 0)
                    {
                        _grantQueue.Add(baseId);
                        if (_log != null) _log.Info("记牌器：变换获得 " + baseId.ToString(CultureInfo.InvariantCulture) + "（不减牌库）");
                    }
                }
            }
            catch (Exception) { }
            return true;
        }

        // 通用「获得牌」钩子：Args[0] = BattleNotifyInfo，info.args 是白给进手牌的牌 id。逐个记白给。
        bool OnGotCardsHook(Yx.Shared.HookContext h)
        {
            try
            {
                if (h != null && h.Args != null && h.Args.Length >= 1 && h.Args[0] != null)
                {
                    int[] ids = _game.GotCardsGrantBaseIds(h.Args[0]);
                    if (ids != null)
                        for (int i = 0; i < ids.Length; i++)
                        {
                            _grantQueue.Add(ids[i]);
                            if (_log != null) _log.Info("记牌器：获得牌 " + ids[i].ToString(CultureInfo.InvariantCulture) + "（不减牌库）");
                        }
                }
            }
            catch (Exception) { }
            return true;
        }

        // 整个每帧逻辑都兜底：任何一帧出错都不抛给宿主，避免触发错误预算把 mod 自动停用（否则新局切换时一次异常就再也不显示）。
        public override void OnUpdate()
        {
            try
            {
                DrainRerolls();                              // 每帧：先把换牌处理掉
                DrainGrants();                               // 每帧：把道韵白给记进「白给额度」
                PollCardSelection();                         // 每帧：通用「选牌获得」（三思抉择等）→ 白给额度
                if (_cd++ % 30 == 0)                         // ~0.5s：换局检测 + 更新记牌快照（抽牌 = owned 增量）
                {
                    long ts = _game.GameBeginTs();
                    if (ts != 0L && ts != _lastBeginTs)      // beginTs 变了 = 新的一局 → 清零
                    {
                        _counter.Reset(); _rerolls.Clear(); _grantQueue.Clear(); _lastSelected = 0; _seenFates.Clear();
                        _lastBeginTs = ts;
                        if (_log != null) _log.Info("记牌器：新局清零（beginTs=" + ts.ToString(CultureInfo.InvariantCulture) + "）");
                    }
                    _game.RebuildPool();                 // 先按当前激活仙命重算牌池份数（瞬影击/全能副职等）
                    Dictionary<int, int> owned = _game.OwnedSnapshot();
                    if (owned != null) _counter.ApplyOwnedSnapshot(owned);
                    // 发现器：把当前天命/副职里没打过的打一条日志，方便识别哪些仙命影响牌库、要不要进覆盖表。
                    if (_log != null)
                    {
                        List<string> fates = _game.DiscoverFates();
                        for (int i = 0; i < fates.Count; i++)
                            if (_seenFates.Add(fates[i])) _log.Info("记牌器：发现 " + fates[i]);
                    }
                }
                // 每帧刷角标（复用同一层、只改文字 → 不闪；跟随悬浮放大；同 HUD 每帧刷）。
                List<CardRef> cards = _game.HandBoardCards();
                for (int i = 0; i < cards.Count; i++)
                {
                    CardRef cr = cards[i];
                    if (cr == null || cr.Rt == null) continue;
                    if (_game.IsDeckCard(cr.BaseId))
                        CardUi.SetBadge(cr.Rt, "cnt", "剩" + _counter.RemainingOf(cr.BaseId).ToString(CultureInfo.InvariantCulture));
                    else
                        CardUi.ClearBadge(cr.Rt, "cnt");
                }
            }
            catch (Exception) { }
        }

        void DrainRerolls()
        {
            if (_rerolls.Count == 0) return;
            for (int i = 0; i < _rerolls.Count; i++)
            {
                int[] ev = _rerolls[i];
                _counter.ApplyReroll(GameCards.BaseId(ev[0]), GameCards.BaseId(ev[1]));
            }
            _rerolls.Clear();
        }

        void DrainGrants()
        {
            if (_grantQueue.Count == 0) return;
            for (int i = 0; i < _grantQueue.Count; i++)
            {
                int b = _grantQueue[i];
                if (_game.IsFatePoolCard(b)) continue;   // 仙命牌池牌不吸收：按抽计数（获得1张抽掉=剩0）
                _counter.ApplyGrant(b);
            }
            _grantQueue.Clear();
        }

        // 通用「选牌获得」：轮询 cardSelectionData.selected，变成新的有效牌库卡就记一次白给额度（三思抉择等天衍仙命 / 各种选牌获得）。
        // 只在变化时处理（去重）；selected 归 0 时同步 _lastSelected，好识别下一次（哪怕又选到同一张）。
        void PollCardSelection()
        {
            int sel = _game.CardSelectionSelectedId();
            if (sel == _lastSelected) return;
            _lastSelected = sel;
            if (sel == 0) return;
            int baseId = GameCards.BaseId(sel);
            if (_game.IsDeckCard(baseId) && !_game.IsFatePoolCard(baseId))
            {
                _counter.ApplyGrant(baseId);
                if (_log != null) _log.Info("记牌器：选牌获得 " + baseId.ToString(CultureInfo.InvariantCulture) + "（不减牌库）");
            }
        }

        public override void OnDisable()
        {
            // 收起当前可见牌上的角标。
            if (_game == null) return;
            List<CardRef> cards = _game.HandBoardCards();
            for (int i = 0; i < cards.Count; i++)
                if (cards[i] != null) CardUi.ClearBadge(cards[i].Rt, "cnt");
        }
    }
}
