# 记牌器

弈仙牌记牌器：在每张牌上显示牌库里还剩几份（每卡 8 份、**化神期 6 份**，抽 -1，换牌 -3）。纯读游戏协议 + 状态，不发包、不改任何东西。搬运自 yxphud 的纯记牌版（去掉伤害计算器与跳过战斗）。

## 构建

本 mod 需在「弈仙牌 MOD SDK / 加载器」工作区内构建（`.csproj` 用相对路径引用 SDK 项目和本机游戏 DLL，单独 clone 无法直接编译）：

1. 取得 SDK / 加载器工作区（含 `sdk/`、`tools/yx-patch`），把本仓库放到工作区的 `mods/com.yx.cardcounter/`。
2. 准备本机 `refs/`：`yx-patch refs --game <游戏目录> --out refs` 从游戏取热更 DLL（厂商版权材料，**不随仓库分发**）。
3. `dotnet build Cardcounter.csproj -c Release` → 产物在 `plugins/*.dll`。
4. `yx-patch check plugins/*.dll` 应为 0 error。

产物 DLL 与 `bin/ obj/ plugins/` 不入库（见 `.gitignore`）。
