using AnvilAndHammerAI.Morale;
using MCM.Abstractions.Attributes;
using MCM.Abstractions.Attributes.v2;
using MCM.Abstractions.Base.Global;
using TaleWorlds.Localization;

namespace AnvilAndHammerAI.Settings
{
    /// <summary>
    /// MCM 设置。AttributeGlobalSettings 由 MCM v5 自动发现注册。
    /// 玩家可见文案全部走本地化键 <c>{=AnvilHammer_xxx}English default</c>:英文默认内联(英文玩家直接显示),
    /// 简体中文等其它语言由 ModuleData/Languages 下对应语言表覆盖(见 std_anvilhammer_strings*.xml)。
    /// MCM v5 的显示名 / 提示 / 分组名均支持 {=键} 语法;DisplayName 为普通属性,手动解析为本地化串。
    /// 注:属性名(JSON 键)保持不变,旧存档仍可加载;分组顺序由 GroupOrder 控制。
    /// </summary>
    public sealed class AnvilSettings : AttributeGlobalSettings<AnvilSettings>
    {
        public override string Id => "AnvilAndHammerAI_v1";
        public override string DisplayName => new TextObject("{=AnvilHammer_mod_display_name}Anvil & Hammer - Cavalry & Morale Battle AI").ToString();
        public override string FolderName => "AnvilAndHammerAI";
        public override string FormatType => "json2";

        // ---------------- 通用 ----------------
        [SettingPropertyBool("{=AnvilHammer_enabled}Enable Mod", Order = 0, RequireRestart = false, HintText = "{=AnvilHammer_enabled_hint}Master toggle. When disabled, this mod no longer changes any battle behavior.")]
        [SettingPropertyGroup("{=AnvilHammer_group_general}General", GroupOrder = 0)]
        public bool Enabled { get; set; } = true;

        [SettingPropertyBool("{=AnvilHammer_debug_logging}Write Diagnostic Log", Order = 1, RequireRestart = false, HintText = "{=AnvilHammer_debug_logging_hint}Write detailed runtime info to the log file for troubleshooting. Usually keep this off.")]
        [SettingPropertyGroup("{=AnvilHammer_group_general}General", GroupOrder = 0)]
        public bool DebugLogging { get; set; } = false;

        [SettingPropertyBool("{=AnvilHammer_player_army_only}Affect Only My Army", Order = 2, RequireRestart = false, HintText = "{=AnvilHammer_player_army_only_hint}Command only your army; enemy forces fall back to vanilla/RBM. The one exception is formation-level morale, which always affects both sides. Default off (both sides affected).")]
        [SettingPropertyGroup("{=AnvilHammer_group_general}General", GroupOrder = 0)]
        public bool PlayerArmyOnly { get; set; } = false;

        [SettingPropertyBool("{=AnvilHammer_auto_formation}Auto Formation (Experimental)", Order = 3, RequireRestart = false, HintText = "{=AnvilHammer_auto_formation_hint}Organize the army into 7 role formations (main inf / flank inf / archers / horse archers / left light cav / right light cav / heavy cav) as the base for unified command. Default on.")]
        [SettingPropertyGroup("{=AnvilHammer_group_general}General", GroupOrder = 0)]
        public bool AutoFormationEnabled { get; set; } = true;

        [SettingPropertyBool("{=AnvilHammer_command_scheduler}Unified Battle Command (Experimental)", Order = 4, RequireRestart = false, HintText = "{=AnvilHammer_command_scheduler_hint}A unified commander directs each formation to run anvil-and-hammer tactics (infantry hold, archer/horse-archer skirmish, light cavalry screen/flank, timed heavy cavalry charge). Requires Auto Formation. Default on.")]
        [SettingPropertyGroup("{=AnvilHammer_group_general}General", GroupOrder = 0)]
        public bool CommandSchedulerEnabled { get; set; } = true;

        [SettingPropertyBool("{=AnvilHammer_threat_reactions}Threat Reactions (Experimental)", Order = 5, RequireRestart = false, HintText = "{=AnvilHammer_threat_reactions_hint}React to unexpected threats on their own: infantry form a shield wall against cavalry, archers/horse archers fall back, reserve cavalry wheel about to counter-charge. Requires Unified Battle Command. Default on.")]
        [SettingPropertyGroup("{=AnvilHammer_group_general}General", GroupOrder = 0)]
        public bool ThreatReactionsEnabled { get; set; } = true;

