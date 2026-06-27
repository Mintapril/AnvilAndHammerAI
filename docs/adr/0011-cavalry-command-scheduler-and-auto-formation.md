# ADR 0011：中央指挥调度器(自写 Tactic)+ 自动编队 + 空间理解

- 状态：已接受(蓝图，待分步在游验证后逐项定稿)
- 日期：2026-06-25
- 关系：
  - **推翻** [ADR-0007](../../_archive/docs/adr/0007-formation-ownership-and-dual-mode.md) 的"轻/重区别只走行为层、不做空间分离"决策。
  - **超越** [ADR-0008](../../_archive/docs/adr/0008-cavalry-formation-split-by-borrowed-slot.md) 的"借一个空闲槽 + postfix 在 RBM 之后抢权"方案，改为"**废掉 RBM 的每 tick 重分 + 完全自管编队分配**"。
  - **保留** 0007/0008 的字面硬约束：**不建第二个 `FormationClass.Cavalry` 槽**（本方案用 `Cavalry`+`LightCavalry`+`HeavyCavalry` 三个**不同**的 class 槽，不违反此约束）。
  - **吸收/扩展** [ADR-0010](../../_archive/docs/adr/0010-light-cav-flank-rear-screen.md)（轻骑护侧后）为调度器的一类动作。
  - **建立在** [ADR-0006](../../_archive/docs/adr/0006-mongol-anvil-hammer-master-model.md)（失序门=锤的释放触发）与 [ADR-0009](../../_archive/docs/adr/0009-charge-driven-formation-shock-rout.md)（震慑池=编队状态来源）之上。

---

## 1. 动机

vanilla 与 RBM 的战斗 AI 本质都是**两层反应式效用选择**：队级 `MaxBy(GetTacticWeight)` 选一个脚本化 Tactic，编队级 `MaxBy(GetAiWeight)` 在被打开权重的动作里挑一个。两者都**没有空间理解**（不找弱侧、不集火、不做合成兵种时序），RBM 只是"换皮"——更真实的动作 + 按文化的 Tactic，骨架与 vanilla 同样笨。

目标：用一个**有全局空间视角的中央指挥调度器**，驱动一套**角色化的多编队布局**，做出 Bannerlord/RBM 都没有的协调行为——**绕弱侧/背后冲锋、集火突破口、轻骑护侧后、重骑择时投锤、步兵分队包抄**——向全战系列的合成兵种观感靠拢。

---

## 2. 核心架构决定

```
空间脑(全局空间视角)
  ~7 支编队建成"有朝向的实体" → 弧覆盖判定
  → 哪侧弱 / 谁在溃逃 / 谁需要护 / 砧钉住了没
        ▼
中央调度器(= 自写 TacticComponent，取代 RBM 的 Tactic = Layer 1)
  定军团意图(突破口 / 各编队任务 / 何时放重骑)
  → 给每支编队"打开"对应动作的权重 SetBehaviorWeight
        ▼
动作库(RBM/vanilla 的 behaviors = Layer 2 的手)
  编队在被打开的动作里 FindBestBehavior 挑一个，执行
```

三条互相咬合的决定：

1. **取代 RBM 的 Tactic（Layer 1），而非嫁接它。** RBM 的 Tactic 是死脚本、无空间感、只认 4 个固定角色，加不动（核实见 §3）。我们**自写一个 `TacticComponent`**（引擎标准扩展点，和 RBM 那些 Tactic 同类对象），骨架抄 vanilla `TacticFullScaleAttack`（它已有左右骑兵 + 阶段框架），在上面加新编队/新动作/空间脑。RBM 的**动作库继续用**。
2. **废掉 RBM 的 `ManageFormationCounts` 每 tick 重分，完全自管编队分配。** 这样 7 编队布局才稳得住（核实见 §3）。
3. **7 编队布局用不同的 class 槽**（不建第二个 `Cavalry` 槽，守 0007 字面约束）。

---

## 3. 关键事实（本次已核实，含 file:line）

引擎（`tools/dump/_twdecomp/MountAndBlade/TaleWorlds.MountAndBlade.decompiled.cs`）：

