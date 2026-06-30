# AnvilAndHammerAI 架构与原理

> 本文档以**当前源码**为唯一依据,描述 mod 实际的运行机制与设计约定,是本项目唯一的权威原理文档。
> 适用版本:游戏 v1.3.15 / RBM v4.2.20。所有常量值均摘自源码,标注了 `文件:行`。

---

## 0. 一句话理念

**本 mod 是一个"权重设定器(weight-setter),不是状态机。**

它从不接管编队的逐帧驾驶,而是**每 ~0.5 秒**对每支编队 `ResetBehaviorWeights()` 后按当前作战计划重新 `SetBehaviorWeight<T>()`,重新把自己想要的那个行为顶到权重最高,让引擎的行为仲裁器选它。原版与 RBM 的行为库继续在底层运行。

> **软压制 → 硬接管(升级)。** 早期单纯靠"高频清零重盖、最后盖章取胜"软压制,两次重设之间存在 ~0.5s 的"夺权窗"。现额外经 `BehaviorWeightOwnerPatch` **硬接管**:拦截 vanilla/RBM 战术层对"**mod 正在驱动的编队**"的 `WeightFactor` 写入(Prefix `return false`),使 mod 成为这些编队的**唯一**权重权威。归属由 `WeightOwnership` 记账(调度器每次 `Drive` 后 `MarkStamped`,`GraceSeconds=1.5s` 宽限窗 > 调度间隔);**未被 mod 接管的编队**(玩家亲控让行 / `ScopeFilter` 外 / 调度关 / 非野战)从不记账,其 vanilla/RBM 权重照常生效,绝不会因无人设权而呆滞。详见 §5。

由此推出三条贯穿全局的设计:

1. **叠加,而非替换。** 共目标的 Harmony 补丁一律 `[HarmonyAfter("com.rbmcombat"/"com.rbmai")]` + `[HarmonyPriority(Priority.Last)]`,只**在 RBM 算出的最终值上做一次乘法**,绝不前缀覆盖或重算 RBM 内部逻辑。
2. **反射容忍。** RBM 与 SandBox 类型**从不在 csproj 引用**,全部 `AccessTools.TypeByName` 运行时解析;缺失则记日志跳过,核心不受影响。
3. **角色即槽位。** 一支编队的"角色"就是它的 `Formation.FormationIndex`(被分配到的 `FormationClass` 槽);骑乘攻击者的角色挂在骑手身上(`mount.RiderAgent.Formation.FormationIndex`)。

> **关于 `SetControlledByAI`:** 与"权重设定器"理念一致,mod 不用它来驱动行为。**唯一**的调用在 `CommandSchedulerMissionLogic.Drive()`(调用行 `CommandSchedulerMissionLogic.cs:310`):当某编队被 RTS 摄像头/指挥系统降为非 AI 控制时,做一次**控制权回收** `SetControlledByAI(true)` 以让权重生效——这是"让权重有效"的防御性补救,不是行为指令。

---

## 1. 总体管线

```
                       ┌─────────────────────── 编队级士气(地基,双方恒生效)─────────────────────┐
                       │  FormationShockPool:每编队一个"震慑池",吸收 5 种压力,越阈值→整队溃逃    │
                       └───────────────────────────────────┬───────────────────────────────────┘
                                                            │ 谁已动摇(供战术脑读)
  侦测层(只读传感)                                          ▼
  FormationScanner ─ 每编队每拍一份融合快照 ┐      ┌──────────────────────────────────────────────┐
  FormationStrength ─ 全局唯一"强度"度量    ├──→  │  阶段二:战术脑 TacticalBrain.Plan(纯函数)      │
  RangedThreatSensor / ThreatAssessor       │      │  把敌编队建成"有朝向实体" → 选突破口 → 算侧/后    │
  FormationAvoidance ─ 路径前瞻避让          ┘      │  落点 → 输出 BattlePlan(含各闸门)               │
                                                    └───────────────────┬──────────────────────────┘
  阶段一:自动编队 AutoFormation                                         │ BattlePlan
  每 0.5s 把每个兵归入 7 个角色编队(各占不同槽)                          ▼
  + 压制 RBM/原版每拍重分,让 7 编队稳住            ┌──────────────────────────────────────────────┐
            │                                       │  阶段三:指挥调度器 CommandScheduler(每 0.5s)   │
            └─── 7 支稳定编队 ────────────────────→ │  逐角色 Reset+SetBehaviorWeight 落实计划         │
                                                    │  + 威胁反应覆盖层(据守/后撤/反冲锋)             │
                                                    │  + 玩家手令退让                                 │
                                                    └───────────────────┬──────────────────────────┘
                                                                        │ 抬高某行为权重
                                                                        ▼
                                          引擎/RBM 行为库 + 4 个自定义 BehaviorComponent
                                          (FlankCharge / HorseArcherKite / ReserveStaging / ThreatReaction)
                                                被选中者执行移动/朝向/阵型/射击指令

  伤害与速度模型(Harmony 后置补丁,叠在 RBM 之上):方向性伤害 / 分角色移速 / 重骑冲锋撞穿
```

三个 MissionLogic 都以 `TickGate(0.5f)` 节流,故"自动编队 / 战术脑+调度 / 士气"各自每 ~0.5s 重算一次。

---

## 2. 加载与补丁引导(`SubModule` / `PatchBootstrapLogic`)

