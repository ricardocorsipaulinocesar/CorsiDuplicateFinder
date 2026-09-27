using CorsiDuplicate.Core.Abstractions;
using CorsiDuplicate.Infrastructure.Logging;
using Microsoft.VisualBasic.FileIO;

namespace CorsiDuplicate.Infrastructure.IO;

public sealed class RecycleBinService : IRecycleBinService
{
    public bool TrySendToRecycleBin(string fullPath, out string? error)
    {
        try
        {
            FileSystem.DeleteFile(fullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            AppLogger.Error(nameof(RecycleBinService), nameof(TrySendToRecycleBin), $"Failed to recycle '{fullPath}'.", ex);
            error = ex.Message;
            return false;
        }
    }
}
