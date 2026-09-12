namespace ScreenTail.Core.Store;

/// <summary>
/// Supplies the 32-byte SQLCipher key for the local store. The Windows implementation keeps it under
/// DPAPI in the user's profile, so another Windows user (or a copied file) can't open the database.
/// Implementations must never log or persist the key in the clear.
/// </summary>
public interface IStoreKeyProvider
{
    int KeyLength => 32;

    byte[] GetKey();
}
