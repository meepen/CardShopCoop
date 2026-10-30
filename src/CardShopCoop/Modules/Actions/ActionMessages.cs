using CardShopCoop.Api;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.Actions
{
    /// <summary>
    /// The framework-owned decision sent when an action has no custom response payload: the
    /// accepted/rejected flag is all the client needs to settle its prediction.
    /// </summary>
    [NetworkMessage]
    internal sealed class ActionDecisionMessage : ActionResponseMessage
    {
    }
}
