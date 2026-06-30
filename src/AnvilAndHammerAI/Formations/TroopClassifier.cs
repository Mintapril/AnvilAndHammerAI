using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace AnvilAndHammerAI.Formations
{
    /// <summary>
    /// 七编队角色(ADR-0011)。每个角色映射到一个**不同的** FormationClass 槽——
    /// 不建第二个 Cavalry 槽(守 ADR-0007 字面约束),改用 Cavalry/LightCavalry/HeavyCavalry
    /// 与 Infantry/HeavyInfantry 等不同 class 槽承载。
    /// </summary>
    public enum TacRole
    {
        Skip,
        Archer,         // Ranged(1)
        InfantryMain,   // Infantry(0)        —— 砧·主线
        InfantryFlank,  // HeavyInfantry(4)   —— 包抄
        HorseArcher,    // HorseArcher(3)
        LightCavLeft,   // LightCavalry(5)    —— 左翼轻骑
        LightCavRight,  // Cavalry(2)         —— 右翼轻骑
        HeavyCav        // HeavyCavalry(6)    —— 锤·决定性冲击
    }

    /// <summary>粗分类(未含左右/主侧均衡;均衡由管理器按当前编队人数决定)。</summary>
    public enum TacCategory { Skip, Archer, Infantry, HorseArcher, LightCav, HeavyCav }

    public static class TroopClassifier
    {
        /// <summary>tier ≥ 此值 = 重骑(锤);其余 = 轻骑。用户锁定:轻 1-5 / 重 6
        /// (仅顶级精英为锤;tier5 归轻骑两翼,使全精英骑兵军仍能分出左右轻骑两翼)。</summary>
        public const int HeavyCavMinTier = 6;

        /// <summary>角色 → FormationClass 槽。</summary>
        public static FormationClass SlotFor(TacRole r)
        {
            switch (r)
            {
                case TacRole.Archer: return FormationClass.Ranged;
                case TacRole.InfantryMain: return FormationClass.Infantry;
                case TacRole.InfantryFlank: return FormationClass.HeavyInfantry;
                case TacRole.HorseArcher: return FormationClass.HorseArcher;
                case TacRole.LightCavLeft: return FormationClass.LightCavalry;
                case TacRole.LightCavRight: return FormationClass.Cavalry;
                case TacRole.HeavyCav: return FormationClass.HeavyCavalry;
                default: return FormationClass.NumberOfRegularFormations; // 哨兵(Skip 不应走到这里)
            }
        }

        /// <summary>
        /// 粗分类:骑乘 + 远程 → 骑射;骑乘近战 → 按 tier 分轻/重;步行远程 → 弓兵;步行近战 → 步兵。
        /// 远程判定用引擎给的 DefaultFormationClass(避免在生成期做易错的弹药扫描)。
        /// 注:"骑射弹尽→轻骑"的动态转换是 §A 锁定项,作为 step1 后续细化(标 TODO),首版先把编队拉起来。
        /// </summary>
        public static TacCategory Categorize(Agent a)
        {
            if (a == null || !a.IsHuman) return TacCategory.Skip;
            bool mounted = a.HasMount;
            FormationClass dfc = a.Character != null ? a.Character.DefaultFormationClass : FormationClass.Infantry;
            bool ranged = dfc == FormationClass.Ranged || dfc == FormationClass.HorseArcher;

            if (mounted && ranged) return TacCategory.HorseArcher;
            if (mounted)
            {
                int tier = a.Character != null ? a.Character.GetBattleTier() : 0;
                return tier >= HeavyCavMinTier ? TacCategory.HeavyCav : TacCategory.LightCav;
            }
            if (ranged) return TacCategory.Archer;
            return TacCategory.Infantry;
        }

        /// <summary>
        /// 骑射是否已打空(§A 锁定:"无箭后并入任意一支轻骑")。遍历武器槽,
        /// 有过远程武器但所有远程武器弹药都为 0 → true。用 RBM 同款 GetAmmoAmount(见 Tactics.cs:530)。
        /// </summary>
        public static bool MountedOutOfAmmo(Agent a)
        {
            if (a == null || !a.HasMount) return false;
            MissionEquipment eq = a.Equipment;
            if (eq == null) return false;
            bool hadRanged = false;
            for (EquipmentIndex i = EquipmentIndex.Weapon0; i <= EquipmentIndex.ExtraWeaponSlot; i++)
            {
                MissionWeapon w = eq[i];
                if (w.IsEmpty) continue;
                WeaponComponentData item = w.CurrentUsageItem;
                if (item != null && item.IsRangedWeapon)
                {
                    hadRanged = true;
                    if (eq.GetAmmoAmount(i) > 0) return false; // 还有弹
                }
            }
            return hadRanged;
        }
    }
}
