namespace System.Runtime.CompilerServices
{
    // Enables C# 9 init accessors on net472. The compiler only needs the type to exist in the
    // declaring assembly; the runtime treats the setter as an ordinary method.
    internal static class IsExternalInit
    {
    }
}
