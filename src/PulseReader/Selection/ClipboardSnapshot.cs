using System.Collections.Specialized;
using System.Drawing;
using System.Runtime.InteropServices;

namespace PulseReader.Selection;

internal sealed class ClipboardSnapshot : IClipboardCaptureSnapshot
{
    private readonly List<IDisposable> _ownedResources;

    private ClipboardSnapshot(
        DataObject? data,
        List<IDisposable> ownedResources,
        bool isComplete)
    {
        Data = data;
        _ownedResources = ownedResources;
        IsComplete = isComplete;
    }

    public DataObject? Data { get; }

    public bool IsComplete { get; }

    public static bool TryCapture(out ClipboardSnapshot? snapshot)
    {
        snapshot = null;
        var source = Clipboard.GetDataObject();

        if (source is null)
        {
            snapshot = new ClipboardSnapshot(null, [], true);
            return true;
        }

        var formats = source.GetFormats(false);
        if (formats.Length == 0)
        {
            snapshot = new ClipboardSnapshot(null, [], true);
            return true;
        }

        var copy = new DataObject();
        var copiedFormatCount = 0;
        var isComplete = true;
        var ownedResources = new List<IDisposable>();

        foreach (var format in formats)
        {
            try
            {
                var data = source.GetData(format, false);
                if (data is null)
                {
                    continue;
                }

                var clonedData = CloneClipboardValue(data, out var independentlyOwned);
                if (!independentlyOwned)
                {
                    isComplete = false;
                }

                if (clonedData is IDisposable disposable && !ReferenceEquals(clonedData, data))
                {
                    ownedResources.Add(disposable);
                }

                copy.SetData(format, false, clonedData);
                copiedFormatCount++;
            }
            catch (Exception exception) when (exception is ExternalException
                                              or InvalidOperationException
                                              or ArgumentException
                                              or IOException
                                              or NotSupportedException)
            {
                // Some application-specific formats cannot be materialized. Other formats are still preserved.
                isComplete = false;
            }
        }

        if (copiedFormatCount == 0)
        {
            foreach (var resource in ownedResources)
            {
                resource.Dispose();
            }

            return false;
        }

        snapshot = new ClipboardSnapshot(copy, ownedResources, isComplete);
        return true;
    }

    private static object CloneClipboardValue(object data, out bool independentlyOwned)
    {
        if (data is Stream stream)
        {
            var originalPosition = stream.CanSeek ? stream.Position : 0;
            var copy = new MemoryStream();

            try
            {
                if (stream.CanSeek)
                {
                    stream.Position = 0;
                }

                stream.CopyTo(copy);
                copy.Position = 0;
                independentlyOwned = true;
                return copy;
            }
            catch
            {
                copy.Dispose();
                throw;
            }
            finally
            {
                if (stream.CanSeek)
                {
                    stream.Position = originalPosition;
                }
            }
        }

        if (data is Image image)
        {
            independentlyOwned = true;
            return image.Clone();
        }

        if (data is StringCollection strings)
        {
            var copy = new StringCollection();
            copy.AddRange(strings.Cast<string>().ToArray());
            independentlyOwned = true;
            return copy;
        }

        if (data is byte[] bytes)
        {
            independentlyOwned = true;
            return bytes.ToArray();
        }

        if (data is string[] stringArray)
        {
            independentlyOwned = true;
            return stringArray.ToArray();
        }

        if (data is string || data.GetType().IsValueType)
        {
            independentlyOwned = true;
            return data;
        }

        independentlyOwned = false;
        return data;
    }

    public void Restore()
    {
        if (Data is null)
        {
            Clipboard.Clear();
        }
        else
        {
            Clipboard.SetDataObject(Data, true);
        }
    }

    public void Dispose()
    {
        foreach (var resource in _ownedResources)
        {
            resource.Dispose();
        }

        _ownedResources.Clear();
    }
}