- **`FormationClass` 槽**：`Infantry=0 / Ranged=1 / Cavalry=2 / HorseArcher=3 / HeavyInfantry=4 / LightCavalry=5 / HeavyCavalry=6 / Bodyguard=7 / General=8`；可指挥实编队 8 槽（0–7）。→ 7 编队布局放得下。
- **编队不能"自己决定动作"**：`FindBestBehavior`（:25183）首句 `if (WeightFactor ≈ 0) continue`——**权重为 0 的动作直接跳过**。`AddAiBehavior`（:25141）只加入列表**不设权重**；`SetBehaviorWeight`（:25128）才设 `WeightFactor`。`OnUnitAddedToFormationForTheFirstTime`（:45497）给每编队加了一整套动作但**全 0 权重**。→ **谁打开权重 = Tactic（Layer 1）**；没人打开则编队发呆。这正是中央调度器必须存在的原因。
- **vanilla `TacticFullScaleAttack` 是好模板**：`AssignTacticFormations1121`（~:38220）已有 `_leftCavalry`/`_rightCavalry`（带 `BehaviorSide.Left/Right`）；`Advance/Attack`（~:40575）给左右骑兵挂 `BehaviorProtectFlank`+`BehaviorCavalryScreen`（推进）与 `BehaviorFlank`+`TacticalCharge`（接战）。**`BehaviorReserve` 动作存在**（`SetDefaultBehaviorWeights` ~:38328 给了权重槽）但**vanilla 从不真用它**——正好被我们接管做"重骑后方预备"。
- **`Formation.Split`（:70356）/ `TransferUnits`（:70382）存在**；`MasterOrderController.SplitFormation`（:32915）把人转入第一个空槽（0–7）。→ 引擎原生支持分编队（本方案改为开局直接自管分配，一般不靠 mid-battle split）。

RBM（`tools/dump/_rbmsrc/Tactics.cs`）：

- **`ManageFormationCountsPatch`（:511–569，prefix）** 每 tick 把 agent 按"骑乘+弹→HorseArcher / 骑乘+近战→Cavalry / 步行+远程→Ranged / 步行+近战→Infantry"塞进 4 槽；带守卫 `agent.Formation != null && agent.Formation.IsAIControlled`。→ **这是把多支骑兵合并回 1 个 Cavalry 槽的元凶，必须废。**
- **废掉它是安全的**：RBM 战术只是去**查**"某编队里有没有这类兵"（`ChooseAndSortByPriority` over `IsCavalryFormation` 等），不依赖重分这个动作在跑。废掉后 RBM 战术"陈旧但不崩"，且 RBM 的**战斗/马匹/伤害/动作**系统全是独立补丁、不受影响。
- **RBM 的 Tactic 是死脚本**：`RBMTacticEmbolon` 等 = `GetTacticWeight`（数重骑）+ 无条件 `Advance/Attack` 脚本，零空间感；名字里的 "Split" 是**行为分配**，不调 `Formation.Split`，不真拆编队。
- **`EarlyStartPatch`（postfix on `MissionCombatantsLogic.EarlyStart`）每场按文化重建 tactic 列表**。→ 我们的自写 Tactic 要在它之后注入并胜出（见 §6 风险）。

mod 活代码：`Detection/FormationSensors.cs` 已用 `Agent.Character.GetBattleTier()` 算 `AvgTier`（tier 分档现成）；`Cavalry/CavalryAiShieldMissionLogic.cs` + `Cavalry/RbmCavalryAiSuppressor.cs` 现役（需重构吸收，见 §5.F）。

---

## 4. 编队布局（7 支，槽位映射）

| 角色 | 槽 | 兵源（自动分配） | 默认任务 |
|---|---|---|---|
| 步兵·主线（砧） | `Infantry(0)` | 步行近战 | 正面推进/钉住 |
| 步兵·包抄 | `HeavyInfantry(4)` | 步行近战（与主线均衡分） | 待命，调度令其绕侧/后 |
| 弓兵 | `Ranged(1)` | 步行远程 | 散射/掩护 |
| 骑射 | `HorseArcher(3)` | 骑乘 + 有弓且有箭 | 骑射骚扰 |
| 轻骑·左翼 | `LightCavalry(5)` | 骑乘近战 tier 1–4 + 弹尽骑射 | 调度派任务（护/格斗/冲阵） |
| 轻骑·右翼 | `Cavalry(2)` | 骑乘近战 tier 1–4 + 弹尽骑射 | 调度派任务（同左，对称） |
| 重骑（锤） | `HeavyCavalry(6)` | 骑乘近战 tier 5–6 | 全军最后跟随当预备，失序门开→投锤 |