        [SettingPropertyBool("{=AnvilHammer_respect_player_orders}Yield Formations You Command", Order = 6, RequireRestart = false, HintText = "{=AnvilHammer_respect_player_orders_hint}When on, formations you command yourself are not overridden by unified command. Keep this off when running RTS Camera, or formations it takes over may stand idle. Default off (the mod runs tactics throughout).")]
        [SettingPropertyGroup("{=AnvilHammer_group_general}General", GroupOrder = 0)]
        public bool RespectPlayerOrders { get; set; } = false;

        [SettingPropertyBool("{=AnvilHammer_battle_event_log}Battle Event Toasts", Order = 7, RequireRestart = false, HintText = "{=AnvilHammer_battle_event_log_hint}Show key battle events in the lower-left (heavy cavalry charge, light cavalry cover/flank, infantry outflank, rout/rally, etc.) to help you read the AI. Default on.")]
        [SettingPropertyGroup("{=AnvilHammer_group_general}General", GroupOrder = 0)]
        public bool BattleEventLog { get; set; } = true;

        // ---------------- 战场显示 ----------------
        [SettingPropertyBool("{=AnvilHammer_show_morale_bars}Show Morale on Formation Markers", Order = 0, RequireRestart = false, HintText = "{=AnvilHammer_show_morale_bars_hint}Fill each formation's troop-type icon with its team colour in proportion to its morale; the lost part turns dark grey (hold Alt to see the markers, or turn on Always Show Formation Markers below). Shown for both your formations and the enemy's, and drains as a formation nears a mass rout. Default on.")]
        [SettingPropertyGroup("{=AnvilHammer_group_battle_ui}Battle Display", GroupOrder = 1)]
        public bool ShowMoraleBars { get; set; } = true;

        [SettingPropertyBool("{=AnvilHammer_always_show_markers}Always Show Formation Markers", Order = 1, RequireRestart = false, HintText = "{=AnvilHammer_always_show_markers_hint}Keep the formation markers (the troop-type icons above each formation) visible at all times, without holding Alt. Default off.")]
        [SettingPropertyGroup("{=AnvilHammer_group_battle_ui}Battle Display", GroupOrder = 1)]
        public bool AlwaysShowFormationMarkers { get; set; } = false;

        [SettingPropertyBool("{=AnvilHammer_show_missile_trails}Show Arrow Trails", Order = 2, RequireRestart = false, HintText = "{=AnvilHammer_show_missile_trails_hint}Draw a light-grey trail along every arrow, bolt and javelin in flight, kept clearly visible even zoomed out and from the RTS overview (unlike the vanilla trail, which disappears at a distance). Default on.")]
        [SettingPropertyGroup("{=AnvilHammer_group_battle_ui}Battle Display", GroupOrder = 1)]
        public bool ShowMissileTrails { get; set; } = true;

        // ---------------- 溃逃与集结 ----------------
        [SettingPropertyFloatingInteger("{=AnvilHammer_rally_delay}Rally Delay (sec)", 0.5f, 30f, "0.0", Order = 0, RequireRestart = false, HintText = "{=AnvilHammer_rally_delay_hint}How long after a formation routs before it tries to steady and rally. Too short = pulled back the instant it routs; too long = can never recover. Default 20.")]
        [SettingPropertyGroup("{=AnvilHammer_group_rout_rally}Rout & Rally", GroupOrder = 2)]
        public float RallyDelaySeconds { get; set; } = 20f;

        [SettingPropertyFloatingInteger("{=AnvilHammer_rally_morale_floor}Rally Morale", 5f, 80f, "0.0", Order = 1, RequireRestart = false, HintText = "{=AnvilHammer_rally_morale_floor_hint}The morale a formation is restored to when it rallies. Higher = steadier and less likely to rout again at once. Default 25.")]
        [SettingPropertyGroup("{=AnvilHammer_group_rout_rally}Rout & Rally", GroupOrder = 2)]
        public float RallyMoraleFloor { get; set; } = 25f;

        // ---------------- 越打越脆 ----------------
        [SettingPropertyBool("{=AnvilHammer_ratchet_enabled}Weaker After Repeated Routs", Order = 0, RequireRestart = false, HintText = "{=AnvilHammer_ratchet_enabled_hint}Each rout permanently lowers the morale a unit can later recover to (it grows brittle); past a point it collapses for good and no longer rallies.")]
        [SettingPropertyGroup("{=AnvilHammer_group_ratchet}Fracture on Repeated Routs", GroupOrder = 3)]
        public bool RatchetEnabled { get; set; } = true;