**加载期不打任何补丁。** `OnSubModuleLoad`(`SubModule.cs:30`)只:① 构造 `new Harmony("com.rangt.anvilandhammer")`;② 启用 UIExtenderEx(`UIExtender.Create("AnvilAndHammerAI").Register(asm).Enable()`,必须在任何 movie 加载前启用,且实例存字段防 GC)。两步各自 try/catch,失败只记日志、不阻断。

**每个 mission 注册全部 MissionLogic**(`OnMissionBehaviorInitialize:58`),**故意不在此判 `IsFieldBattle`**——此时 `MissionTeamAIType` 通常还是 `NoTeamAI`,`IsFieldBattle` 会假性为 `false` 误杀整个 mod;各 MissionLogic 改在自己的 tick/谓词里、当该值可靠时自行 self-gate。注册顺序(固定,有意义):

1. `PatchBootstrapLogic` — 最先,尽早触发补丁安装
2. `AutoFormationMissionLogic` — 阶段一
3. `CommandSchedulerMissionLogic(shockPool)` — 阶段二+三,注入共享震慑池
4. `RangedThreatSensor` — 从第一帧开始计数(喂"受远程攻击"压力)
5. `ChargeImpactSensor` — 从第一帧开始计数(监听真实骑兵冲撞命中,喂"冲锋冲击"震慑)
6. `FormationMoraleMissionLogic(shockPool, rangedSensor, chargeSensor)` — 士气层
7. `AnvilDiagnosticsMissionLogic(shockPool)` — 只读诊断(排在士气后,读到同拍数据)
8. `BattleEndSafetyMissionLogic(shockPool)` — 防过早判负
9. `MissileTrailMissionLogic` — 箭矢轨迹可视化(纯渲染,受 MCM 开关 + 仅 RTS/非操控角色门控)

此处创建**共享** `FormationShockPool`、`RangedThreatSensor`、`ChargeImpactSensor`,并 `MoraleReadout.Register(shockPool)` 把同一个池交给只读 UI 读出。震慑池的读者:士气层(写)、诊断(只读)、战败安全(读"是否触底")、调度器(读作"放锤"判据)、UI 读出。

**补丁惰性安装**(`EnsurePatched:102`,幂等):由 `PatchBootstrapLogic` 在 `OnBehaviorInitialize`(AfterStart,`Mission.Current` 已就绪)与每帧 `OnMissionTick`(首帧兜底)触发。守卫:`_patched` 已 true 或 `_harmony==null` 立即返回;`Mission.Current` 为 null 或 `!IsFieldBattle` 返回——**补丁只在就绪的野战里装**。`_patched` 在打补丁**之前**置 true,防重入二次 `PatchAll`。

- **先预热 `MovementOrder` 静态构造**(`MovementOrder.MovementOrderStop`):必须在 `Mission.Current` 非空时跑一次它的 cctor,否则后续补丁在 `Mission.Current==null` 时触发该 cctor 会 NRE 并**永久毒化** `MovementOrder` 类型。
- 然后三段独立 try/catch:① `PatchAll`(收集所有 `[HarmonyPatch]` 特性补丁:伤害系统、原版 resort 压制);② `RbmResortPatch.Apply`(反射压制 RBM 重分);③ `FormationSpeedPatch.Apply`(反射挂 SandBox/CustomBattle 测速模型)。

---

## 3. 阶段一:自动编队(`AutoFormationMissionLogic` / `TroopClassifier`)

每 ~0.5s(`TickInterval=0.5f`),对在范围内的每个**活人**士兵重判角色并归入对应槽。部署阶段(`MissionMode.Deployment`)不运行。

### 7 个角色 → `FormationClass` 槽位映射(`TroopClassifier.SlotFor`)

| 角色 | 槽位(编号) | 兵源 | 默认任务 |
|---|---|---|---|
| 主步兵(砧) | `Infantry`(0) | 步行近战 | 正面推进/钉住 |
| 包抄步兵 | `HeavyInfantry`(4) | 步行近战(与主线按强度配比) | 待命→绕侧/后 |
| 弓兵 | `Ranged`(1) | 步行远程 | 散射/掩护 |
| 骑射 | `HorseArcher`(3) | 骑乘 + 默认远程兵种 | 风筝骚扰 |
| 左轻骑 | `LightCavalry`(5) | 骑乘近战 tier 1–5(+弹尽骑射) | 调度派活(护/侧击/拦截) |
| 右轻骑 | `Cavalry`(2) | 同左,左右对称均衡 | 同左 |
| 重骑(锤) | `HeavyCavalry`(6) | 骑乘近战 tier ≥6(仅顶级精英) | 后方预备→择时投锤 |

七个角色各用**不同**的 `FormationClass` 槽,守住"不建第二个 `Cavalry` 槽"的硬约束。

### 分类与配平

- **粗分类**(`TroopClassifier.Categorize`):`mounted+远程→HorseArcher`;`mounted 近战→ GetBattleTier()≥HeavyCavMinTier(6) 则 HeavyCav,否则 LightCav`;`步行+远程→Archer`;`步行近战→Infantry`。"是否远程"用引擎给的 `Character.DefaultFormationClass`(不在出生时扫弹药)。
- **左右/主侧配平**用**强度累加值(tally)**,不是 `Formation.CountOfUnits`——因为同一拍内 `a.Formation=target` 不会立刻反映到 `CountOfUnits`,用实时计数会把首批人全塞进一个角色。强度 = `Character.GetBattleTier()` 之和(全 mod 统一度量)。轻骑左右取 tally 较小一侧(并列偏左);主步兵 vs 包抄按 **50:31** 的目标配比贪心填(包抄略小)。
- **角色粘滞**:首次遇到才 `Resolve`,缓存进 `ConditionalWeakTable`,之后只重申不重算。**唯一**的后续变更:骑射**打光弹药**后单向转入较空的轻骑翼(`MountedOutOfAmmo` 实扫真实弹药),不可逆转回骑射。
- 玩家英雄 `Agent.Main` 在 `RespectPlayerOrders` 开时免于自动归队。