> 两支轻骑**职责相同、对称左右**，具体干什么由调度每次决定；编队归属只需左右均衡。

---

## 5. 决策详述

### A. 自动编队 = 分类规则（锁定）

每个 agent，**生成时**与**弹药耗尽时**重判，按此树入座：

```
步行 + 远程            → 弓兵 Ranged
步行 + 近战            → 步兵(主线/包抄 两队，按人数均衡)
骑乘 + 有弓且有箭       → 骑射 HorseArcher
骑乘，无弓 / 箭已空:
   ├ 原骑射、箭空了     → 轻骑(任一翼，均衡)        ← 一律轻骑，不看 tier
   ├ tier 1–4          → 轻骑(任一翼，均衡)
   └ tier 5–6          → 重骑 HeavyCavalry
```

- tier 用 `Agent.Character.GetBattleTier()`（已在 `FormationSensors` 用）。
- 边角待确认：**骑乘投枪兵**（标枪、无弓）——有可投掷弹药时进骑射/散兵位、投光按 tier 归近战骑（实现时定，不卡）。
- 分配是**逐 agent 缓存**（`ConditionalWeakTable`），避免每 tick 重算。

### B. 废 RBM 重分 + 自管分配

- **挂钩**：自有补丁使 RBM 的 `ManageFormationCounts` 重分失效（prefix 短路其 agent 重分段；保留它对其它逻辑无害的部分），并**自管**：在 spawn 钩子（`OnAgentBuild`/spawn）按 §5.A 给每个兵入座，含援军。
- vanilla 自身的 `ManageFormationCounts` 也需同样压制，避免它把人重排回默认 4 槽。
- 分配**在 spawn 时一次**（+ 弹尽时重判），不每 tick 重排——比 RBM 每 tick 重分**更省**。

### C. 中央调度器 = 自写 `TacticComponent`

- 继承 `TacticComponent`，骨架抄 `TacticFullScaleAttack`（左右骑兵分配 + Advance/Attack 阶段 + flank/screen 行为模式）。
- 自有 `ManageFormationCounts`：识别 §4 的 7 支并打角色标记。
- **空间脑**（纯函数、可单测）：把 ~7 支建成"有朝向实体"，算弧覆盖 → 输出军团意图：
  - `schwerpunkt`：哪支敌编队是当前突破口（最残/最孤立/已动摇，读 §D 的 ShockPool）。
  - `flankPoint`：突破口的开放弧落点（可达性用 `Scene.GetPathDistanceBetweenPositions` 验）。
  - `screenJobs`：哪支友军被哪个威胁指着 → 派哪翼轻骑去护。
  - `hammerRelease`：失序门（ADR-0006）开 → 放重骑。
- 每 tick 据意图给各编队 `SetBehaviorWeight`（永远软压、不靠 IsAIControlled 夺控）。

### D. 新动作（behaviors）

| 动作 | 真新 / 复用 | 做法 |
|---|---|---|
| 瞄弱点的**持续绕后/侧击冲锋**（含避 braced 枪墙→改侧面） | **真新** behavior | 自写 `BehaviorComponent`，读调度器的 `flankPoint`，先走落点再切冲锋；经 `OnUnitAddedToFormationForTheFirstTime` postfix 加入 |
| 轻骑**护侧后**（左右对称、调度派活） | 复用 + 扩展 0010 | `BehaviorProtectFlank`/`BehaviorCavalryScreen` + 后方守卫自建；调度按 `screenJobs` 配权重 |
| 重骑**后方预备 → 择时投锤** | 复用 `BehaviorReserve` | 平时 Reserve 权重压住后队；`hammerRelease` 时翻成冲锋 |
| 步兵**分队包抄** | 复用现有步兵动作 | 包抄队（`HeavyInfantry` 槽）由调度指目标/落点绕侧后 |
| 集火突破口 | 不是新动作 | 调度把多支的目标统一偏到 `schwerpunkt` |
| 诈退 | **真新但难** | **本轮不做**，记此待后议 |