        [SettingPropertyFloatingInteger("{=AnvilHammer_ratchet_decay}Fragility Per Rout", 0.3f, 1f, "0.00", Order = 1, RequireRestart = false, HintText = "{=AnvilHammer_ratchet_decay_hint}Each rout multiplies the morale-recovery cap by this factor (lower = collapses for good sooner). Set to 1 for no fragility. Default 0.55.")]
        [SettingPropertyGroup("{=AnvilHammer_group_ratchet}Fracture on Repeated Routs", GroupOrder = 3)]
        public float RatchetDecayPerBreak { get; set; } = 0.55f;

        // ---------------- 战败判定保护 ----------------
        [SettingPropertyBool("{=AnvilHammer_safety_enabled}Prevent Premature Defeat", Order = 0, RequireRestart = false, HintText = "{=AnvilHammer_safety_enabled_hint}When your troops rout temporarily, stop the game from declaring defeat too early: routers that can still recover do not leave the field at once. Fully collapsed units and the enemy are not protected.")]
        [SettingPropertyGroup("{=AnvilHammer_group_safety}Defeat Prevention", GroupOrder = 4)]
        public bool SafetyEnabled { get; set; } = true;

        // ---------------- 伤亡分布 ----------------
        [SettingPropertyBool("{=AnvilHammer_casualty_enabled}Concentrate Casualties in Pursuit", Order = 0, RequireRestart = false, HintText = "{=AnvilHammer_casualty_enabled_hint}Make casualties fall more in the pursuit and less in the head-on fight: hits on fleeing or rear-facing enemies deal more, standing toe-to-toe deals less.")]
        [SettingPropertyGroup("{=AnvilHammer_group_casualty}Casualty Distribution", GroupOrder = 5)]
        public bool CasualtyEnabled { get; set; } = true;

        [SettingPropertyFloatingInteger("{=AnvilHammer_stand_multiplier}Head-on Melee Damage Multiplier", 0.5f, 1f, "0.00", Order = 1, RequireRestart = false, HintText = "{=AnvilHammer_stand_multiplier_hint}Damage multiplier when standing toe-to-toe. Lower = slower kills head-on. Default 0.65.")]
        [SettingPropertyGroup("{=AnvilHammer_group_casualty}Casualty Distribution", GroupOrder = 5)]
        public float StandMultiplier { get; set; } = 0.65f;

        [SettingPropertyFloatingInteger("{=AnvilHammer_pursuit_multiplier}Pursuit Damage Multiplier", 1f, 4f, "0.0", Order = 2, RequireRestart = false, HintText = "{=AnvilHammer_pursuit_multiplier_hint}Damage multiplier when hitting a fleeing enemy. Higher = more thorough pursuit kills. Default 1.7.")]
        [SettingPropertyGroup("{=AnvilHammer_group_casualty}Casualty Distribution", GroupOrder = 5)]
        public float PursuitMultiplier { get; set; } = 1.7f;

        [SettingPropertyFloatingInteger("{=AnvilHammer_rear_multiplier}Rear Attack Damage Multiplier", 1f, 4f, "0.0", Order = 3, RequireRestart = false, HintText = "{=AnvilHammer_rear_multiplier_hint}Damage multiplier when hitting a (non-fleeing) enemy from behind. Default 1.5.")]
        [SettingPropertyGroup("{=AnvilHammer_group_casualty}Casualty Distribution", GroupOrder = 5)]
        public float RearMultiplier { get; set; } = 1.5f;

        [SettingPropertyBool("{=AnvilHammer_pursuit_guaranteed_kill}Pursuit One-Shot Kill (Aggressive)", Order = 4, RequireRestart = false, HintText = "{=AnvilHammer_pursuit_guaranteed_kill_hint}Hits on a fleeing enemy deal lethal damage outright (one-shot). Aggressive; off by default.")]
        [SettingPropertyGroup("{=AnvilHammer_group_casualty}Casualty Distribution", GroupOrder = 5)]
        public bool PursuitGuaranteedKill { get; set; } = false;

