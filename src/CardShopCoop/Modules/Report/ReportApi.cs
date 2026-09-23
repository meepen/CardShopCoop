namespace CardShopCoop.Modules.Report
{
    internal static class ReportApi
    {
        internal static void NotifyCommittedPurchaseMutation()
            => ReportHostBehaviour.NotifyCommittedPurchaseMutation();
    }
}
