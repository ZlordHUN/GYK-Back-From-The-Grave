namespace GraveyardKeeperCoop.Patches
{
    internal enum DeferredDialogueOpType
    {
        Advance,
        Choice
    }

    internal sealed class DeferredDialogueOp
    {
        public DeferredDialogueOpType Type;
        public int ChoiceIndex;
        public string ChoiceText;
    }
}