        [SettingPropertyFloatingInteger("{=AnvilHammer_side_multiplier}Side Attack Damage Multiplier", 0.8f, 2.5f, "0.00", Order = 5, RequireRestart = false, HintText = "{=AnvilHammer_side_multiplier_hint}Damage multiplier when hitting a (non-fleeing) enemy from the side. Direction is judged by the enemy formation's facing. Default 1.0 (no change).")]
        [SettingPropertyGroup("{=AnvilHammer_group_casualty}Casualty Distribution", GroupOrder = 5)]
        public float SideMultiplier { get; set; } = 1.0f;

        [SettingPropertyFloatingInteger("{=AnvilHammer_flank_rear_multiplier}Flank/Cavalry Rear Damage Multiplier", 1f, 4f, "0.00", Order = 6, RequireRestart = false, HintText = "{=AnvilHammer_flank_rear_multiplier_hint}Damage multiplier when flank infantry, light cavalry or heavy cavalry strike an enemy formation from behind (replaces the generic rear multiplier; these roles excel at getting around). Default 1.8.")]
        [SettingPropertyGroup("{=AnvilHammer_group_casualty}Casualty Distribution", GroupOrder = 5)]
        public float FlankRearMultiplier { get; set; } = 1.8f;

        [SettingPropertyFloatingInteger("{=AnvilHammer_flank_side_multiplier}Flank/Cavalry Side Damage Multiplier", 1f, 3f, "0.00", Order = 7, RequireRestart = false, HintText = "{=AnvilHammer_flank_side_multiplier_hint}Damage multiplier when flank infantry, light cavalry or heavy cavalry strike an enemy formation from the side (replaces the generic side multiplier). Default 1.3.")]
        [SettingPropertyGroup("{=AnvilHammer_group_casualty}Casualty Distribution", GroupOrder = 5)]
        public float FlankSideMultiplier { get; set; } = 1.3f;

        [SettingPropertyBool("{=AnvilHammer_charge_momentum_enabled}Scale Cavalry Impact by Mount Tier", Order = 8, RequireRestart = false, HintText = "{=AnvilHammer_charge_momentum_enabled_hint}High-speed cavalry collision (impact) damage scales with mount tier: light cavalry lower, heavy cavalry higher. Multiplied on top of the direction multiplier. Default on.")]
        [SettingPropertyGroup("{=AnvilHammer_group_casualty}Casualty Distribution", GroupOrder = 5)]
        public bool ChargeMomentumEnabled { get; set; } = true;

        [SettingPropertyFloatingInteger("{=AnvilHammer_light_cav_charge_mult}Light Cavalry Impact Multiplier", 0.3f, 2f, "0.00", Order = 9, RequireRestart = false, HintText = "{=AnvilHammer_light_cav_charge_mult_hint}Impact damage multiplier for a light cavalry charge. Light cavalry carries less momentum. Default 0.8.")]
        [SettingPropertyGroup("{=AnvilHammer_group_casualty}Casualty Distribution", GroupOrder = 5)]
        public float LightCavChargeMult { get; set; } = 0.8f;

        [SettingPropertyFloatingInteger("{=AnvilHammer_heavy_cav_charge_mult}Heavy Cavalry Impact Multiplier", 1f, 5f, "0.00", Order = 10, RequireRestart = false, HintText = "{=AnvilHammer_heavy_cav_charge_mult_hint}Impact damage multiplier for a heavy cavalry charge. Heavy cavalry carries great momentum. Default 1.7.")]
        [SettingPropertyGroup("{=AnvilHammer_group_casualty}Casualty Distribution", GroupOrder = 5)]
        public float HeavyCavChargeMult { get; set; } = 1.7f;

        [SettingPropertyBool("{=AnvilHammer_heavy_cav_plow_through}Heavy Cavalry Plow Through", Order = 11, RequireRestart = false, HintText = "{=AnvilHammer_heavy_cav_plow_through_hint}A heavy cavalry charge knocks frontal enemies to the ground, keeping momentum to punch through the line (not halted by standing soldiers, less slowdown). Reflects heavy momentum; off, cavalry is more easily stopped after impact. Only your heavy cavalry, and only knocks down enemies. Default on.")]
        [SettingPropertyGroup("{=AnvilHammer_group_casualty}Casualty Distribution", GroupOrder = 5)]
        public bool HeavyCavPlowThrough { get; set; } = true;

