using System.Collections.Generic;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Separates temporary personal buff contributions from the mixed player
    /// GameRes that also contains shared campaign parameters.
    /// </summary>
    internal static class PersonalBuffState
    {
        public static GameRes CalculateContributions(
            IList<PlayerBuff> buffs)
        {
            var result = new GameRes();
            if (buffs == null)
                return result;

            for (int i = 0; i < buffs.Count; i++)
            {
                BuffDefinition definition = GetDefinition(buffs[i]);
                if (definition?.res == null)
                    continue;

                foreach (GameResAtom atom in definition.res.ToAtomList(1f))
                    result.Add(atom);
            }

            return result;
        }

        public static void AddContributions(
            GameRes target,
            IList<PlayerBuff> buffs)
        {
            ApplyContributions(target, buffs, subtract: false);
        }

        public static void SubtractContributions(
            GameRes target,
            IList<PlayerBuff> buffs)
        {
            ApplyContributions(target, buffs, subtract: true);
        }

        private static void ApplyContributions(
            GameRes target,
            IList<PlayerBuff> buffs,
            bool subtract)
        {
            if (target == null || buffs == null)
                return;

            for (int i = 0; i < buffs.Count; i++)
            {
                BuffDefinition definition = GetDefinition(buffs[i]);
                if (definition?.res == null)
                    continue;

                foreach (GameResAtom atom in definition.res.ToAtomList(1f))
                {
                    if (subtract)
                        target.Sub(atom);
                    else
                        target.Add(atom);
                }
            }
        }

        private static BuffDefinition GetDefinition(PlayerBuff buff)
        {
            if (buff == null || string.IsNullOrEmpty(buff.buff_id) ||
                GameBalance.me == null)
            {
                return null;
            }

            try
            {
                return GameBalance.me.GetData<BuffDefinition>(buff.buff_id);
            }
            catch
            {
                return null;
            }
        }
    }
}