### 压制 RBM/原版重分(两个互补补丁,缺一不可)

- **`RbmResortPatch`**(反射):前缀 RBM 的 `ManageFormationCountsPatch.PrefixSetDefaultBehaviorWeights`,对范围内队伍短路其"按兵种塞回 4 槽"的重分,并令 `__result=false` 使原版 `ManageFormationCounts` 也被跳过。RBM 类型经 `AccessTools.TypeByName` 解析,缺失则跳过。
- **`FormationResortPatch`**(特性补丁,`[HarmonyBefore("com.rbmai","com.rbmcombat")]`+`[HarmonyPriority(Priority.First)]`):前缀 `TacticComponent.SplitFormationClassIntoGivenNumber`(多数战术合并同类编队都走这个原语),范围内返回 false 阻断。
- **为何两个都要**:`FormationResortPatch` 堵的是"合并原语",`RbmResortPatch` 堵的是 RBM **绕过该原语**直接改 `agent.Formation` 的重分——任一单独都漏。

> 阶段一结束时,7 支编队"先发呆"是**正常**的——还没有调度器给它们打开任何行为权重。

---

## 4. 阶段二:战术脑(`TacticalBrain.Plan`,纯函数)

`Plan(myTeam, mission, shockPool, scanner, baseRoutThreshold) → BattlePlan`,**无副作用**,只读传感、返回一份新计划。所有"是否执行"的门控都在调度器里做。

### 关键判定

- **砧咬合门 `BattleJoined`**(所有侧/后动作的前置):取主步兵编队,**深度感知**地判它是否已贴上敌方步兵——`reach = JoinDistance(15) + 我方半深 + 敌方半深`,中心距平方 < reach² 即"咬上"。优先认敌步兵,否则认任意敌编队。**纯骑兵军(无步兵编队)直接 `BattleJoined=true`**,不被冻结。
- **突破口(schwerpunkt)选择**:先按强度筛"显著"敌编队(`FormationStrength ≥ 0.15 × 最强敌编队`,相对而非定值)。对每个候选算
  `score = arcVal × (1 + weakness) × prox`
  - `arcVal`:开放弧——**后方开 1.0 / 仅某翼开 0.7 / 全被遮 0.25**;某点"开"= 28m 内无**其它**敌编队中心。
  - `weakness = poolFrac + depletion + routingFraction`:`poolFrac` = 该敌编队震慑池 / 其自身按 tier 调整的溃逃阈值;`depletion = 1 − 存活比`;`routingFraction` = 正在逃的比例。
  - `prox = 1/(1 + 我方骑兵参考点到目标距离 × 0.01)`,越近越大。
  取分最高者为 `Target`,其最佳弧落点为 `FlankPoint`(后方优先,供包抄步兵+重骑用)。
- **左右合围分点**:把目标的两个**侧翼**点按"我方左/右半平面"分给左/右轻骑(`LeftCavPoint`/`RightCavPoint`),避免某翼骑兵横穿战场穿过友军。
- **放锤门 `ReleaseHammer`**:`目标震慑池 ≥ 0.75 × 其自身 tier 调整阈值`(濒临崩溃)。读 `pool.TryGet`,开局未写池时安全地为 false。
- **护线门 `CoverInfantry`/`ThreatCav`**:对主步兵/包抄/弓三类被护编队,扫 60m 内、强度 > 0.2×被护强度的**近战**敌骑(排除骑射),取最近者为 `ThreatCav`。
- **追击**:在显著性早返**之前**算(溃兵会脱离编队使强度归零)——全军 ≥80% 在逃 `Pursue=true`;≥20% 在逃 `LightCavPursue=true`。

接近点几何(`FormationGeometry.ApproachPointsFor`):以敌编队朝向(失效时退化为"指向参考点")为 face,`+perp` 为左翼、`-perp` 为右翼、`-face` 为后方,各加 `Standoff(14)` + 半宽/半深。

---

## 5. 阶段三:指挥调度器(`CommandSchedulerMissionLogic`)

每 0.5s 对每支在范围内的队伍:`TacticalBrain.Plan(...)` → `DriveTeam(...)`。联合门控:`Enabled && AutoFormationEnabled && CommandSchedulerEnabled` 且野战非部署。

`DriveTeam` 按槽取出 7 支编队,算几个门(joined/release/hammerApproaching/pursue),再对每个角色走 `Drive(formation, 闭包)`。每个闭包模式:`Reset(f)` → 可选 `Ensure*` 挂载自定义行为 → 可选 `TryReact` 覆盖(命中则提前返回)→ 一个或多个 `W<Behavior>(f, 权重)`。

### 逐角色权重(节选,均在 `BattleJoined` 等门后)

