namespace CardShopCoop.Api
{
    /// <summary>
    /// Base class for module-owned information attached to a game object. The generic argument is
    /// the game type the information belongs to; the identity module resolves the live object to
    /// its stable GameObjectId and stores one instance per (object, information type).
    ///
    /// The owner type must be one the identity module replicates: a <c>Customer</c>, a
    /// <c>Worker</c>, a packaging box, or an <c>InteractableObject</c> placed through the game's
    /// shelf lists (including decorations). A type outside those spaces cannot be keyed on the
    /// wire and the API rejects it.
    /// </summary>
    public abstract class ExtendGameObjectInformation<T> where T : class
    {
    }
}