        [SettingPropertyFloatingInteger("{=AnvilHammer_ranged_damage_multiplier}Ranged Weapon Damage Multiplier", 0.2f, 1.5f, "0.00", Order = 12, RequireRestart = false, HintText = "{=AnvilHammer_ranged_damage_multiplier_hint}Damage multiplier for hits from bows, crossbows and thrown weapons (arrows, bolts, javelins). Lower = ranged weapons deal less damage. Default 0.8.")]
        [SettingPropertyGroup("{=AnvilHammer_group_casualty}Casualty Distribution", GroupOrder = 5)]
        public float RangedDamageMultiplier { get; set; } = 0.8f;

        // ---------------- 编队速度 ----------------
        [SettingPropertyBool("{=AnvilHammer_formation_speed_enabled}Adjust Speed by Formation", Order = 0, RequireRestart = false, HintText = "{=AnvilHammer_formation_speed_enabled_hint}Fine-tune speed by role: flank infantry faster to get around, light cavalry and horse archers faster to maneuver, heavy cavalry a touch slower. Off = vanilla speeds. Default on.")]
        [SettingPropertyGroup("{=AnvilHammer_group_speed}Formation Speed", GroupOrder = 7)]
        public bool FormationSpeedEnabled { get; set; } = true;

        [SettingPropertyFloatingInteger("{=AnvilHammer_flank_infantry_speed_mult}Flank Infantry Speed Multiplier", 0.8f, 1.5f, "0.00", Order = 1, RequireRestart = false, HintText = "{=AnvilHammer_flank_infantry_speed_mult_hint}Movement-speed multiplier for flank infantry (to get around to the enemy's side/rear). Default 1.25.")]
        [SettingPropertyGroup("{=AnvilHammer_group_speed}Formation Speed", GroupOrder = 7)]
        public float FlankInfantrySpeedMult { get; set; } = 1.25f;

        [SettingPropertyFloatingInteger("{=AnvilHammer_light_cav_speed_mult}Light Cavalry Speed Multiplier", 0.8f, 1.5f, "0.00", Order = 2, RequireRestart = false, HintText = "{=AnvilHammer_light_cav_speed_mult_hint}Charge-speed multiplier for light cavalry. Default 1.1.")]
        [SettingPropertyGroup("{=AnvilHammer_group_speed}Formation Speed", GroupOrder = 7)]
        public float LightCavSpeedMult { get; set; } = 1.1f;

        [SettingPropertyFloatingInteger("{=AnvilHammer_horse_archer_speed_mult}Horse Archer Speed Multiplier", 0.8f, 1.5f, "0.00", Order = 3, RequireRestart = false, HintText = "{=AnvilHammer_horse_archer_speed_mult_hint}Charge-speed multiplier for horse archers (to help them kite). Default 1.15.")]
        [SettingPropertyGroup("{=AnvilHammer_group_speed}Formation Speed", GroupOrder = 7)]
        public float HorseArcherSpeedMult { get; set; } = 1.15f;

        [SettingPropertyFloatingInteger("{=AnvilHammer_heavy_cav_speed_mult}Heavy Cavalry Speed Multiplier", 0.6f, 1.2f, "0.00", Order = 4, RequireRestart = false, HintText = "{=AnvilHammer_heavy_cav_speed_mult_hint}Charge-speed multiplier for heavy cavalry. Heavy armor moves slower. Default 0.8.")]
        [SettingPropertyGroup("{=AnvilHammer_group_speed}Formation Speed", GroupOrder = 7)]
        public float HeavyCavSpeedMult { get; set; } = 0.8f;

        // ---------------- 成片溃逃(编队级士气系统;随本 Mod 常开,无独立开关) ----------------
        [SettingPropertyFloatingInteger("{=AnvilHammer_pool_rout_threshold}Formation Rout Pressure Threshold", 5f, 120f, "0.0", Order = 1, RequireRestart = false, HintText = "{=AnvilHammer_pool_rout_threshold_hint}A formation routs as a whole once its pressure exceeds this. Lower = easier mass collapse. Default 30.")]
        [SettingPropertyGroup("{=AnvilHammer_group_morale}Mass Rout (Morale)", GroupOrder = 6)]
        public float PoolRoutThreshold { get; set; } = MoraleTuning.PoolRoutThresholdDefault;