| 角色 | 行为 | 权重 | 条件 |
|---|---|---|---|
| 主步兵 | `BehaviorAdvance` / `BehaviorDefend` | 1 / 1.2 | 推进;咬合后加防御。**从不给 charge**(无自由混战),并 MatchWidth 到 `AnvilTarget` |
| 包抄步兵 | `FlankChargeBehavior` | 1.2 | `joined && HasTarget`;否则去侧后 staging |
| 弓兵 | `BehaviorScreenedSkirmish`+`BehaviorSkirmishLine` | 1+0.5 | 否则反应层后撤 |
| 骑射 | `HorseArcherKiteBehavior` | 1 | 风筝 |
| 左/右轻骑 | `FlankChargeBehavior`(护线 1.8 / 侧击 1.4) 或 `Screen` | — | 优先级:护线>追击>侧击突破口>护住主步兵侧翼 |
| 重骑 | `FlankChargeBehavior` | 2 | `joined && releaseNow && HasTarget` 才投锤;否则深预备/前压 staging |

### 投锤时序

维护 `Dictionary<Team,float> _joinedSince`:首次 `BattleJoined` 记时,断开则清。
`releaseNow = plan.ReleaseHammer || (joined && 咬合时长 ≥ HammerReleaseDelay(12s))`——**时间兜底**,即便敌池永远到不了阈值,重骑也终会投。
**前压预备** `hammerApproaching`:`目标池 ≥ 0.5×阈值` 或 咬合时长 ≥ 12×0.6=7.2s 时,重骑提前移到突破口开放弧侧 `HeavyCavChargeReadyDist(80m)` 处蓄势,而非死守深预备。

### 追击短路

`plan.Pursue` 时整队提前分支:只有左右轻骑得 `BehaviorCharge`(2)去追溃兵,其余全 `BehaviorStop`(1)保持队形不乱追。**这是唯一被许可的自由冲锋**。

### 溃逃后撤(`HoldRout`)与远程编队级聚焦

- **溃逃后撤**:`Drive` 在设权前先查该编队震慑态——`RoutLatched`(普通溃逃)时改走 `HoldRout`:`ThreatReactionBehavior` 切 `Mode.Rout`(整队背对最近敌编队有序奔退,**面朝撤离方向 = 背部朝敌**,故被追击时按背袭/追击判定吃满溃逃伤害加成),不逐兵 Panic、不脱编(保持成建制,脱火后情势池衰减即可解锁集结)。**仅决定性崩溃(`Bottomed`,棘轮触底)才逐兵 `Panic` 四散**(见 §8)。
- **远程编队级聚焦索敌**(`ApplyRangedFocus`):弓兵/骑射不再各打各的——对整支编队**逐兵硬锁**:每个 agent `SetTargetAgent(目标编队内活体,轮流分摊)` + `SetAutomaticTargetSelection(false)`(关自动索敌防 vanilla/RBM 改回),实现"整队只打同一支敌编队"。目标 = `plan.Target`(突破口)→ `plan.AnvilTarget` → 无则 `ReleaseRangedFocus` 释放回原生。被骑兵贴脸后撤 / 溃逃(`HoldRout`)时同样释放以便自卫。(`SetTargetFormation` 仅是索敌"偏好",实测不足以禁止分散,故升级为逐兵硬锁。)与 RBM 共存:RBM 只 patch 弹道、不碰索敌。

### 控制权、权重归属与玩家退让(`Drive()` 这一咽喉点)

- **玩家手令退让**:`PlayerControl.IsPlayerCommanded(f)`(= `RespectPlayerOrders` 开 && 玩家为将 && 该编队 `!IsAIControlled`)为真时,**不 Reset、不改任何权重**直接返回,不踩玩家手令。MP/replay 下 `IsAIControlled` 不可靠,故该退让在网络/回放中禁用。
- **控制权回收**:否则若 `!f.IsAIControlled` 调一次 `f.SetControlledByAI(true)`(全 mod 唯一一处)让权重生效——是补救,非状态机。
- **权重归属盖章(硬接管)**:`Drive` 把对该编队的 `Reset+SetBehaviorWeight` 包在 `WeightOwnership.ModStamping=true` 内(放行自身写入),收尾 `MarkStamped(f, now)` 记账;`BehaviorWeightOwnerPatch` 据此拦掉 vanilla/RBM 对该编队的后续设权(见 §0)。
- `W<T>` 守卫:`w<=0` 跳过,且仅当 `GetBehavior<T>()!=null` 才 `SetBehaviorWeight<T>`(防缺行为抛 `MBException`);自定义行为经 `Ensure*` 惰性 `AddAiBehavior`。

---

## 6. 威胁反应覆盖层(`TryReact` + `ThreatReactionBehavior`)

调度器内的**严格优先层**:当某编队被敌近战骑兵**贴脸**时,临时把 `ThreatReactionBehavior` 抬到 `ReactionWeight(3f)`——高于一切常规角色权重(最高 2f),必胜行为仲裁——并 `return true` 跳过常规权重设定。总开关 `ThreatReactionsEnabled`。

- **威胁判定**:`ThreatAssessor.NearestChargingCav(f, team, m, range, ratio)` = 范围内、强度 ≥ `ratio×自身强度` 的最近敌近战骑编队。各角色参数不同:步兵据守(30m, 0.10)、弓兵/骑射后撤(28m, 0.05)、骑兵反冲锋(30m, 0.15,阈值更高以免小股骚扰打断锤的时序)。
- **四种姿态**(`Mode`):**Brace**(步兵立盾墙,被 ≥4 扇区围则改方阵 Square,原地不动只朝向威胁,免被背身冲穿)、**FallBack**(弓兵退到主步兵身后集结点或直接远离 30m,Loose 阵型)、**Counter**(骑兵:太近先后撤 `CounterRunup(22)` 蓄距,`_windupDone` 闩防来回抖,再 `ChargeToTarget`)、**Rout**(整队背对威胁有序奔退、**面朝撤离方向=背部朝敌**)。前三种由 `TryReact` 在遇袭时触发;**Rout 不经 `TryReact`,而由调度器 `HoldRout` 在编队普通溃逃锁存(`RoutLatched`)时驱动**(见 §5/§8)。
- **迟滞去抖**:有威胁立即进入;威胁消失后保持 `HoldSeconds(2)` 再退出;Counter 目标被歼则提前结束;退出时清回 None 以便下次重新蓄势。
- **被committed状态抑制**:主步兵/包抄仅在 `!joined/!flanking` 才据守、轻骑仅在 `!committed` 才反冲、重骑仅在 `!hammering` 才反冲——**已投入的合围或决定性投锤不会被小股骚扰取消**。