### E. RBM 集成边界

- **只废**：RBM + vanilla 的 `ManageFormationCounts` 重分。
- **全留**：RBM 的战斗/马匹/伤害/couch/brace/各 behavior。共目标补丁一律 `[HarmonyAfter("com.rbmai")]` + 反射（`AccessTools.TypeByName`），RBM 缺席可降级加载。
- **自写 Tactic 注入**：postfix `EarlyStartPatch` 之后 `AddTacticOption(我们的)` 且使其 `GetTacticWeight` 胜出（或抑制 RBM 的 tactic 选项）。详见 §6 风险。

### F. 与现有 mod 的关系

- `Cavalry/CavalryAiShieldMissionLogic`（无差别 pin `IsAIControlled=false`）与 `Cavalry/RbmCavalryAiSuppressor`：**重构吸收进新调度/编队管理器**——不再无差别压制，而是由调度按编队决定控制权。
- 震慑池（0009）→ 喂空间脑"谁已动摇"；失序门（0006）→ 重骑释放触发。
- `Settings/AnvilSettings`：加本系统总开关 + 子开关；`Diagnostics` 加 `[tactic]`/`[brain]`/`[slot]` 遥测。

### G. 范围

**给双方 AI**（敌人也会绕你后、护他弓兵、择时投锤）——否则打 AI 验证不出效果。沿用 `ScopeFilter`/`PlayerArmyOnly` 仅作可选限定。

---

## 6. 实施顺序（每步绿构建 + 部署 + 在游验证；未验证不声称可用）

1. **自动编队地基**：废 RBM/vanilla 重分 + spawn 钩子自管分配（§5.A/B）。
   verify：CustomBattle 看 `[slot]` 日志——7 支是否稳定存在、不被打散、左右轻骑均衡。**此步它们发呆是正常的**（还没调度器打开权重）。
2. **调度器骨架**：自写 `TacticComponent` 取代 RBM tactic，先用**现成**动作（charge/screen/reserve）驱动 7 支。
   verify：左右轻骑会护侧、重骑压后不乱冲、步兵推进——编队各司其职。
3. **空间脑**：弧覆盖 → `schwerpunkt`/`flankPoint`/`screenJobs`（纯函数先单测，再接调度）。
   verify：集火 + 轻骑朝弱侧/威胁动。
4. **新动作**：绕后/侧击冲锋 behavior + 步兵包抄。
   verify：骑兵绕开放弧、步兵分队包抄。
5. **在游调参**：阈值靠 `[tactic]/[brain]/[slot]` 日志反推。

---

## 7. 风险与备选（否决）

- **废 RBM 重分会不会废掉 RBM tactic？** 不会——RBM tactic 是查询式、不依赖重分在跑（§3 已核实），降为"陈旧但能用"；而我们的自写 Tactic 会取代它当主指挥。**残留风险**：RBM 的 `EarlyStartPatch` 每场重建 tactic 选项 + 我们的 tactic 必须胜出 `GetTacticWeight`。缓解：postfix 注入我方 tactic 并给高权重，或直接抑制 RBM 的 tactic 选项。**此为首要在游验证点**。
- **抖动/性能**：分配改为 spawn 时一次（+弹尽重判），比 RBM 每 tick 重分省；`[slot]` 监控人数稳定不回弹。
- **降级预案**：若"完全自管 + 自写 Tactic"在游不稳，退回 ADR-0008 的"借一个槽 + postfix 抢权 + 不换 RBM tactic"较温和方案，并在本 ADR 记失败。
- 否决：① 嫁接 RBM 的 Tactic（无智能可加，只剩空壳，§3）；② 第二个 `Cavalry` 槽（引擎无此槽，守 0007）；③ 全量 `SetControlledByAI(false)` 手动驾驶所有编队（丢掉 RBM 动作执行层，表面积爆炸）；④ 诈退（本轮）。

---

## 8. 待你确认 / 实现时再定的小项

- 骑乘投枪兵归骑射还是按 tier 归近战骑（§5.A 边角）。
- 自写 Tactic 胜出 vs 抑制 RBM tactic 选项，二选一（§6，倾向在游试注入+高权重）。
- `CavalryAiShield`/`RbmCavalrySuppressor` 重构的确切边界（§5.F）。