        [SettingPropertyFloatingInteger("{=AnvilHammer_pool_decay_per_second}Pressure Decay (per sec)", 1f, 30f, "0.0", Order = 2, RequireRestart = false, HintText = "{=AnvilHammer_pool_decay_per_second_hint}How fast the pressure from being under fire or surrounded fades each second. Higher = fades faster. (Pressure from casualties stays as a lasting morale loss and does not fade.) Default 3.")]
        [SettingPropertyGroup("{=AnvilHammer_group_morale}Mass Rout (Morale)", GroupOrder = 6)]
        public float PoolDecayPerSecond { get; set; } = MoraleTuning.PoolDecayPerSecondDefault;

        [SettingPropertyBool("{=AnvilHammer_ranged_pressure_enabled}Pressure Source: Under Fire", Order = 3, RequireRestart = false, HintText = "{=AnvilHammer_ranged_pressure_enabled_hint}Count being volleyed by arrows and javelins toward rout pressure (shield hits, body hits, and arrows landing nearby all count). Default on.")]
        [SettingPropertyGroup("{=AnvilHammer_group_morale}Mass Rout (Morale)", GroupOrder = 6)]
        public bool RangedPressureEnabled { get; set; } = true;

        [SettingPropertyFloatingInteger("{=AnvilHammer_ranged_pressure_intensity}Under-Fire Morale Impact (%)", 0f, 300f, "0", Order = 5, RequireRestart = false, HintText = "{=AnvilHammer_ranged_pressure_intensity_hint}How strongly being shot at saps a formation toward a mass rout. 100% is the (already reduced) default; lower it if arrows rattle your troops too much, raise it to make massed archery break formations faster. Set 0% to remove rout pressure from fire entirely.")]
        [SettingPropertyGroup("{=AnvilHammer_group_morale}Mass Rout (Morale)", GroupOrder = 6)]
        public float RangedPressureIntensity { get; set; } = 100f;

        [SettingPropertyBool("{=AnvilHammer_charge_shock_pressure_enabled}Pressure Source: Enemy Cavalry Charge", Order = 4, RequireRestart = false, HintText = "{=AnvilHammer_charge_shock_pressure_enabled_hint}Count enemy cavalry charges that crash into a formation toward rout pressure — hits from the flank or rear shake it far more than a head-on charge, and heavy cavalry hit harder than light. Default on.")]
        [SettingPropertyGroup("{=AnvilHammer_group_morale}Mass Rout (Morale)", GroupOrder = 6)]
        public bool ChargeShockPressureEnabled { get; set; } = true;

        /// <summary>诊断用(写日志,非玩家可见):一行打印当前全部生效参数。</summary>
        public string Snapshot() =>
            $"Enabled={Enabled} Debug={DebugLogging} playerOnly={PlayerArmyOnly} autoForm={AutoFormationEnabled} sched={CommandSchedulerEnabled} react={ThreatReactionsEnabled} " +
            $"| ui(moraleBars={ShowMoraleBars} alwaysMarkers={AlwaysShowFormationMarkers}) " +
            $"| rally delay={RallyDelaySeconds:0.0} floor={RallyMoraleFloor:0.0} " +
            $"| ratchet={RatchetEnabled} decay={RatchetDecayPerBreak:0.00} " +
            $"| safety={SafetyEnabled} " +
            $"| casualty={CasualtyEnabled} stand={StandMultiplier:0.00} pursuit={PursuitMultiplier:0.0} rear={RearMultiplier:0.0} kill={PursuitGuaranteedKill} " +
            $"| poolThr={PoolRoutThreshold:0.0} poolDecay={PoolDecayPerSecond:0.0} ranged={RangedPressureEnabled}/{RangedPressureIntensity:0}% charge={ChargeShockPressureEnabled} " +
            $"| respectPlayer={RespectPlayerOrders} side={SideMultiplier:0.00} flankRear={FlankRearMultiplier:0.00} flankSide={FlankSideMultiplier:0.00} cavCharge={ChargeMomentumEnabled}(l={LightCavChargeMult:0.00} h={HeavyCavChargeMult:0.00} plow={HeavyCavPlowThrough}) " +
            $"| speed={FormationSpeedEnabled}(fInf={FlankInfantrySpeedMult:0.00} lcav={LightCavSpeedMult:0.00} ha={HorseArcherSpeedMult:0.00} hcav={HeavyCavSpeedMult:0.00})";
    }
}