---

## 7. 自定义行为库(4 个 `BehaviorComponent`)

mod 仅有的"真·新动作",全部直接继承引擎 `BehaviorComponent`(**零反射、纯公开 API**)。共同模式:只有当 `self.AI.ActiveBehavior == this`(即靠权重被选中)时才在 `TickOccasionally` 里重算并下发 `SetMovementOrder/SetFacingOrder/...`——不被选中即惰性,完全契合"权重设定器"。四者都重写 `GetBehaviorString()` 返回字面 `TextObject`,绕开基类按类型名查本地化会抛 "text … doesn't exist" 的坑。

- **`FlankChargeBehavior`**(唯一真新动作,左右轻骑/重骑/包抄共用):走到突破口的开放弧落点(沿"远离敌中心"方向额外外推一段 run-up:骑 34m/步 26m,形成更大的侧后切入角),逼近到 `自身宽×1.2+6m` 内即切 `ChargeToTarget`(此最终段**故意绕过避让**以真正撞上)。`_roundedFor` 闩记录"已绕过侧翼线":未绕过时避开**所有**编队(含目标,让路径绕侧而非穿正面),绕过后只把目标排除出避让(直插)。
- **`HorseArcherKiteBehavior`**:站在敌前 ~60% 调整射程处(夹于 [30,55]m),始终在"敌→我方主步兵"连线的敌前侧、绝不绕到敌后;横向扫动用 `sin(CurrentTime×0.22)×35°` **确定性**摆动(非随机,存档可复现);自由射击、**从不冲锋**——这正是相对原版骑射行为的意义。
- **`ReserveStagingBehavior`**:移到调度器注入的 staging 点并 `FormOrderDeep`(纵深块,而非一字宽线,修复包抄/重骑在后方铺成宽线互相重叠)。`target=null` 即避开**一切**编队(防重叠)。左右轻骑的护侧位与包抄/重骑的后方预备位**复用同一行为**,只是注入点不同。
- **`ThreatReactionBehavior`**:威胁反应层的唯一动作(Brace/FallBack/Counter),细节见 §6。

> 无主步兵的纯骑兵军里,Screen/Staging 退化为原版 `BehaviorReserve`。

---

## 8. 编队级士气:震慑池(`FormationMoraleMissionLogic` 等)—— 地基

整支编队会**一起崩**,而非逐兵掉血。这是 mod 的结构性基础,也是**唯一无视 `ScopeFilter`、对敌我双方恒生效**的子系统;纯 MissionLogic,**零 Harmony、零反射**,士气读写走公开 `AgentComponentExtensions.Get/SetMorale` 与 `CommonAIComponent.StopRetreating`。

每编队一个 `FormationShockState`(弱键 `ConditionalWeakTable`)。**有效压力 `Effective` = 情势池 `Pool` + 伤亡地板 `CasualtyFloor` + 冲锋震慑 `ChargeShock`**,三者时间行为不同,越 `有效阈值` 即整队溃逃:

