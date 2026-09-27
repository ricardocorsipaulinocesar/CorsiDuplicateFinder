namespace CorsiDuplicate.Core.Abstractions;

public interface IRecycleBinService
{
    /// <summary>
    /// Moves the file at the given path to the Recycle Bin (never a permanent delete).
    /// Returns false with an error message on failure instead of throwing, so a bulk
    /// delete can continue past individual failures and report them together.
    /// </summary>
    bool TrySendToRecycleBin(string fullPath, out string? error);
}
