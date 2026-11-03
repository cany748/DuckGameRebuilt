using System;
using System.Collections.Specialized;
using System.Drawing;

// Desktop-only APIs referenced by DGR that don't exist on .NET 9 for non-Windows targets.
// Callers already wrap them in try/catch, so throwing keeps the original fallback behaviour.

namespace System.Windows.Forms
{
    public class Form
    {
    }

    public class DataObject
    {
        public void SetImage(Image image) => throw new PlatformNotSupportedException();
        public void SetData(string format, bool autoConvert, object data) => throw new PlatformNotSupportedException();
    }

    public static class Clipboard
    {
        public static void SetDataObject(object data, bool copy, int retryTimes, int retryDelay) => throw new PlatformNotSupportedException();
        public static bool ContainsImage() => false;
        public static Image GetImage() => null;
        public static bool ContainsFileDropList() => false;
        public static StringCollection GetFileDropList() => new StringCollection();
        public static bool ContainsText() => false;
        public static string GetText() => "";
        public static void SetText(string text) => throw new PlatformNotSupportedException();
    }
}

namespace System.Security.Cryptography
{
    public sealed class SHA256Cng : IDisposable
    {
        private readonly SHA256 _impl = SHA256.Create();

        public byte[] ComputeHash(byte[] buffer) => _impl.ComputeHash(buffer);

        public void Dispose() => _impl.Dispose();
    }
}