- **情势池 `Pool`**(级联+包围+远程三个**情势型**源):每 0.5s 用**真实** dt `Pool += Σ速率×dt` 再 `Pool -= PoolDecayPerSecond(3)×dt`,两步 `≥0`,**封顶** `有效阈值×PoolCapFactor(1.25)`(防无界增长——威胁一缓即可数秒内衰减回解锁线)。
- **伤亡地板 `CasualtyFloor`**(**持久、可重整**):`= CasualtyErosionGain(90) × 自上次重整以来损失的当前兵力比`;**不衰减**(伤亡是永久侵蚀),仅在集结时基准归位→归零。tier3 损失 35% → 90×0.35=31.5 ≈ tier3 有效阈,恰可独力压垮(参考值 #1)。由编排层每拍计算,**非** `IMoralePressure`。
- **冲锋震慑 `ChargeShock`**(`ChargeImpactSensor` 监听**真实**骑兵冲撞命中累加):每次 `IsHorseCharge` 命中计 `强度比 × 角色(重骑×3/轻骑×1) × 方向(背1/侧0.5/正0.15)`,**半衰期 60s** 慢衰减,编排层每拍镜像入 `ChargeShock`;溃逃即 `Discharge` 清零(已兑现,不滞留逼其反复再溃)。1 次重骑背冲 ≈42.8≥阈(必垮),60s 内 3 次轻骑背冲 ≈31.5=阈(参考值 #2/#3)。

有效阈值 = `PoolRoutThreshold(默认30) × TierResist(avgTier)`;`TierResist = clamp(1+(avgTier−2.5)×0.10, 0.6, 1.3)`——**高 tier 更难崩、低 tier 更脆**。

### 情势型压力源(`IMoralePressure`,各返回瞬时速率 /秒,积分进情势池)

| 源 | Tag | 公式(摘自 `MoralePressure.cs`) | 可关 |
|---|---|---|---|
| 级联 | csc | 邻编队溃逃比(排除自身)超 `CascadeNeutral(0.15)` 部分 ×`CascadeGain(12)`,封顶 `CascadeCap(8)` | 否 |
| 包围 | enc | ≥`EncircleMinSectors(4)` 个方向有敌:`EncircleGain(2.5)×coverage×density`(density 封顶 2) | 否 |
| 远程 | rng | `RangedGain(5.0)×传感器威胁/人数×MCM「挨射士气影响」%`(命中身/盾/近失加权指数衰减,半衰期 20s) | ✅ `RangedPressureEnabled` |

> **伤亡**与**冲锋**不再是情势型源:伤亡是上文每拍算的**持久地板**,冲锋是 `ChargeImpactSensor` 累加的 `ChargeShock`——二者各有持久/慢衰减语义,不进情势池。级联只看**邻队**(排除自身)杜绝自激雪崩;首拍以自身为 prev。`RangedGain` 早期被砍到 0.25(几乎不影响士气),现按实测调至 **5.0**。

### 决策(`Decide`)

- **上升沿一次性溃逃**:`Effective ≥ 有效阈值` 且未 latch → 整队溃逃(`RoutLatched`,记时)。
- **越打越脆(棘轮)**:每次整队溃逃 `RatchetLevel++`;集结目标随之降:`effRallyFloor = RallyMoraleFloor × RatchetDecayPerBreak(0.55)^level`;满 `RatchetBottomLevel(3)` 档则 **`Bottomed` 触底,永不再集结**。
- **集结门(双迟滞)**(任一不满足即不集结):`Bottomed`→否;"大势已去"(本队活人占全场 < `LetGoTeamShareThreshold(0.12)`)→否;距上次溃逃 < `RallyDelaySeconds(默认20)`→否;**情势池** Pool 未跌回 `阈值×RoutClearFactor(0.5)`(火力/包围明显退去)→否;否则集结。时间闸 + 池闸两道杜绝"延时一到又越阈再溃"的每拍抖动。
- **援军重整**:已 `Bottomed` 编队的原溃兵逃尽后,若被援军重新填充(有非逃成员)且距触底已过 `BottomedRefillResetDelay(2s)` → `ResetCollapse` 视作新战力(免援军一并被恐慌)。

### 溃逃落地:编队级后撤 + 决定性逐兵恐慌(`MoraleEffects` / 调度器)

- **普通溃逃**(`RoutLatched` 未触底):**不逐兵 Panic**(逐兵 Panic 触发引擎 `OnFleeing` 把兵踢出编队、令集结无法关联回去 → 亚秒抖动)。改为**编队级整体后撤**——调度器 `HoldRout` 走 `ThreatReactionBehavior.Mode.Rout`(背朝敌有序奔退,见 §5/§6),保持成建制;脱火后情势池衰减、池闸+时间闸满足即集结。
- **决定性崩溃**(`Bottomed`):`MoraleEffects.PanicAgent` 逐兵 `CommonAIComponent.Panic()` 四散(`IsRunningAway` → 吃追击伤害加成),morale 压到 `PanicFloorMorale(0.005)`——**只降不升、严格 >0**(不变量 `0.001 < 0.005 < 0.01[原版 panic 阈]`,绝不 `SetMorale(0)`)。触底不集结 → 无脱编↔召回对冲。
- **集结**(`RallyAgent`):只对正在逃者——先 `StopRetreating()`(单**升** morale 不解溃逃闩),再把 morale 补到 `effRallyFloor`。
- **RallySweep**:扫 `team.ActiveAgents` 把已脱离编队的散溃兵 `StopRetreating`(它们不在编队成员里),好让自动编队下一拍归位。

> UI 士气读出 `MoraleReadout.TryGetRemaining` = **同一个池**的 `1 − Effective/有效阈值`(再以存活比封顶);`MoraleReadout.IsRouting`(= `RoutLatched || Bottomed`)驱动标记图标的**溃逃闪烁**(见 §12)。

---

## 9. 侦测/传感层(`Detection/`)—— 只读

- **`FormationScanner.Scan`**:每编队每拍**一次** `ApplyActionOnEachUnit` 走完,产出融合快照 `FormationSnapshot`(Count、RoutingFraction、AvgMorale、AvgTier、CasualtyRatio、LocalEnemyCount、OccupiedSectors、NearestEnemyCavDist)。复用 visit 委托与扇区桶,稳态**零堆分配**。刻意读 `QuerySystem.CasualtyRatio`(`.Value` 会自动重算),不读 `...ReadOnly`(被软压制的编队不跑 team AI,ReadOnly 缓存会冻在初值)。
- **`FormationStrength.Of`**:全 mod **唯一**的"强度/规模"度量 = 活人 `GetBattleTier()` 之和(= 人数 × 平均 tier),按 `Mission.CurrentTime` 每帧记忆化。**约定:任何地方都不比原始人头数**,一律走它。
- **`RangedThreatSensor`**:`OnMissileHit` 按命中身/盾/近失(权重 1.0/0.5/0.25)累加每编队威胁(只算敌方射向该编队的);`OnMissionTick` 用 `factor=exp(−1.5×dt)` 真指数衰减(长帧也不会一步归零),低于 0.01 剔除。喂士气"受远程攻击"压力源。
- **`ChargeImpactSensor`**:`OnAgentHit` 监听**真实**骑兵冲撞命中(`IsHorseCharge`),按 `强度比 × 角色(重骑3/轻骑1) × 方向(背1/侧0.5/正0.15)` 累加每编队"冲击单位",半衰期 60s 慢衰减;编排层每拍镜像入士气 `ChargeShock`,编队溃逃即 `Discharge` 清零。零 Harmony、对双方恒生效。
- **`ThreatAssessor.NearestChargingCav`**:供反应层用,范围+强度比筛最近敌近战骑(排除骑射)。
- **`FormationAvoidance.Steer`**:路径前瞻**纯垂直**避让——前瞻距离随速度自适应([12,90]m),只侧移不后退、不会被正面挡停;重叠时硬推开。`MoveTo` 是统一的"去某点"构造。最终冲锋段**不**走它(要撞上目标)。
- **`TickGate`**:固定间隔节流值类型,`Ready(dt, out elapsed)` 返回**真实累计 dt** 供按真实时间积分。

---

## 10. 伤害与速度模型(`Combat/`)—— 叠在 RBM 之上

三个**后置**补丁,只读/只乘最终值,绝不前缀覆盖或重算 RBM 内层伤害/护甲。受 `ScopeFilter`(按**发起方**队伍判)+ `IsFieldBattle` + 各自 MCM 开关门控。

- **`DamageSystem`**(`AgentApplyDamageModel.CalculateDamage` 的单一 Postfix,`[HarmonyAfter("com.rbmcombat")]`+`Priority.Last`):把**最终** `__result` 乘**恰好一次**(一个方向/追击因子 × 一个可选冲锋因子)。
  - **投射**(`IsMissile`):跳过方向/追击/冲锋缩放(那些仅近战,套到箭上会把背身误判成背击),只乘一个统一的 `RangedDamageMultiplier`(MCM「远程武器伤害倍率」,默认 0.8)后返回;友伤、被盾挡仍直接返回不动。
  - **方向**优先用编队级判定 `FormationHitDirection`(受击编队朝向 vs 攻击者→受击者approach,阈值 `Dot 0.3`:后2/前0/侧1),编队缺失/失效时退回逐 agent 判定。
  - **侧后加成更强的 4 个角色**(`IsFlankRole`):包抄步兵 `HeavyInfantry`、左轻骑 `LightCavalry`、右轻骑 `Cavalry`、重骑 `HeavyCavalry`。
  - **追击**:打逃兵用 `PursuitMultiplier(1.7)` 覆盖一切;开 `PursuitGuaranteedKill` 时把伤害顶到"血量+1"保证击杀。
  - **冲锋因子**:马冲(`IsHorseCharge`)时按骑手角色——重骑 `HeavyCavChargeMult(1.7)`、轻骑 `LightCavChargeMult(0.8)`。
- **`FormationSpeedPatch`**(反射挂 `SandboxAgentStatCalculateModel` + `CustomBattleAgentStatCalculateModel` 的 `UpdateAgentStats`):按角色乘移速——**步兵分支**只给包抄步兵 `MaxSpeedMultiplier ×FlankInfantrySpeedMult(1.25)`;**坐骑分支**按骑手角色乘 `MountSpeed`(轻骑 1.1 / 骑射 1.15 / **重骑 0.8**),骑兵的跑动只在坐骑分支处理以免双乘。SandBox 类型反射解析,缺失记日志跳过。
- **`ChargePlowThroughPatch`**(`DecideAgentKnockedDownByBlow` 的 Postfix):重骑对**敌**马冲时强制 `__result=true`(击倒)——把挡路目标移出马的路径,使其少减速、撞穿。这是"降低冲锋减速"的可改代理(物理减速是原生 C++、无可设属性)。只对敌、绝不撞翻友军。

---

## 11. 作用范围与安全

- **`ScopeFilter.Applies(team/formation/agent)`**:`PlayerArmyOnly` 关时一切队伍都驱动;开时只驱动 `IsPlayerTeam`,敌方回落到原版/RBM。它在**发起方**的队伍上判。**它门控**:自动编队、统一指挥、骑兵合围、伤害分布。**它不门控**:士气(永远双方)、战败安全(独立按"是否玩家友军"判,与该开关无关)。
- **`BattleEndSafetyMissionLogic`**:`AfterStart` 末位 `+=` 订阅 `Mission.CanAgentRout_AdditionalCondition`;对"可恢复的玩家友军溃兵"返回 false **阻止淡出移除**,好让它之后能被集结。敌方、或已 `Bottomed`(棘轮触底=该让它决定性崩溃)的编队则放行正常溃逃。这是温和的 phase-1 方案(更激进的 `BattleEndLogic` 闩有意搁置,以免锁死判负检查)。

---

## 12. 设置 / 可观测性 / UI(`Settings` / `Logging` / `Diagnostics` / `UI`)

- **`AnvilSettings`**(MCM v5,`AttributeGlobalSettings`,Id `AnvilAndHammerAI_v1`,`json2`):分组(按 `GroupOrder` 渲染)= 常规(0)、战场显示(1)、溃逃与集结(2)、连溃脆化(3)、防过早判负(4)、伤亡分布(5)、群体溃逃/士气(6)、编队移速(7)。**士气系统本身无独立开关——随 mod 常开**,只暴露调参与两个压力源开关。本会话新增的玩家旋钮:战场显示组「显示箭矢轨迹」、伤亡分布组「远程武器伤害倍率」(默认 0.8)、以及「挨射士气影响」% 滑条(在远程压制基线上自调)。玩家可见文本走 `{=AnvilHammer_*}` 本地化键、用游戏内白话;JSON 属性名保持稳定以兼容旧存档。
- **`Log`**:写 `Documents\Mount and Blade II Bannerlord\AnvilAndHammerAI.log`,Info 恒写、Debug 仅 `DebugLogging` 开、所有写入吞异常(日志永不崩游戏)。
- **`Telemetry` + `AnvilDiagnosticsMissionLogic`**:每 5s 刷一次 `[tele B/C][tele D][tele E][tele F][tele R]` 五行 + `[slot]/[side]/[diag]` 心跳。**指导原则:某计数器为 0 = 该子系统这 5s 没触发**——用来定位哪一层没工作。
- **`BattleNarrator`**:左下角提示(溃逃红/集结绿/战术蓝),按(编队×频道×模式)去重 + 3s 冷却;仅玩家自己的队伍出提示。
- **UI 士气填充**(UIExtenderEx):`FormationMarkerMoraleFillExtension` 在编队标记图标的 `TeamTypeWidget/Children` **Index 0**(白色兵种符号**之下**)注入一个裁剪容器,内含一张与图标同款圆形 sprite(`General\compass\target_background`)的**深灰**盘;容器高 = `(1−剩余士气)×图标高`,只露顶部那段→把"已失士气"段盖灰,下段透出原阵营色(我方青/友军绿/敌方红),白符号始终可见。`FormationMarkerMoraleMixin` 每帧读 `MoraleReadout` 并复刻原版距离缩放算高度。`AlwaysShowMarkersPatch` 在 `AlwaysShowFormationMarkers` 开时把原版 `MissionFormationMarkerVM.IsEnabled` 的 `false→true`,让标记免长按 Alt 全程显示。
- **UI 图标染色 + 溃逃闪烁**(同 `FormationMarkerMoraleMixin`,经 `FormationMarkerIconColorExtension` 给兵种符号 `FormationTypeMarker` 加 `Color` 绑定 `@FormationIconColor`):包抄步兵/重骑染金、其余白;**处于溃逃中**(`MoraleReadout.IsRouting` = `RoutLatched||Bottomed`)的编队,图标 alpha 按正弦在 `[0.15,1]` 间**渐变闪烁**(周期 ~1s),敌我皆然——一眼可辨哪支在崩。只改 alpha 保留 RGB(原版不写该 widget 的 Color)。
- **箭矢轨迹**(`MissileTrailMissionLogic`,纯渲染):沿每枚在飞导弹**真实路径**抛池化短杆段(`rts_arrow_body` + **无光照** `vertex_color_blend_no_depth_mat`(敌我同为浅灰,不因朝光/背光偏色)+ `SetFactor1` 浅灰,`EntityFlags|0x400000`+`EntityVisibilityFlags=4` 穿遮挡常显),每段抛下时调原生 `GameEntity.FadeOut(TTL)` 自动淡出、到龄回收复用;可见长 ≈ 弹速×TTL ≈ 半程。**仅 RTS/自由镜头**(`MainAgent==null || !IsPlayerControlled`)显示、玩家操控角色时隐藏,受 MCM「显示箭矢轨迹」门控,`MaxSegments` 为降帧护栏。

---

## 13. 硬约定 / 不变量(违反即破坏 RBM 共存或崩溃)

1. **绝不在 csproj 引用 RBM/SandBox**;运行时 `AccessTools.TypeByName` 反射解析并反射打补丁,缺失则记日志跳过。
2. **绝不在加载期打补丁**;经 `EnsurePatched` 在首场野战安装(先预热 `MovementOrder` cctor),否则 NRE 永久毒化该类型。
3. **共目标补丁只叠加、不替换**:`[HarmonyAfter("com.rbmcombat"/"com.rbmai")]`+`[HarmonyPriority(Priority.Last)]`,只乘最终值,绝不前缀覆盖 RBM 内层。
4. **角色 = `Formation.FormationIndex`**;`Formation` 没有 `FormationClass` 属性(同名是别的网络消息类型);骑乘攻击者的角色在骑手身上。
5. **`ScopeFilter` 门控一切,除了士气(双方)与战败安全(独立玩家侧)**。
6. **权重设定器 + 硬接管,非状态机**:`Reset`+`SetBehaviorWeight` 重申计划,并经 `BehaviorWeightOwnerPatch`/`WeightOwnership` 拦掉 vanilla/RBM 对 **mod 驱动编队**的 `WeightFactor` 写入(未接管编队不拦,绝不写呆滞);唯一 `SetControlledByAI` 是 `Drive()` 里的控制权回收。
7. **无自由混战**:`BehaviorCharge` 只在追击溃兵分支出现(且仅左右轻骑),砧永不冲锋。
8. **规模/强度一律走 `FormationStrength`(tier 加权),不比原始人头数**。
9. **士气绝不 `SetMorale(0)`**,只压到 `0.005`(>0,< 原版 panic 0.01);只降不升,集结只对在逃者且必先 `StopRetreating`。
10. **玩家可见文本只用游戏内白话**,不漏内部代号/机制术语/遥测标签;MCM 的 JSON 属性名保持稳定,组顺序用 `GroupOrder` 控制。

---

## 14. 验证

无单元测试(这是战场 AI mod)。按序验证:① 构建即部署(见 [README](../README.md) 的"从源码构建");② 在游——自定义战斗或战役野战(各子系统都 self-gate 到 `IsFieldBattle`);③ 读日志 `Documents\Mount and Blade II Bannerlord\AnvilAndHammerAI.log` 的 `[tele]`/`[slot]`/`[diag]` 心跳,按"计数器为 0 = 该层没触发"定位问题。
